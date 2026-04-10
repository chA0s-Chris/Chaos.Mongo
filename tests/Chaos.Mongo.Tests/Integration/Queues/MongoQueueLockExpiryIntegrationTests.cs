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

public class MongoQueueLockExpiryIntegrationTests
{
    private MongoDbContainer _container;

    [OneTimeSetUp]
    public async Task GetMongoDbContainer()
        => _container = await MongoDbTestContainer.StartContainerAsync();

    [Test]
    public async Task QueueHandlerFailure_ReprocessesMessageAfterLockLeaseExpires()
    {
        // Arrange
        var leaseTime = TimeSpan.FromSeconds(2);
        var handler = new RecoveringPayloadHandler();
        var services = new ServiceCollection();
        services.AddNUnitTestLogging();

        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"QueueLeaseRecoveryTest_{Guid.NewGuid():N}";
        services.AddMongo(url, uniqueDbName)
                .WithQueue<LeasePayload>(queue => queue
                                                  .WithPayloadHandler(_ => handler)
                                                  .WithCollectionName("lease-queue")
                                                  .WithLockLeaseTime(leaseTime)
                                                  .WithoutAutoStartSubscription());

        await using var serviceProvider = services.BuildServiceProvider();
        var helper = serviceProvider.GetRequiredService<IMongoHelper>();
        var collection = helper.Database.GetCollection<MongoQueueItem<LeasePayload>>("lease-queue");
        var queue = serviceProvider.GetRequiredService<IMongoQueue<LeasePayload>>();
        await queue.StartSubscriptionAsync();

        // Act
        await queue.PublishAsync(new()
        {
            Value = "recover-me"
        });

        await handler.WaitForAttempts(1, TimeSpan.FromSeconds(10));
        var firstAttemptItem = await collection.Find(x => x.Payload.Value == "recover-me").SingleAsync();

        await handler.WaitForSuccess(1, TimeSpan.FromSeconds(10));
        var recoveredItem = await WaitForQueueItemAsync(collection,
                                                        x => x.Payload.Value == "recover-me" && x.IsClosed,
                                                        TimeSpan.FromSeconds(10));

        // Assert
        firstAttemptItem.IsClosed.Should().BeFalse();
        firstAttemptItem.IsLocked.Should().BeTrue();
        firstAttemptItem.LockedUtc.Should().NotBeNull();

        handler.Attempts.Should().Be(2);
        handler.AttemptStartedAtUtc.Should().HaveCount(2);
        (handler.AttemptStartedAtUtc[1] - handler.AttemptStartedAtUtc[0]).Should().BeGreaterThanOrEqualTo(leaseTime - TimeSpan.FromMilliseconds(500));
        handler.SuccessfulPayloads.Should().ContainSingle(x => x.Value == "recover-me");
        recoveredItem.IsClosed.Should().BeTrue();
        recoveredItem.IsLocked.Should().BeFalse();
        recoveredItem.ClosedUtc.Should().NotBeNull();
        recoveredItem.LockedUtc.Should().BeNull();

        // Cleanup
        await queue.StopSubscriptionAsync();
    }

