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

    [OneTimeTearDown]
    public Task DisposeMongoDbContainer()
        => _container.DisposeAsync().AsTask();

    [OneTimeSetUp]
    public async Task GetMongoDbContainer()
        => _container = await MongoDbTestContainer.StartContainerAsync();

    [Test]
    public async Task QueueHandlerFailure_ReprocessesMessageAfterLockLeaseExpires()
    {
        // Arrange
        var handler = new RecoveringPayloadHandler();
        var services = new ServiceCollection();
        services.AddNUnitTestLogging();

        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"QueueLeaseRecoveryTest_{Guid.NewGuid():N}";
        services.AddMongo(url, uniqueDbName)
                .WithQueue<LeasePayload>(queue => queue
                                                  .WithPayloadHandler(_ => handler)
                                                  .WithCollectionName("lease-queue")
                                                  .WithLockLeaseTime(TimeSpan.FromMilliseconds(250))
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
        handler.SuccessfulPayloads.Should().ContainSingle(x => x.Value == "recover-me");
        recoveredItem.IsClosed.Should().BeTrue();
        recoveredItem.IsLocked.Should().BeFalse();
        recoveredItem.ClosedUtc.Should().NotBeNull();
        recoveredItem.LockedUtc.Should().BeNull();

        // Cleanup
        await queue.StopSubscriptionAsync();
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
        while (!cts.Token.IsCancellationRequested)
        {
            var queueItem = await collection.Find(filter).FirstOrDefaultAsync(cts.Token);
            if (queueItem is not null)
            {
                return queueItem;
            }

            await Task.Delay(100, cts.Token);
        }

        throw new TimeoutException("Queue item did not reach the expected state.");
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
        private readonly List<LeasePayload> _successfulPayloads = [];
        private readonly SemaphoreSlim _successSemaphore = new(0);

        public Int32 Attempts { get; private set; }
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
