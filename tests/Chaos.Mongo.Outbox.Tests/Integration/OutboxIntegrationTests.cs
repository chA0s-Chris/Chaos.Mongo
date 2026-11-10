// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox.Tests.Integration;

using Chaos.Mongo.Configuration;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using NUnit.Framework;
using System.Collections.Concurrent;
using System.Diagnostics;
using Testcontainers.MongoDb;

[Category("Integration")]
public class OutboxIntegrationTests
{
    private MongoDbContainer _container;
    private IMongoHelper _mongoHelper;
    private OutboxOptions _options;
    private IOutbox _outbox;
    private ServiceProvider _serviceProvider;

    [SetUp]
    public async Task Setup()
    {
        _container = await MongoDbTestContainer.StartContainerAsync();
        var url = MongoUrl.Create(_container.GetConnectionString());
        _serviceProvider = new ServiceCollection()
                           .AddLogging(l => l.SetMinimumLevel(LogLevel.Debug))
                           .AddMongo(url, configure: options =>
                           {
                               options.DefaultDatabase = $"OutboxTestDb_{Guid.NewGuid():N}";
                               options.RunConfiguratorsOnStartup = false;
                           })
                           .WithOutbox(o => o
                                            .WithPublisher<TestOutboxPublisher>(ServiceLifetime.Singleton)
                                            .WithMessage<TestPayload>("TestPayload")
                                            .WithMessage<AnotherTestPayload>("AnotherPayload")
                                            .WithMaxRetries(3)
                                            .WithPollingInterval(TimeSpan.FromMilliseconds(100))
                                            .WithLockLeaseTime(TimeSpan.FromSeconds(10))
                                            .WithBatchSize(10))
                           .Services
                           .BuildServiceProvider();

        _mongoHelper = _serviceProvider.GetRequiredService<IMongoHelper>();
        _outbox = _serviceProvider.GetRequiredService<IOutbox>();
        _options = _serviceProvider.GetRequiredService<OutboxOptions>();

        // Run configurators to create indexes
        foreach (var configurator in _serviceProvider.GetServices<IMongoConfigurator>())
            await configurator.ConfigureAsync(_mongoHelper);
    }

    [TearDown]
    public void TearDown()
    {
        _serviceProvider.Dispose();
    }

    [Test]
    public async Task AddMessageAsync_MultipleMessagesInSameTransaction_AllPersisted()
    {
        using var session = await _mongoHelper.Client.StartSessionAsync();
        session.StartTransaction();

        await _outbox.AddMessageAsync(session, new TestPayload
        {
            Name = "First"
        });
        await _outbox.AddMessageAsync(session, new AnotherTestPayload
        {
            Description = "Second"
        });

        await session.CommitTransactionAsync();

        var collection = _mongoHelper.Database.GetCollection<OutboxMessage>(_options.CollectionName);
        var messages = await collection.Find(FilterDefinition<OutboxMessage>.Empty)
                                       .Sort(Builders<OutboxMessage>.Sort.Ascending(m => m.Id))
                                       .ToListAsync();

        messages.Should().HaveCount(2);
        messages[0].Type.Should().Be("TestPayload");
        messages[1].Type.Should().Be("AnotherPayload");
    }

    [Test]
    public void AddMessageAsync_NullSession_ThrowsArgumentNullException()
    {
        var act = () => _outbox.AddMessageAsync(null!, new TestPayload());

        act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("session");
    }

    [Test]
    public async Task AddMessageAsync_TransactionAborted_MessageNotPersisted()
    {
        using var session = await _mongoHelper.Client.StartSessionAsync();
        session.StartTransaction();

        await _outbox.AddMessageAsync(session, new TestPayload
        {
            Name = "Aborted"
        });

        await session.AbortTransactionAsync();

        var collection = _mongoHelper.Database.GetCollection<OutboxMessage>(_options.CollectionName);
        var messages = await collection.Find(FilterDefinition<OutboxMessage>.Empty).ToListAsync();

        messages.Should().BeEmpty();
    }

    [Test]
    public async Task AddMessageAsync_UnregisteredPayloadType_ThrowsInvalidOperationException()
    {
        using var session = await _mongoHelper.Client.StartSessionAsync();
        session.StartTransaction();

        // The assertion is awaited before session is disposed.
        // ReSharper disable once AccessToDisposedClosure
        var act = () => _outbox.AddMessageAsync(session, new UnregisteredPayload());

        await act.Should().ThrowAsync<InvalidOperationException>()
                 .WithMessage("*not registered*");
    }

