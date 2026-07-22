// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Chaos.Mongo.Configuration;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using NUnit.Framework;
using Testcontainers.MongoDb;

/// <summary>
/// Covers event id generation for events appended without an explicit <see cref="Event{TAggregate}.Id"/>.
/// Both append paths are exercised because the bulk-write path serializes events itself and therefore
/// does not go through the driver's id generation.
/// </summary>
public class EventStoreGeneratedIdTests
{
    private MongoDbContainer _container = null!;

    [TestCase(true)]
    [TestCase(false)]
    public async Task AppendEventsAsync_PreservesExplicitEventIds(Boolean bulkWriteOptimizationEnabled)
    {
        var eventStore = CreateEventStore(bulkWriteOptimizationEnabled);
        var aggregateId = Guid.NewGuid();
        var explicitId = Guid.NewGuid();

        var @event = new OrderCreatedEvent
        {
            Id = explicitId,
            AggregateId = aggregateId,
            Version = 1,
            CustomerName = "Explicit",
            TotalAmount = 25.00m
        };

        await eventStore.AppendEventsAsync([@event]);

        @event.Id.Should().Be(explicitId);

        var persistedEvents = await ReadEventStreamAsync(eventStore, aggregateId);
        persistedEvents.Should().ContainSingle().Which.Id.Should().Be(explicitId);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task AppendEventsAsync_WhenEventIdIsEmpty_GeneratesDistinctIds(Boolean bulkWriteOptimizationEnabled)
    {
        var eventStore = CreateEventStore(bulkWriteOptimizationEnabled);
        var aggregateId = Guid.NewGuid();

        var created = new OrderCreatedEvent
        {
            AggregateId = aggregateId,
            Version = 1,
            CustomerName = "Generated",
            TotalAmount = 10.00m
        };
        var shipped = new OrderShippedEvent
        {
            AggregateId = aggregateId,
            Version = 2
        };
        var completed = new OrderCompletedEvent
        {
            AggregateId = aggregateId,
            Version = 3
        };

        await eventStore.AppendEventsAsync([created, shipped, completed]);

        var generatedIds = new[]
        {
            created.Id,
            shipped.Id,
            completed.Id
        };

        generatedIds.Should().NotContain(Guid.Empty);
        generatedIds.Should().OnlyHaveUniqueItems();

        // The generated ids must be what actually reached the database, otherwise the bulk-write
        // path would have persisted Guid.Empty and collided on the unique _id index.
        var persistedEvents = await ReadEventStreamAsync(eventStore, aggregateId);
        persistedEvents.Select(e => e.Id).Should().Equal(generatedIds);
    }

    [OneTimeSetUp]
    public async Task GetMongoDbContainer() => _container = await MongoDbTestContainer.StartContainerAsync();

    private static async Task<List<Event<OrderAggregate>>> ReadEventStreamAsync(
        IEventStore<OrderAggregate> eventStore,
        Guid aggregateId)
    {
        var events = new List<Event<OrderAggregate>>();
        await foreach (var @event in eventStore.GetEventStream(aggregateId))
        {
            events.Add(@event);
        }

        return events;
    }

    private IEventStore<OrderAggregate> CreateEventStore(Boolean bulkWriteOptimizationEnabled)
    {
        var url = MongoUrl.Create(_container.GetConnectionString());
        var sp = new ServiceCollection()
                 .AddMongo(url, configure: options =>
                 {
                     options.DefaultDatabase = $"GeneratedIdTestDb_{Guid.NewGuid():N}";
                     options.RunConfiguratorsOnStartup = false;
                 })
                 .WithEventStore<OrderAggregate>(es =>
                 {
                     es.WithEvent<OrderCreatedEvent>("OrderCreated")
                       .WithEvent<OrderShippedEvent>("OrderShipped")
                       .WithEvent<OrderCompletedEvent>("OrderCompleted")
                       .WithCollectionPrefix("Orders");

                     if (bulkWriteOptimizationEnabled)
                     {
                         es.WithBulkWriteOptimization();
                     }
                 })
                 .Services
                 .BuildServiceProvider();

        var mongoHelper = sp.GetRequiredService<IMongoHelper>();
        foreach (var configurator in sp.GetServices<IMongoConfigurator>())
        {
            configurator.ConfigureAsync(mongoHelper).GetAwaiter().GetResult();
        }

        return sp.GetRequiredService<IEventStore<OrderAggregate>>();
    }
}