    [Test]
    public async Task SlowHandlerCompletion_DoesNotClearReplacementLock()
    {
        // Arrange
        var leaseTime = TimeSpan.FromMilliseconds(750);
        var uniqueDbName = $"QueueLeaseOwnershipTest_{Guid.NewGuid():N}";
        var collectionName = "lease-queue";
        var firstHandler = new BlockingPayloadHandler();
        var secondHandler = new BlockingPayloadHandler();

        await using var firstServiceProvider = CreateServiceProvider(uniqueDbName, collectionName, leaseTime, firstHandler);
        await using var secondServiceProvider = CreateServiceProvider(uniqueDbName, collectionName, leaseTime, secondHandler);

        var firstHelper = firstServiceProvider.GetRequiredService<IMongoHelper>();
        var collection = firstHelper.Database.GetCollection<MongoQueueItem<LeasePayload>>(collectionName);
        var firstQueue = firstServiceProvider.GetRequiredService<IMongoQueue<LeasePayload>>();
        var secondQueue = secondServiceProvider.GetRequiredService<IMongoQueue<LeasePayload>>();

        await firstQueue.StartSubscriptionAsync();

        try
        {
            // Act
            await firstQueue.PublishAsync(new()
            {
                Value = "handoff-me"
            });

            await firstHandler.WaitForStart(TimeSpan.FromSeconds(10));
            var firstLock = await WaitForQueueItemAsync(collection,
                                                        x => x.Payload.Value == "handoff-me" && x.IsLocked && !x.IsClosed,
                                                        TimeSpan.FromSeconds(10));

            await secondQueue.StartSubscriptionAsync();

            await secondHandler.WaitForStart(TimeSpan.FromSeconds(10));
            var replacementLock = await WaitForQueueItemAsync(collection,
                                                              x => x.Payload.Value == "handoff-me" &&
                                                                   x.IsLocked &&
                                                                   !x.IsClosed &&
                                                                   x.LockedUtc != null &&
                                                                   x.LockedUtc > firstLock.LockedUtc,
                                                              TimeSpan.FromSeconds(10));

            firstHandler.Release();
            await firstHandler.WaitForCompletion(TimeSpan.FromSeconds(10));

            var itemWhileReplacementOwnsLock = await WaitForQueueItemAsync(collection,
                                                                           x => x.Payload.Value == "handoff-me" &&
                                                                                x.IsLocked &&
                                                                                !x.IsClosed &&
                                                                                x.LockedUtc == replacementLock.LockedUtc,
                                                                           TimeSpan.FromSeconds(10));

            // Assert
            itemWhileReplacementOwnsLock.IsClosed.Should().BeFalse();
            itemWhileReplacementOwnsLock.IsLocked.Should().BeTrue();
            itemWhileReplacementOwnsLock.LockedUtc.Should().Be(replacementLock.LockedUtc);

            secondHandler.Release();
            await secondHandler.WaitForCompletion(TimeSpan.FromSeconds(10));

            var closedItem = await WaitForQueueItemAsync(collection,
                                                         x => x.Payload.Value == "handoff-me" && x.IsClosed,
                                                         TimeSpan.FromSeconds(10));

            closedItem.IsClosed.Should().BeTrue();
            closedItem.IsLocked.Should().BeFalse();
            closedItem.LockedUtc.Should().BeNull();
        }
        finally
        {
            await secondQueue.StopSubscriptionAsync();
            await firstQueue.StopSubscriptionAsync();
        }
    }

    [Test]
    public async Task StartSubscription_CreatesCompoundIndexForLeaseRecovery()
    {
        // Arrange
        var handler = new PassivePayloadHandler();
        var services = new ServiceCollection();
        services.AddNUnitTestLogging();

        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"QueueLeaseIndexTest_{Guid.NewGuid():N}";
        services.AddMongo(url, uniqueDbName)
                .WithQueue<LeasePayload>(queue => queue
                                                  .WithPayloadHandler(_ => handler)
                                                  .WithCollectionName("lease-queue")
                                                  .WithoutAutoStartSubscription());

        await using var serviceProvider = services.BuildServiceProvider();
        var helper = serviceProvider.GetRequiredService<IMongoHelper>();
        var collection = helper.Database.GetCollection<BsonDocument>("lease-queue");
        var queue = serviceProvider.GetRequiredService<IMongoQueue<LeasePayload>>();

        // Act
        await queue.StartSubscriptionAsync();
        var indexes = await collection.Indexes.ListAsync();
        var indexDocuments = await indexes.ToListAsync();

        // Assert
        indexDocuments.Should().Contain(x =>
                                            x["key"].AsBsonDocument.ElementCount == 3 &&
                                            x["key"]["IsClosed"] == 1 &&
                                            x["key"]["IsLocked"] == 1 &&
                                            x["key"]["LockedUtc"] == 1 &&
                                            !x.Contains("partialFilterExpression"));

        // Cleanup
        await queue.StopSubscriptionAsync();
    }

