// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests.Integration.Queues;

using Chaos.Mongo.Queues;
using Chaos.Testing.Logging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using NUnit.Framework;
using System.Linq.Expressions;
using Testcontainers.MongoDb;

[Category("Integration")]
public class MongoQueueRetryIntegrationTests
{
    private MongoDbContainer _container;

    [OneTimeSetUp]
    public async Task GetMongoDbContainer()
        => _container = await MongoDbTestContainer.StartContainerAsync();

    [Test]
    public async Task QueueHandlerCrashesWithAttemptCountAtInt32MaxValueBoundary_ReclaimTerminalizesWithoutGrantingOrIncrementingFurther()
    {
        // Arrange: a queue item that has already recorded Int32.MaxValue attempts, with MaxRetries also
        // configured to Int32.MaxValue. The first acquire below still grants the last allowed attempt
        // (AttemptCount becomes Int32.MaxValue + 1), but the consumer that acquired it crashes (its handler
        // never resolves it with a success or failure). A second, independent subscription then reclaims it
        // after the lease expires: its own pre-update AttemptCount (Int32.MaxValue + 1) already exceeds
        // MaxRetries (Int32.MaxValue), so it must terminalize directly, without granting itself a lock,
        // invoking its handler, or incrementing AttemptCount beyond Int32.MaxValue + 1.
        var leaseTime = TimeSpan.FromMilliseconds(750);
        const String collectionName = "retry-queue-crash-boundary";
        var uniqueDbName = $"QueueRetryCrashBoundaryTest_{Guid.NewGuid():N}";
        var firstHandler = new HangingPayloadHandler();
        var secondHandler = new HangingPayloadHandler();

        await using var firstServiceProvider = CreateServiceProvider(uniqueDbName, collectionName, leaseTime, firstHandler, Int32.MaxValue);
        var helper = firstServiceProvider.GetRequiredService<IMongoHelper>();
        var collection = helper.Database.GetCollection<MongoQueueItem<RetryPayload>>(collectionName);
        var firstQueue = firstServiceProvider.GetRequiredService<IMongoQueue<RetryPayload>>();

        await using var secondServiceProvider = CreateServiceProvider(uniqueDbName, collectionName, leaseTime, secondHandler, Int32.MaxValue);
        var secondQueue = secondServiceProvider.GetRequiredService<IMongoQueue<RetryPayload>>();

        try
        {
            // Act: seed the item directly with AttemptCount already at Int32.MaxValue.
            var seededItem = new MongoQueueItem<RetryPayload>
            {
                Id = ObjectId.GenerateNewId(),
                CreatedUtc = DateTime.UtcNow,
                Payload = new RetryPayload
                {
                    Value = "crash-boundary-message"
                },
                AttemptCount = Int32.MaxValue
            };
            await collection.InsertOneAsync(seededItem);

            await firstQueue.StartSubscriptionAsync();
            await firstHandler.WaitForAttempts(1, TimeSpan.FromSeconds(10));

            await secondQueue.StartSubscriptionAsync();
            var terminalItem = await WaitForQueueItemAsync(collection,
                                                           x => x.Payload.Value == "crash-boundary-message" && x.IsClosed && x.IsTerminal,
                                                           TimeSpan.FromSeconds(10));

            // Assert
            firstHandler.Attempts.Should().Be(1, "the first subscription was granted the last allowed attempt before crashing");
            secondHandler.Attempts.Should().Be(0, "the reclaim already exceeded MaxRetries, so it must terminalize without ever invoking a handler");
            terminalItem.AttemptCount.Should().Be((Int64)Int32.MaxValue + 1, "the reclaim must not increment AttemptCount beyond the already-granted attempt");
            terminalItem.IsClosed.Should().BeTrue();
            terminalItem.IsTerminal.Should().BeTrue();
            terminalItem.IsLocked.Should().BeFalse();
        }
        finally
        {
            await secondQueue.StopSubscriptionAsync();
            await firstQueue.StopSubscriptionAsync();
        }
    }

