// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox.Tests.Integration;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using NUnit.Framework;
using Testcontainers.MongoDb;

public class OutboxAttemptCountBoundaryIntegrationTests
{
    private MongoDbContainer _container;

    [OneTimeSetUp]
    public async Task GetMongoDbContainer()
        => _container = await MongoDbTestContainer.StartContainerAsync();

    [Test]
    public async Task Processor_CrashesWithAttemptCountAtInt32MaxValueBoundary_ReclaimTerminalizesWithoutGrantingOrIncrementingFurther()
    {
        // Arrange: an outbox message that has already recorded Int32.MaxValue attempts, with MaxRetries also
        // configured to Int32.MaxValue. The first claim below still grants the last allowed attempt
        // (AttemptCount becomes Int32.MaxValue + 1), but that processor crashes (its publisher never resolves
        // the claim with a success or failure). A second, independent processor then reclaims the message
        // after the lease expires: its own pre-update AttemptCount (Int32.MaxValue + 1) already exceeds
        // MaxRetries (Int32.MaxValue), so it must terminalize directly, without granting itself a lock,
        // calling its own publisher, or incrementing AttemptCount beyond Int32.MaxValue + 1.
        var url = MongoUrl.Create(_container.GetConnectionString());
        var databaseName = $"OutboxCrashBoundaryTestDb_{Guid.NewGuid():N}";
        var leaseTime = TimeSpan.FromMilliseconds(750);
        var firstPublisher = new HangingOutboxPublisher();
        var secondPublisher = new HangingOutboxPublisher();

        await using var firstServiceProvider = CreateServiceProvider(url, databaseName, leaseTime, firstPublisher);
        var mongoHelper = firstServiceProvider.GetRequiredService<IMongoHelper>();
        var options = firstServiceProvider.GetRequiredService<OutboxOptions>();
        var collection = mongoHelper.Database.GetCollection<OutboxMessage>(options.CollectionName);

        await using var secondServiceProvider = CreateServiceProvider(url, databaseName, leaseTime, secondPublisher);

        using var session = await mongoHelper.Client.StartSessionAsync();
        session.StartTransaction();
        await firstServiceProvider.GetRequiredService<IOutbox>().AddMessageAsync(session, new TestPayload
        {
            Name = "Crash boundary message"
        });
        await session.CommitTransactionAsync();

        // Seed AttemptCount directly at Int32.MaxValue instead of actually performing that many attempts.
        var updateResult = await collection.UpdateOneAsync(
            FilterDefinition<OutboxMessage>.Empty,
            Builders<OutboxMessage>.Update.Set(m => m.AttemptCount, Int32.MaxValue));
        updateResult.ModifiedCount.Should().Be(1);

        var firstProcessor = firstServiceProvider.GetRequiredService<IOutboxProcessor>();
        var secondProcessor = secondServiceProvider.GetRequiredService<IOutboxProcessor>();

        try
        {
            await firstProcessor.StartAsync();
            await firstPublisher.WaitForClaimAsync(TimeSpan.FromSeconds(10));

            await secondProcessor.StartAsync();

            await WaitUntilAsync(async () =>
            {
                var msg = await collection.Find(FilterDefinition<OutboxMessage>.Empty).FirstAsync();
                return msg.State == OutboxMessageState.Failed;
            });
        }
        finally
        {
            await secondProcessor.StopAsync();
            await firstProcessor.StopAsync();
        }

        // Assert
        var message = await collection.Find(FilterDefinition<OutboxMessage>.Empty).FirstAsync();
        message.State.Should().Be(OutboxMessageState.Failed);
        message.FailedUtc.Should().NotBeNull();
        message.AttemptCount.Should().Be((Int64)Int32.MaxValue + 1, "the reclaim must not increment AttemptCount beyond the already-granted attempt");
        firstPublisher.Claims.Should().Be(1, "the first processor was granted the last allowed attempt before crashing");
        secondPublisher.Claims.Should().Be(0, "the reclaim already exceeded MaxRetries, so it must terminalize without ever calling the publisher");
    }

