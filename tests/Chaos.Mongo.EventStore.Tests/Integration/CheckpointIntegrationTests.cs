// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using NUnit.Framework;
using Testcontainers.MongoDb;

public class CheckpointIntegrationTests
{
    private MongoDbContainer _container;
    private IAggregateRepository<OrderAggregate> _eventRepository;
    private IEventStore<OrderAggregate> _eventStore;
    private IMongoHelper _mongoHelper;

    [Test]
    public async Task AppendEventsAsync_BatchSkippingIntervalMultiple_CreatesCheckpoint()
    {
        var aggregateId = Guid.NewGuid();
        await _eventStore.AppendEventsAsync([Created(aggregateId, 1), Shipped(aggregateId, 2)]);

        // Revision 2 -> 4 jumps over the multiple 3 without ending on it.
        await _eventStore.AppendEventsAsync([Shipped(aggregateId, 3), Completed(aggregateId, 4)]);

        var checkpoints = await ReadCheckpointsAsync(aggregateId);
        checkpoints.Should().ContainSingle();
        checkpoints[0].Id.Version.Should().Be(4);
        checkpoints[0].Revision.Should().Be(4);
        checkpoints[0].State.Status.Should().Be("Completed");
    }

    [Test]
    public async Task AppendEventsAsync_MixedBatchEndingWithObservationalEvent_KeysCheckpointByLastStateChange()
    {
        var aggregateId = Guid.NewGuid();

        await _eventStore.AppendEventsAsync(
        [
            Created(aggregateId, 1),
            Viewed(aggregateId, 2),
            Shipped(aggregateId, 3),
            Completed(aggregateId, 4),
            Viewed(aggregateId, 5)
        ]);

        var checkpoints = await ReadCheckpointsAsync(aggregateId);
        checkpoints.Should().ContainSingle();
        checkpoints[0].Id.Version.Should().Be(4);
        checkpoints[0].Revision.Should().Be(3);
    }

    [Test]
    public async Task AppendEventsAsync_ObservationalEventsOnly_NeverCreateCheckpoint()
    {
        var aggregateId = Guid.NewGuid();
        await _eventStore.AppendEventsAsync([Created(aggregateId, 1), Shipped(aggregateId, 2)]);

        await _eventStore.AppendEventsAsync([Viewed(aggregateId, 3), Viewed(aggregateId, 4)]);
        await _eventStore.AppendEventsAsync([Viewed(aggregateId, 5)]);

        (await ReadCheckpointsAsync(aggregateId)).Should().BeEmpty();

        await _eventStore.AppendEventsAsync([Completed(aggregateId, 6)]);

        var checkpoints = await ReadCheckpointsAsync(aggregateId);
        checkpoints.Should().ContainSingle();
        checkpoints[0].Id.Version.Should().Be(6);
        checkpoints[0].Revision.Should().Be(3);
        checkpoints[0].State.Version.Should().Be(6);
        checkpoints[0].State.Revision.Should().Be(3);
    }

    [Test]
    public async Task AppendEvents_CreatesCheckpointAtInterval()
    {
        var aggregateId = Guid.NewGuid();

        // Append 3 events (checkpoint interval is 3)
        await _eventStore.AppendEventsAsync(
        [
            new OrderCreatedEvent
            {
                Id = Guid.NewGuid(),
                AggregateId = aggregateId,
                Version = 1,
                CustomerName = "Alice",
                TotalAmount = 99.99m
            }
        ]);

        await _eventStore.AppendEventsAsync(
        [
            new OrderShippedEvent
            {
                Id = Guid.NewGuid(),
                AggregateId = aggregateId,
                Version = 2
            }
        ]);

        await _eventStore.AppendEventsAsync(
        [
            new OrderCompletedEvent
            {
                Id = Guid.NewGuid(),
                AggregateId = aggregateId,
                Version = 3
            }
        ]);

        // Verify checkpoint was created at version 3
        var checkpointCollection = _mongoHelper.Database.GetCollection<CheckpointDocument<OrderAggregate>>("Orders_Checkpoints");
        var checkpoints = await checkpointCollection.Find(
                                                        Builders<CheckpointDocument<OrderAggregate>>.Filter.Eq(c => c.Id.AggregateId, aggregateId))
                                                    .ToListAsync();

        checkpoints.Should().HaveCount(1);
        checkpoints[0].Id.Version.Should().Be(3);
        checkpoints[0].Revision.Should().Be(3);
        checkpoints[0].State.Status.Should().Be("Completed");
        checkpoints[0].State.Revision.Should().Be(3);
    }

    [Test]
    public async Task AppendEvents_NoCheckpointBeforeInterval()
    {
        var aggregateId = Guid.NewGuid();

        // Append 2 events (checkpoint interval is 3, so no checkpoint yet)
        await _eventStore.AppendEventsAsync(
        [
            new OrderCreatedEvent
            {
                Id = Guid.NewGuid(),
                AggregateId = aggregateId,
                Version = 1,
                CustomerName = "Bob",
                TotalAmount = 50.00m
            }
        ]);

        await _eventStore.AppendEventsAsync(
        [
            new OrderShippedEvent
            {
                Id = Guid.NewGuid(),
                AggregateId = aggregateId,
                Version = 2
            }
        ]);

        var checkpointCollection = _mongoHelper.Database.GetCollection<CheckpointDocument<OrderAggregate>>("Orders_Checkpoints");
        var checkpoints = await checkpointCollection.Find(
                                                        Builders<CheckpointDocument<OrderAggregate>>.Filter.Eq(c => c.Id.AggregateId, aggregateId))
                                                    .ToListAsync();

        checkpoints.Should().BeEmpty();
    }