    [Test]
    public async Task QueueHandlerFailure_WithAttemptCountAtInt32MaxValueBoundary_RecordsExactInt64CountWithoutOverflow()
    {
        // Arrange: a queue item that has already recorded Int32.MaxValue attempts, with MaxRetries also
        // configured to Int32.MaxValue. The next acquire must still grant one further attempt (the existing
        // count still equals MaxRetries), record AttemptCount == Int32.MaxValue + 1 (2_147_483_648) as a true
        // Int64 value without truncation or a numeric-overflow exception, and a failure at that count must
        // terminalize immediately via the same Int64 comparison, not the old overflow-prone MaxRetries + 1 shape.
        var leaseTime = TimeSpan.FromMilliseconds(750);
        const String collectionName = "retry-queue-boundary";
        var handler = new AlwaysFailingPayloadHandler();
        var services = new ServiceCollection();
        services.AddNUnitTestLogging();

        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"QueueRetryBoundaryTest_{Guid.NewGuid():N}";
        services.AddMongo(url, uniqueDbName)
                .WithQueue<RetryPayload>(queue => queue
                                                  .WithPayloadHandler(_ => handler)
                                                  .WithCollectionName(collectionName)
                                                  .WithLockLeaseTime(leaseTime)
                                                  .WithMaxRetries(Int32.MaxValue)
                                                  .WithoutAutoStartSubscription());

        await using var serviceProvider = services.BuildServiceProvider();
        var helper = serviceProvider.GetRequiredService<IMongoHelper>();
        var collection = helper.Database.GetCollection<MongoQueueItem<RetryPayload>>(collectionName);
        var queue = serviceProvider.GetRequiredService<IMongoQueue<RetryPayload>>();
        await queue.StartSubscriptionAsync();

        try
        {
            // Act: seed the item directly with AttemptCount already at Int32.MaxValue instead of publishing and
            // waiting for Int32.MaxValue real attempts.
            var seededItem = new MongoQueueItem<RetryPayload>
            {
                Id = ObjectId.GenerateNewId(),
                CreatedUtc = DateTime.UtcNow,
                Payload = new RetryPayload
                {
                    Value = "boundary-message"
                },
                AttemptCount = Int32.MaxValue
            };
            await collection.InsertOneAsync(seededItem);

            await handler.WaitForAttempts(1, TimeSpan.FromSeconds(10));
            var terminalItem = await WaitForQueueItemAsync(collection,
                                                           x => x.Payload.Value == "boundary-message" && x.IsClosed && x.IsTerminal,
                                                           TimeSpan.FromSeconds(10));

            // Assert
            handler.Attempts.Should().Be(1, "the seeded item already consumed Int32.MaxValue attempts; this is the final granted attempt");
            terminalItem.AttemptCount.Should().Be((Int64)Int32.MaxValue + 1);
            terminalItem.IsClosed.Should().BeTrue();
            terminalItem.IsTerminal.Should().BeTrue();
            terminalItem.IsLocked.Should().BeFalse();
        }
        finally
        {
            await queue.StopSubscriptionAsync();
        }
    }

    [Test]
    public async Task QueueHandlerFailure_WithMaxRetries_MarksItemTerminalAfterRetryBudgetIsExhausted()
    {
        // Arrange
        var leaseTime = TimeSpan.FromMilliseconds(750);
        var handler = new AlwaysFailingPayloadHandler();
        var services = new ServiceCollection();
        services.AddNUnitTestLogging();

        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"QueueRetryTerminalTest_{Guid.NewGuid():N}";
        services.AddMongo(url, uniqueDbName)
                .WithQueue<RetryPayload>(queue => queue
                                                  .WithPayloadHandler(_ => handler)
                                                  .WithCollectionName("retry-queue")
                                                  .WithLockLeaseTime(leaseTime)
                                                  .WithMaxRetries(1)
                                                  .WithoutAutoStartSubscription());

        await using var serviceProvider = services.BuildServiceProvider();
        var helper = serviceProvider.GetRequiredService<IMongoHelper>();
        var collection = helper.Database.GetCollection<MongoQueueItem<RetryPayload>>("retry-queue");
        var queue = serviceProvider.GetRequiredService<IMongoQueue<RetryPayload>>();
        await queue.StartSubscriptionAsync();

        try
        {
            // Act
            await queue.PublishAsync(new RetryPayload
            {
                Value = "poison-message"
            });

            await handler.WaitForAttempts(2, TimeSpan.FromSeconds(10));
            var terminalItem = await WaitForQueueItemAsync(collection,
                                                           x => x.Payload.Value == "poison-message" && x.IsClosed && x.IsTerminal,
                                                           TimeSpan.FromSeconds(10));

            // Assert
            handler.Attempts.Should().Be(2);
            terminalItem.AttemptCount.Should().Be(2);
            terminalItem.IsClosed.Should().BeTrue();
            terminalItem.IsTerminal.Should().BeTrue();
            terminalItem.IsLocked.Should().BeFalse();
            terminalItem.LockedUtc.Should().BeNull();
            terminalItem.ClosedUtc.Should().NotBeNull();
        }
        finally
        {
            await queue.StopSubscriptionAsync();
        }
    }

