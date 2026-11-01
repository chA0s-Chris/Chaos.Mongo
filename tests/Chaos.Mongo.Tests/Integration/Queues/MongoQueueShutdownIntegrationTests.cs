// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests.Integration.Queues;

using Chaos.Mongo.Queues;
using Chaos.Testing.Logging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using NUnit.Framework;
using Testcontainers.MongoDb;

public class MongoQueueShutdownIntegrationTests
{
    private MongoDbContainer _container;

    [OneTimeSetUp]
    public async Task GetMongoDbContainer()
        => _container = await MongoDbTestContainer.StartContainerAsync();

    [Test]
    public async Task StopSubscriptionAsync_WhenHandlerIsInFlight_ReleasesLockSoItemIsReprocessedWithoutWaitingForLease()
    {
        // Arrange: a deliberately long lease. If the released item waited out the lease instead of being
        // released immediately, the second subscription below would never observe it within its short timeout.
        var leaseTime = TimeSpan.FromSeconds(30);
        var firstHandler = new ShutdownAwarePayloadHandler();
        var uniqueDbName = $"QueueShutdownReleaseTest_{Guid.NewGuid():N}";
        const String collectionName = "shutdown-queue";

        await using var firstServiceProvider = CreateServiceProvider(uniqueDbName, collectionName, leaseTime, firstHandler);
        var helper = firstServiceProvider.GetRequiredService<IMongoHelper>();
        var collection = helper.Database.GetCollection<MongoQueueItem<ShutdownPayload>>(collectionName);
        var firstQueue = firstServiceProvider.GetRequiredService<IMongoQueue<ShutdownPayload>>();
        await firstQueue.StartSubscriptionAsync();

        await firstQueue.PublishAsync(new ShutdownPayload
        {
            Value = "shutdown-me"
        });
        await firstHandler.WaitForStart(TimeSpan.FromSeconds(10));

        // Act: shutting down the subscription cancels the in-flight handler. Because the handler was already
        // invoked with an acquired lock, this is a shutdown-caused cancellation, not a handler failure.
        await firstQueue.StopSubscriptionAsync().WaitAsync(TimeSpan.FromSeconds(15));

        // Assert: the lock was released immediately, not left closed/terminal, and not counted as a retry.
        var releasedItem = await collection.Find(x => x.Payload.Value == "shutdown-me").SingleAsync();
        releasedItem.IsLocked.Should().BeFalse();
        releasedItem.LockedUtc.Should().BeNull();
        releasedItem.IsClosed.Should().BeFalse();
        releasedItem.IsTerminal.Should().BeFalse();
        releasedItem.RetryCount.Should().Be(0, "a shutdown-caused cancellation must not be counted as a retry");

        // Assert: the item is actually selectable again right away. A fresh subscription against the same
        // collection picks it up well inside the 30-second lease, proving the release did not require waiting
        // out the stale lock's lease to expire.
        var secondHandler = new ShutdownAwarePayloadHandler();
        await using var secondServiceProvider = CreateServiceProvider(uniqueDbName, collectionName, leaseTime, secondHandler);
        var secondQueue = secondServiceProvider.GetRequiredService<IMongoQueue<ShutdownPayload>>();
        await secondQueue.StartSubscriptionAsync();
        try
        {
            await secondHandler.WaitForStart(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await secondQueue.StopSubscriptionAsync();
        }
    }

    private ServiceProvider CreateServiceProvider(
        String databaseName,
        String collectionName,
        TimeSpan lockLeaseTime,
        IMongoQueuePayloadHandler<ShutdownPayload> handler)
    {
        var services = new ServiceCollection();
        services.AddNUnitTestLogging();

        var url = MongoUrl.Create(_container.GetConnectionString());
        services.AddMongo(url, databaseName)
                .WithQueue<ShutdownPayload>(queue => queue
                                                     .WithPayloadHandler(_ => handler)
                                                     .WithCollectionName(collectionName)
                                                     .WithLockLeaseTime(lockLeaseTime)
                                                     .WithoutAutoStartSubscription());

        return services.BuildServiceProvider();
    }

    private sealed class ShutdownAwarePayloadHandler : IMongoQueuePayloadHandler<ShutdownPayload>
    {
        private readonly SemaphoreSlim _startSemaphore = new(0);

        public async Task WaitForStart(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            await _startSemaphore.WaitAsync(cts.Token);
        }

        public async Task HandlePayloadAsync(ShutdownPayload payload, CancellationToken cancellationToken = default)
        {
            _startSemaphore.Release();

            // Blocks until the ambient token is cancelled by subscription shutdown, simulating a handler that
            // is still in-flight when the process stops rather than one that cancelled or timed out on its own.
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    private sealed class ShutdownPayload
    {
        public String Value { get; init; } = String.Empty;
    }
}
