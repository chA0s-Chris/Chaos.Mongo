// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox.Tests.Integration;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using NUnit.Framework;
using System.Diagnostics;
using Testcontainers.MongoDb;

public class OutboxIntegrationTests
{
    private MongoDbContainer _container;
    private IMongoHelper _mongoHelper;
    private OutboxOptions _options;
    private IOutbox _outbox;
    private ServiceProvider _serviceProvider;

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

        var act = () => _outbox.AddMessageAsync(session, new UnregisteredPayload());

        await act.Should().ThrowAsync<InvalidOperationException>()
                 .WithMessage("*not registered*");
    }

    [Test]
    public async Task AddMessageAsync_WithoutTransaction_ThrowsInvalidOperationException()
    {
        using var session = await _mongoHelper.Client.StartSessionAsync();
        // Not starting a transaction

        var act = () => _outbox.AddMessageAsync(session, new TestPayload
        {
            Name = "Test"
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
                 .WithMessage("*active MongoDB transaction*");
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

        // Set retry count to MaxRetries - 1 so next failure triggers permanent failure
        var collection = _mongoHelper.Database.GetCollection<OutboxMessage>(_options.CollectionName);
        var updateResult = await collection.UpdateOneAsync(
            FilterDefinition<OutboxMessage>.Empty,
            Builders<OutboxMessage>.Update.Set(m => m.RetryCount, _options.MaxRetries - 1));

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
        message.RetryCount.Should().Be(_options.MaxRetries);
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
            var msg = await collection.Find(FilterDefinition<OutboxMessage>.Empty).FirstAsync();
            return msg.RetryCount > 0;
        });

        await processor.StopAsync();

        // Verify the message has retry count incremented and next attempt scheduled
        var message = await collection.Find(FilterDefinition<OutboxMessage>.Empty).FirstAsync();
        message.RetryCount.Should().BeGreaterThan(0);
        message.Error.Should().Be("Broker unavailable");
        message.State.Should().Be(OutboxMessageState.Pending);
        message.IsLocked.Should().BeFalse();
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
        var publisher = _serviceProvider.GetRequiredService<IOutboxPublisher>() as TestOutboxPublisher;
        var processor = _serviceProvider.GetRequiredService<IOutboxProcessor>();
        await processor.StartAsync();

        await WaitUntilAsync(() => Task.FromResult(publisher!.PublishedMessages.Count >= 5));

        await processor.StopAsync();

        // Verify all published
        publisher!.PublishedMessages.Should().HaveCount(5);

        // Verify all marked processed
        var collection = _mongoHelper.Database.GetCollection<OutboxMessage>(_options.CollectionName);
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
        await processor.StartAsync();

        await WaitUntilAsync(() => Task.FromResult(publisher!.PublishedMessages.Count >= 1));

        await processor.StopAsync();

        // Verify the message was published
        publisher!.PublishedMessages.Should().HaveCount(1);
        publisher.PublishedMessages[0].Type.Should().Be("TestPayload");
        publisher.PublishedMessages[0].CorrelationId.Should().Be("corr-1");

        // Verify message is marked as processed
        var collection = _mongoHelper.Database.GetCollection<OutboxMessage>(_options.CollectionName);
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
                                            .WithLockTimeout(TimeSpan.FromSeconds(10))
                                            .WithBatchSize(10))
                           .Services
                           .BuildServiceProvider();

        _mongoHelper = _serviceProvider.GetRequiredService<IMongoHelper>();
        _outbox = _serviceProvider.GetRequiredService<IOutbox>();
        _options = _serviceProvider.GetRequiredService<OutboxOptions>();

        // Run configurators to create indexes
        foreach (var configurator in _serviceProvider.GetServices<Configuration.IMongoConfigurator>())
            await configurator.ConfigureAsync(_mongoHelper);
    }

    [TearDown]
    public void TearDown()
    {
        _serviceProvider.Dispose();
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

    private class UnregisteredPayload
    {
        public String Data { get; set; } = String.Empty;
    }
}