    [Test]
    public async Task QueueHandlerFailure_WithNoRetry_MarksItemTerminalAfterFirstFailure()
    {
        // Arrange
        var leaseTime = TimeSpan.FromMilliseconds(750);
        var handler = new AlwaysFailingPayloadHandler();
        var services = new ServiceCollection();
        services.AddNUnitTestLogging();

        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"QueueNoRetryTerminalTest_{Guid.NewGuid():N}";
        services.AddMongo(url, uniqueDbName)
                .WithQueue<RetryPayload>(queue => queue
                                                  .WithPayloadHandler(_ => handler)
                                                  .WithCollectionName("retry-queue")
                                                  .WithLockLeaseTime(leaseTime)
                                                  .WithNoRetry()
                                                  .WithoutAutoStartSubscription());

        await using var serviceProvider = services.BuildServiceProvider();
        var helper = serviceProvider.GetRequiredService<IMongoHelper>();
        var collection = helper.Database.GetCollection<MongoQueueItem<RetryPayload>>("retry-queue");
        var queue = serviceProvider.GetRequiredService<IMongoQueue<RetryPayload>>();
        await queue.StartSubscriptionAsync();

        try
        {
            // Act
            await queue.PublishAsync(new RetryPayload
            {
                Value = "single-attempt"
            });

            await handler.WaitForAttempts(1, TimeSpan.FromSeconds(10));
            var terminalItem = await WaitForQueueItemAsync(collection,
                                                           x => x.Payload.Value == "single-attempt" && x.IsClosed && x.IsTerminal,
                                                           TimeSpan.FromSeconds(10));
            await Task.Delay(leaseTime + TimeSpan.FromMilliseconds(500));

            // Assert
            handler.Attempts.Should().Be(1);
            terminalItem.AttemptCount.Should().Be(1);
            terminalItem.IsClosed.Should().BeTrue();
            terminalItem.IsTerminal.Should().BeTrue();
        }
        finally
        {
            await queue.StopSubscriptionAsync();
        }
    }

    private static async Task<MongoQueueItem<RetryPayload>> WaitForQueueItemAsync(
        IMongoCollection<MongoQueueItem<RetryPayload>> collection,
        Expression<Func<MongoQueueItem<RetryPayload>, Boolean>> filter,
        TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var queueItem = await collection.Find(filter).FirstOrDefaultAsync(cts.Token);
                if (queueItem is not null)
                {
                    return queueItem;
                }

                await Task.Delay(100, cts.Token);
            }
        }
        catch (OperationCanceledException e) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException("Queue item did not reach the expected retry state.", e);
        }

        throw new TimeoutException("Queue item did not reach the expected retry state.");
    }

    private ServiceProvider CreateServiceProvider(
        String databaseName,
        String collectionName,
        TimeSpan lockLeaseTime,
        IMongoQueuePayloadHandler<RetryPayload> handler,
        Int32 maxRetries)
    {
        var services = new ServiceCollection();
        services.AddNUnitTestLogging();

        var url = MongoUrl.Create(_container.GetConnectionString());
        services.AddMongo(url, databaseName)
                .WithQueue<RetryPayload>(queue => queue
                                                  .WithPayloadHandler(_ => handler)
                                                  .WithCollectionName(collectionName)
                                                  .WithLockLeaseTime(lockLeaseTime)
                                                  .WithMaxRetries(maxRetries)
                                                  .WithoutAutoStartSubscription());

        return services.BuildServiceProvider();
    }

    private sealed class AlwaysFailingPayloadHandler : IMongoQueuePayloadHandler<RetryPayload>
    {
        private readonly SemaphoreSlim _attemptSemaphore = new(0);

        public Int32 Attempts { get; private set; }

        public async Task WaitForAttempts(Int32 count, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            for (var i = 0; i < count; i++)
            {
                await _attemptSemaphore.WaitAsync(cts.Token);
            }
        }

        public Task HandlePayloadAsync(RetryPayload payload, CancellationToken cancellationToken = default)
        {
            Attempts++;
            _attemptSemaphore.Release();
            throw new InvalidOperationException("Simulated permanent failure");
        }
    }

    /// <summary>
    /// Payload handler that never throws and never completes on its own, simulating a crashed or permanently
    /// hung consumer. Each invocation blocks until the ambient token is cancelled (by subscription shutdown),
    /// so it never writes a resolved success or failure for the attempt that invoked it.
    /// </summary>
    private sealed class HangingPayloadHandler : IMongoQueuePayloadHandler<RetryPayload>
    {
        private readonly SemaphoreSlim _attemptSemaphore = new(0);

        public Int32 Attempts { get; private set; }

        public async Task WaitForAttempts(Int32 count, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            for (var i = 0; i < count; i++)
            {
                await _attemptSemaphore.WaitAsync(cts.Token);
            }
        }

        public async Task HandlePayloadAsync(RetryPayload payload, CancellationToken cancellationToken = default)
        {
            Attempts++;
            _attemptSemaphore.Release();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    private sealed class RetryPayload
    {
        public String Value { get; init; } = String.Empty;
    }
}