    [Test]
    public async Task Processor_PublishFailsWithAttemptCountAtInt32MaxValueBoundary_RecordsExactInt64CountWithoutOverflow()
    {
        // Arrange: an outbox message that has already recorded Int32.MaxValue attempts, with MaxRetries also
        // configured to Int32.MaxValue. The next claim must still grant one further attempt (the existing count
        // still equals MaxRetries), record AttemptCount == Int32.MaxValue + 1 (2_147_483_648) as a true Int64
        // value without truncation or a numeric-overflow exception, and a failure at that count must
        // terminalize immediately via the same Int64 comparison, not the old overflow-prone comparison shape.
        var url = MongoUrl.Create(_container.GetConnectionString());
        var publisher = new TestOutboxPublisher
        {
            ShouldThrow = true,
            ThrowMessage = "Simulated permanent failure at the boundary"
        };
        var services = new ServiceCollection()
                       .AddLogging()
                       .AddMongo(url, configure: options =>
                       {
                           options.DefaultDatabase = $"OutboxBoundaryTestDb_{Guid.NewGuid():N}";
                           options.RunConfiguratorsOnStartup = false;
                       })
                       .WithOutbox(o => o
                                        .WithPublisher<TestOutboxPublisher>(ServiceLifetime.Singleton)
                                        .WithMessage<TestPayload>("TestPayload")
                                        .WithMaxRetries(Int32.MaxValue)
                                        .WithPollingInterval(TimeSpan.FromMilliseconds(100))
                                        .WithLockLeaseTime(TimeSpan.FromSeconds(10))
                                        .WithBatchSize(10))
                       .Services
                       .AddSingleton<IOutboxPublisher>(publisher)
                       .BuildServiceProvider();

        await using var serviceProvider = services;
        var mongoHelper = serviceProvider.GetRequiredService<IMongoHelper>();
        var options = serviceProvider.GetRequiredService<OutboxOptions>();

        using var session = await mongoHelper.Client.StartSessionAsync();
        session.StartTransaction();
        await serviceProvider.GetRequiredService<IOutbox>().AddMessageAsync(session, new TestPayload
        {
            Name = "Boundary message"
        });
        await session.CommitTransactionAsync();

        // Seed AttemptCount directly at Int32.MaxValue instead of actually performing that many attempts.
        var collection = mongoHelper.Database.GetCollection<OutboxMessage>(options.CollectionName);
        var updateResult = await collection.UpdateOneAsync(
            FilterDefinition<OutboxMessage>.Empty,
            Builders<OutboxMessage>.Update.Set(m => m.AttemptCount, Int32.MaxValue));
        updateResult.ModifiedCount.Should().Be(1);

        var processor = serviceProvider.GetRequiredService<IOutboxProcessor>();
        await processor.StartAsync();

        try
        {
            await WaitUntilAsync(async () =>
            {
                var msg = await collection.Find(FilterDefinition<OutboxMessage>.Empty).FirstAsync();
                return msg.State == OutboxMessageState.Failed;
            });
        }
        finally
        {
            await processor.StopAsync();
        }

        // Assert
        var message = await collection.Find(FilterDefinition<OutboxMessage>.Empty).FirstAsync();
        message.State.Should().Be(OutboxMessageState.Failed);
        message.FailedUtc.Should().NotBeNull();
        message.AttemptCount.Should().Be((Int64)Int32.MaxValue + 1);
        publisher.PublishedMessages.Should().BeEmpty("the publisher throws on every call, so success is never recorded");
    }

    private static async Task WaitUntilAsync(Func<Task<Boolean>> condition, TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromSeconds(10);
        using var cts = new CancellationTokenSource(timeout.Value);
        while (!await condition())
        {
            if (cts.IsCancellationRequested)
            {
                throw new TimeoutException($"Condition was not met within {timeout}.");
            }

            await Task.Delay(50, cts.Token);
        }
    }

    private ServiceProvider CreateServiceProvider(MongoUrl url, String databaseName, TimeSpan lockLeaseTime, IOutboxPublisher publisher)
    {
        var services = new ServiceCollection()
                       .AddLogging()
                       .AddMongo(url, configure: options =>
                       {
                           options.DefaultDatabase = databaseName;
                           options.RunConfiguratorsOnStartup = false;
                       })
                       .WithOutbox(o => o
                                        .WithPublisher<TestOutboxPublisher>(ServiceLifetime.Singleton)
                                        .WithMessage<TestPayload>("TestPayload")
                                        .WithMaxRetries(Int32.MaxValue)
                                        .WithPollingInterval(TimeSpan.FromMilliseconds(100))
                                        .WithLockLeaseTime(lockLeaseTime)
                                        .WithBatchSize(10))
                       .Services
                       .AddSingleton<IOutboxPublisher>(publisher);

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Publisher that never throws and never completes on its own, simulating a crashed processor. Each call
    /// blocks until the ambient token is cancelled (by processor shutdown), so it never resolves the claim
    /// that invoked it with a success or failure.
    /// </summary>
    private sealed class HangingOutboxPublisher : IOutboxPublisher
    {
        private readonly SemaphoreSlim _claimSemaphore = new(0);

        public Int32 Claims { get; private set; }

        public async Task WaitForClaimAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            await _claimSemaphore.WaitAsync(cts.Token);
        }

        public async Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken = default)
        {
            Claims++;
            _claimSemaphore.Release();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }
}