    private static async Task<MongoQueueItem<LeasePayload>> WaitForQueueItemAsync(
        IMongoCollection<MongoQueueItem<LeasePayload>> collection,
        Expression<Func<MongoQueueItem<LeasePayload>, Boolean>> filter,
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
            throw new TimeoutException("Queue item did not reach the expected state.", e);
        }

        throw new TimeoutException("Queue item did not reach the expected state.");
    }

    private ServiceProvider CreateServiceProvider(
        String databaseName,
        String collectionName,
        TimeSpan lockLeaseTime,
        IMongoQueuePayloadHandler<LeasePayload> handler)
    {
        var services = new ServiceCollection();
        services.AddNUnitTestLogging();

        var url = MongoUrl.Create(_container.GetConnectionString());
        services.AddMongo(url, databaseName)
                .WithQueue<LeasePayload>(queue => queue
                                                  .WithPayloadHandler(_ => handler)
                                                  .WithCollectionName(collectionName)
                                                  .WithLockLeaseTime(lockLeaseTime)
                                                  .WithoutAutoStartSubscription());

        return services.BuildServiceProvider();
    }

    private sealed class BlockingPayloadHandler : IMongoQueuePayloadHandler<LeasePayload>
    {
        private readonly SemaphoreSlim _completionSemaphore = new(0);
        private readonly SemaphoreSlim _releaseSemaphore = new(0);
        private readonly SemaphoreSlim _startSemaphore = new(0);

        public void Release()
            => _releaseSemaphore.Release();

        public async Task WaitForCompletion(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            await _completionSemaphore.WaitAsync(cts.Token);
        }

        public async Task WaitForStart(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            await _startSemaphore.WaitAsync(cts.Token);
        }

        public async Task HandlePayloadAsync(LeasePayload payload, CancellationToken cancellationToken = default)
        {
            _startSemaphore.Release();
            await _releaseSemaphore.WaitAsync(cancellationToken);
            _completionSemaphore.Release();
        }
    }

    private sealed class LeasePayload
    {
        public String Value { get; init; } = String.Empty;
    }

    private sealed class PassivePayloadHandler : IMongoQueuePayloadHandler<LeasePayload>
    {
        public Task HandlePayloadAsync(LeasePayload payload, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class RecoveringPayloadHandler : IMongoQueuePayloadHandler<LeasePayload>
    {
        private readonly SemaphoreSlim _attemptSemaphore = new(0);
        private readonly List<DateTimeOffset> _attemptStartedAtUtc = [];
        private readonly List<LeasePayload> _successfulPayloads = [];
        private readonly SemaphoreSlim _successSemaphore = new(0);

        public Int32 Attempts { get; private set; }
        public IReadOnlyList<DateTimeOffset> AttemptStartedAtUtc => _attemptStartedAtUtc;
        public IReadOnlyList<LeasePayload> SuccessfulPayloads => _successfulPayloads;

        public async Task WaitForAttempts(Int32 count, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            for (var i = 0; i < count; i++)
            {
                await _attemptSemaphore.WaitAsync(cts.Token);
            }
        }

        public async Task WaitForSuccess(Int32 count, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            for (var i = 0; i < count; i++)
            {
                await _successSemaphore.WaitAsync(cts.Token);
            }
        }

        public Task HandlePayloadAsync(LeasePayload payload, CancellationToken cancellationToken = default)
        {
            _attemptStartedAtUtc.Add(DateTimeOffset.UtcNow);
            Attempts++;
            _attemptSemaphore.Release();

            if (Attempts == 1)
            {
                throw new InvalidOperationException("Simulated handler failure");
            }

            _successfulPayloads.Add(payload);
            _successSemaphore.Release();
            return Task.CompletedTask;
        }
    }
}
