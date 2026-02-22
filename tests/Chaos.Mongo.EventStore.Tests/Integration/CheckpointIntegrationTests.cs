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
        checkpoints[0].State.Status.Should().Be("Completed");
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
}