    [Test]
    public async Task GetAtVersionAsync_FromCheckpoint_SkipsObservationalEvents()
    {
        var aggregateId = Guid.NewGuid();
        await _eventStore.AppendEventsAsync([Created(aggregateId, 1), Viewed(aggregateId, 2), Shipped(aggregateId, 3)]);
        await _eventStore.AppendEventsAsync([Viewed(aggregateId, 4)]);
        await _eventStore.AppendEventsAsync([Completed(aggregateId, 5)]);
        await _eventStore.AppendEventsAsync([Viewed(aggregateId, 6)]);

        var atHead = await _eventRepository.GetAtVersionAsync(aggregateId, 6);
        var atCheckpoint = await _eventRepository.GetAtVersionAsync(aggregateId, 4);

        var readModel = await _eventRepository.GetAsync(aggregateId);
        readModel.Should().NotBeNull();
        atHead.Should().NotBeNull();
        atHead.Version.Should().Be(readModel.Version).And.Be(5);
        atHead.Revision.Should().Be(readModel.Revision).And.Be(3);
        atHead.Status.Should().Be("Completed");
        atCheckpoint.Should().NotBeNull();
        atCheckpoint.Version.Should().Be(3);
        atCheckpoint.Revision.Should().Be(2);
        atCheckpoint.Status.Should().Be("Shipped");
    }

    [OneTimeSetUp]
    public async Task GetMongoDbContainer() => _container = await MongoDbTestContainer.StartContainerAsync();

    [Test]
    public async Task Repository_GetAtVersion_UsesCheckpoint()
    {
        var aggregateId = Guid.NewGuid();

        // Create 6 events to get 2 checkpoints (at version 3 and 6)
        for (var i = 1; i <= 6; i++)
        {
            Event<OrderAggregate> evt = i == 1
                ? new OrderCreatedEvent
                {
                    Id = Guid.NewGuid(),
                    AggregateId = aggregateId,
                    Version = i,
                    CustomerName = "Charlie",
                    TotalAmount = 100.00m
                }
                : new OrderShippedEvent
                {
                    Id = Guid.NewGuid(),
                    AggregateId = aggregateId,
                    Version = i
                };

            await _eventStore.AppendEventsAsync([evt]);
        }

        // Get state at version 5 — should use checkpoint at version 3 and replay events 4-5
        var aggregate = await _eventRepository.GetAtVersionAsync(aggregateId, 5);
        aggregate.Should().NotBeNull();
        aggregate.Version.Should().Be(5);
        aggregate.CustomerName.Should().Be("Charlie");
    }

    [SetUp]
    public void Setup()
    {
        var url = MongoUrl.Create(_container.GetConnectionString());
        var sp = new ServiceCollection()
                 .AddMongo(url, configure: options =>
                 {
                     options.DefaultDatabase = $"CheckpointTestDb_{Guid.NewGuid():N}";
                     options.RunConfiguratorsOnStartup = false;
                 })
                 .WithEventStore<OrderAggregate>(es => es
                                                       .WithEvent<OrderCreatedEvent>("OrderCreated")
                                                       .WithEvent<OrderShippedEvent>("OrderShipped")
                                                       .WithEvent<OrderCompletedEvent>("OrderCompleted")
                                                       .WithEvent<OrderViewedEvent>("OrderViewed")
                                                       .WithCollectionPrefix("Orders")
                                                       .WithCheckpoints(3))
                 .Services
                 .BuildServiceProvider();

        _eventStore = sp.GetRequiredService<IEventStore<OrderAggregate>>();
        _eventRepository = sp.GetRequiredService<IAggregateRepository<OrderAggregate>>();
        _mongoHelper = sp.GetRequiredService<IMongoHelper>();

        // Manually run configurators to create indexes
        foreach (var configurator in sp.GetServices<Configuration.IMongoConfigurator>())
            configurator.ConfigureAsync(_mongoHelper).GetAwaiter().GetResult();
    }

    private static OrderCompletedEvent Completed(Guid aggregateId, Int64 version)
        => new()
        {
            AggregateId = aggregateId,
            Version = version
        };

    private static OrderCreatedEvent Created(Guid aggregateId, Int64 version)
        => new()
        {
            AggregateId = aggregateId,
            Version = version,
            CustomerName = "Checkpointed",
            TotalAmount = 10.00m
        };

    private static OrderShippedEvent Shipped(Guid aggregateId, Int64 version)
        => new()
        {
            AggregateId = aggregateId,
            Version = version
        };

    private static OrderViewedEvent Viewed(Guid aggregateId, Int64 version)
        => new()
        {
            AggregateId = aggregateId,
            Version = version,
            ViewedBy = "auditor"
        };

    private Task<List<CheckpointDocument<OrderAggregate>>> ReadCheckpointsAsync(Guid aggregateId)
        => _mongoHelper.Database
                       .GetCollection<CheckpointDocument<OrderAggregate>>("Orders_Checkpoints")
                       .Find(Builders<CheckpointDocument<OrderAggregate>>.Filter.Eq(c => c.Id.AggregateId, aggregateId))
                       .SortBy(c => c.Id.Version)
                       .ToListAsync();
}