    [Test]
    public async Task AddMessageAsync_WithTransaction_InsertsMessage()
    {
        using var session = await _mongoHelper.Client.StartSessionAsync();
        session.StartTransaction();

        await _outbox.AddMessageAsync(session, new TestPayload
        {
            Name = "Test",
            Value = 42
        }, "corr-123");

        await session.CommitTransactionAsync();

        var collection = _mongoHelper.Database.GetCollection<OutboxMessage>(_options.CollectionName);
        var messages = await collection.Find(FilterDefinition<OutboxMessage>.Empty).ToListAsync();

        messages.Should().HaveCount(1);
        messages[0].Type.Should().Be("TestPayload");
        messages[0].CorrelationId.Should().Be("corr-123");
        messages[0].State.Should().Be(OutboxMessageState.Pending);
        messages[0].IsLocked.Should().BeFalse();

        var payload = messages[0].DeserializePayload<TestPayload>();
        payload.Name.Should().Be("Test");
        payload.Value.Should().Be(42);
    }

    [Test]
    public async Task AddMessageAsync_WithoutTransaction_ThrowsInvalidOperationException()
    {
        using var session = await _mongoHelper.Client.StartSessionAsync();
        // Not starting a transaction

        // The assertion is awaited before session is disposed.
        // ReSharper disable once AccessToDisposedClosure
        var act = () => _outbox.AddMessageAsync(session, new TestPayload
        {
            Name = "Test"
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
                 .WithMessage("*active MongoDB transaction*");
    }

    [Test]
    public async Task DeserializePayload_ReturnsTypedPayload()
    {
        using var session = await _mongoHelper.Client.StartSessionAsync();
        session.StartTransaction();
        await _outbox.AddMessageAsync(session, new TestPayload
        {
            Name = "Typed",
            Value = 99
        });
        await session.CommitTransactionAsync();

        var collection = _mongoHelper.Database.GetCollection<OutboxMessage>(_options.CollectionName);
        var message = await collection.Find(FilterDefinition<OutboxMessage>.Empty).FirstAsync();

        var payload = message.DeserializePayload<TestPayload>();

        payload.Should().NotBeNull();
        payload.Name.Should().Be("Typed");
        payload.Value.Should().Be(99);
    }

    [Test]
    public async Task Processor_ExhaustedRetries_MarksAsFailed()
    {
        // Insert a message and manually set it near max retries
        using var session = await _mongoHelper.Client.StartSessionAsync();
        session.StartTransaction();
        await _outbox.AddMessageAsync(session, new TestPayload
        {
            Name = "Exhaust retries"
        });
        await session.CommitTransactionAsync();

        // Set AttemptCount to MaxRetries so the next claim becomes the last allowed attempt (MaxRetries + 1
        // total attempts), and its failure triggers permanent failure.
        var collection = _mongoHelper.Database.GetCollection<OutboxMessage>(_options.CollectionName);
        var updateResult = await collection.UpdateOneAsync(
            FilterDefinition<OutboxMessage>.Empty,
            Builders<OutboxMessage>.Update.Set(m => m.AttemptCount, _options.MaxRetries!.Value));

        updateResult.ModifiedCount.Should().Be(1);

        // Configure publisher to fail
        var publisher = _serviceProvider.GetRequiredService<IOutboxPublisher>() as TestOutboxPublisher;
        publisher!.ShouldThrow = true;

        // Start processor
        var processor = _serviceProvider.GetRequiredService<IOutboxProcessor>();
        await processor.StartAsync();

        await WaitUntilAsync(async () =>
        {
            var msg = await collection.Find(FilterDefinition<OutboxMessage>.Empty).FirstAsync();
            return msg.State == OutboxMessageState.Failed;
        });

        await processor.StopAsync();

        // Verify the message is permanently failed
        var message = await collection.Find(FilterDefinition<OutboxMessage>.Empty).FirstAsync();
        message.State.Should().Be(OutboxMessageState.Failed);
        message.FailedUtc.Should().NotBeNull();
        message.AttemptCount.Should().Be(_options.MaxRetries!.Value + 1);
    }

    [Test]
    public async Task Processor_FailedMessage_RetriesWithBackoff()
    {
        // Insert a message
        using var session = await _mongoHelper.Client.StartSessionAsync();
        session.StartTransaction();
        await _outbox.AddMessageAsync(session, new TestPayload
        {
            Name = "Fail me"
        });
        await session.CommitTransactionAsync();

        // Configure publisher to fail
        var publisher = _serviceProvider.GetRequiredService<IOutboxPublisher>() as TestOutboxPublisher;
        publisher!.ShouldThrow = true;
        publisher.ThrowMessage = "Broker unavailable";

        // Start processor
        var processor = _serviceProvider.GetRequiredService<IOutboxProcessor>();
        await processor.StartAsync();

        var collection = _mongoHelper.Database.GetCollection<OutboxMessage>(_options.CollectionName);

        await WaitUntilAsync(async () =>
        {
            // AttemptCount is granted atomically at claim time, before the publisher ever runs, so waiting on
            // it alone could observe the claim before the resolved failure below is actually recorded. Wait for
            // the failure's own writes instead: the error message set, the lock released, and the next attempt
            // scheduled.
            var msg = await collection.Find(FilterDefinition<OutboxMessage>.Empty).FirstAsync();
            return msg.Error is not null && !msg.IsLocked && msg.NextAttemptUtc is not null;
        });

        await processor.StopAsync();

        // Verify the message has its attempt count incremented and next attempt scheduled
        var message = await collection.Find(FilterDefinition<OutboxMessage>.Empty).FirstAsync();
        message.AttemptCount.Should().BeGreaterThan(0);
        message.Error.Should().Be("Broker unavailable");
        message.State.Should().Be(OutboxMessageState.Pending);
        message.IsLocked.Should().BeFalse();
    }

    [Test]
    public async Task Processor_MaxRetriesOneWithTwoResolvedFailures_TerminalizesAfterExactlyTwoInvocations()
    {
        // Arrange: a fresh message (AttemptCount starts at 0, not seeded) processed under a dedicated
        // MaxRetries=1 budget with a short bounded backoff, through a publisher that records every invocation
        // and always throws. This is the real, non-seeded equivalent of the equal-budget boundary: the message
        // must be granted exactly two attempts (MaxRetries + 1) — observing AttemptCount 1, then 2 — before the
        // second resolved failure terminalizes it, and no third claim must ever occur.
        var publisher = new RecordingThrowingOutboxPublisher();
        var services = new ServiceCollection()
                       .AddLogging()
                       .AddMongo(MongoUrl.Create(_container.GetConnectionString()), configure: options =>
                       {
                           options.DefaultDatabase = $"OutboxMaxRetriesOneTestDb_{Guid.NewGuid():N}";
                           options.RunConfiguratorsOnStartup = false;
                       })
                       .WithOutbox(o => o
                                        .WithPublisher<RecordingThrowingOutboxPublisher>(ServiceLifetime.Singleton)
                                        .WithMessage<TestPayload>("TestPayload")
                                        .WithMaxRetries(1)
                                        .WithRetryBackoff(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100))
                                        .WithPollingInterval(TimeSpan.FromMilliseconds(50))
                                        .WithLockLeaseTime(TimeSpan.FromSeconds(10))
                                        .WithBatchSize(10))
                       .Services
                       .AddSingleton<IOutboxPublisher>(publisher)
                       .BuildServiceProvider();

        await using var serviceProvider = services;
        var mongoHelper = serviceProvider.GetRequiredService<IMongoHelper>();
        var options = serviceProvider.GetRequiredService<OutboxOptions>();
        var collection = mongoHelper.Database.GetCollection<OutboxMessage>(options.CollectionName);

        using var session = await mongoHelper.Client.StartSessionAsync();
        session.StartTransaction();
        await serviceProvider.GetRequiredService<IOutbox>().AddMessageAsync(session, new TestPayload
        {
            Name = "Two-attempt exhaustion"
        });
        await session.CommitTransactionAsync();

        var processor = serviceProvider.GetRequiredService<IOutboxProcessor>();
        await processor.StartAsync();

        try
        {
            await WaitUntilAsync(async () =>
            {
                var msg = await collection.Find(FilterDefinition<OutboxMessage>.Empty).FirstAsync();
                return msg.State == OutboxMessageState.Failed;
            });

            // Give an erroneous third claim a chance to happen before asserting the final invocation count.
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }
        finally
        {
            await processor.StopAsync();
        }

        // Assert
        var message = await collection.Find(FilterDefinition<OutboxMessage>.Empty).FirstAsync();
        message.State.Should().Be(OutboxMessageState.Failed);
        message.FailedUtc.Should().NotBeNull();
        message.AttemptCount.Should().Be(2, "MaxRetries=1 allows exactly two attempts before the message terminalizes");
        message.IsLocked.Should().BeFalse();
        message.LockedUtc.Should().BeNull();
        message.LockId.Should().BeNull();

        publisher.ObservedAttemptCounts.Should().Equal(new[]
                                                       {
                                                           1L,
                                                           2L
                                                       },
                                                       "the publisher must observe exactly the two granted attempts, in order, and never a third");
    }

    [Test]
    public async Task Processor_MultiplePendingMessages_ProcessesAll()
    {
        // Insert multiple messages
        for (var i = 0; i < 5; i++)
        {
            using var session = await _mongoHelper.Client.StartSessionAsync();
            session.StartTransaction();
            await _outbox.AddMessageAsync(session, new TestPayload
            {
                Name = $"Message {i}"
            });
            await session.CommitTransactionAsync();
        }

        // Start processor
        var publisher = (TestOutboxPublisher)_serviceProvider.GetRequiredService<IOutboxPublisher>();
        var processor = _serviceProvider.GetRequiredService<IOutboxProcessor>();
        var collection = _mongoHelper.Database.GetCollection<OutboxMessage>(_options.CollectionName);
        await processor.StartAsync();

        await WaitUntilAsync(async () =>
        {
            var count = await collection.CountDocumentsAsync(
                Builders<OutboxMessage>.Filter.Eq(m => m.State, OutboxMessageState.Processed));
            return count >= 5;
        });

        await processor.StopAsync();

        // Verify all published
        publisher.PublishedMessages.Should().HaveCount(5);

        // Verify all marked processed
        var messages = await collection.Find(FilterDefinition<OutboxMessage>.Empty).ToListAsync();
        messages.Should().AllSatisfy(m => m.State.Should().Be(OutboxMessageState.Processed));
    }

    [Test]
    public async Task Processor_ProcessesPendingMessages()
    {
        // Insert a message
        using var session = await _mongoHelper.Client.StartSessionAsync();
        session.StartTransaction();
        await _outbox.AddMessageAsync(session, new TestPayload
        {
            Name = "Process me"
        }, "corr-1");
        await session.CommitTransactionAsync();

        // Start processor
        var publisher = _serviceProvider.GetRequiredService<IOutboxPublisher>() as TestOutboxPublisher;
        var processor = _serviceProvider.GetRequiredService<IOutboxProcessor>();
        var collection = _mongoHelper.Database.GetCollection<OutboxMessage>(_options.CollectionName);
        await processor.StartAsync();

        await WaitUntilAsync(async () =>
        {
            var count = await collection.CountDocumentsAsync(
                Builders<OutboxMessage>.Filter.Eq(m => m.State, OutboxMessageState.Processed));
            return count >= 1;
        });

        await processor.StopAsync();

        // Verify the message was published
        publisher!.PublishedMessages.Should().HaveCount(1);
        publisher.PublishedMessages.First().Type.Should().Be("TestPayload");
        publisher.PublishedMessages.First().CorrelationId.Should().Be("corr-1");

        // Verify message is marked as processed
        var message = await collection.Find(FilterDefinition<OutboxMessage>.Empty).FirstAsync();
        message.State.Should().Be(OutboxMessageState.Processed);
        message.ProcessedUtc.Should().NotBeNull();
        message.IsLocked.Should().BeFalse();
    }

    [Test]
    public async Task Processor_StopAsync_GracefulShutdown()
    {
        var processor = _serviceProvider.GetRequiredService<IOutboxProcessor>();
        await processor.StartAsync();

        // Stopping should not throw
        var act = () => processor.StopAsync();

        await act.Should().NotThrowAsync();
    }

    private static async Task WaitUntilAsync(Func<Task<Boolean>> condition, TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromSeconds(10);
        var sw = Stopwatch.StartNew();

        while (sw.Elapsed < timeout)
        {
            if (await condition())
                return;

            await Task.Delay(50);
        }

        throw new TimeoutException($"Condition was not met within {timeout}.");
    }

    /// <summary>
    /// Publisher that records the claimed message's already-granted <see cref="OutboxMessage.AttemptCount"/> on
    /// every invocation, in order, and always throws — used to prove the exact number and sequence of real
    /// claim/publish cycles a low <c>MaxRetries</c> budget allows, rather than seeding the count directly.
    /// </summary>
    private sealed class RecordingThrowingOutboxPublisher : IOutboxPublisher
    {
        private readonly ConcurrentQueue<Int64> _observedAttemptCounts = new();

        public IReadOnlyCollection<Int64> ObservedAttemptCounts => _observedAttemptCounts.ToArray();

        public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken = default)
        {
            _observedAttemptCounts.Enqueue(message.AttemptCount);
            throw new InvalidOperationException("Simulated permanent publish failure");
        }
    }

    private class UnregisteredPayload { }
}
