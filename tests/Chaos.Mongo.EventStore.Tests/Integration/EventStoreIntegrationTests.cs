// Copyright (c) 2025-2026 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Chaos.Mongo.EventStore.Errors;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using NUnit.Framework;
using Testcontainers.MongoDb;

public class EventStoreIntegrationTests
{
    private MongoDbContainer _container;
    private IAggregateRepository<OrderAggregate> _eventRepository;
    private IEventStore<OrderAggregate> _eventStore;
    private IMongoHelper _mongoHelper;

    [Test]
    public async Task AppendEventsAsync_DuplicateEventId_ThrowsConcurrencyExceptionWithIsIdAffected()
    {
        var aggregateId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        await _eventStore.AppendEventsAsync(
        [
            new OrderCreatedEvent
            {
                Id = eventId,
                AggregateId = aggregateId,
                Version = 1,
                CustomerName = "Grace",
                TotalAmount = 40.00m
            }
        ]);

        // Try to insert another event with the same event ID
        var act = () => _eventStore.AppendEventsAsync(
        [
            new OrderShippedEvent
            {
                Id = eventId,
                AggregateId = aggregateId,
                Version = 2
            }
        ]);

        await act.Should().ThrowAsync<MongoDuplicateEventException>();
    }

    [Test]
    public async Task AppendEventsAsync_DuplicateVersion_ThrowsConcurrencyException()
    {
        var aggregateId = Guid.NewGuid();

        await _eventStore.AppendEventsAsync(
        [
            new OrderCreatedEvent
            {
                Id = Guid.NewGuid(),
                AggregateId = aggregateId,
                Version = 1,
                CustomerName = "Frank",
                TotalAmount = 30.00m
            }
        ]);

        // Try to insert another event with the same version
        var act = () => _eventStore.AppendEventsAsync(
        [
            new OrderShippedEvent
            {
                Id = Guid.NewGuid(),
                AggregateId = aggregateId,
                Version = 1
            }
        ]);

        await act.Should().ThrowAsync<MongoConcurrencyException>();
    }

    [Test]
    public async Task AppendEventsAsync_EmptyEvents_ThrowsArgumentException()
    {
        var act = () => _eventStore.AppendEventsAsync(Array.Empty<Event<OrderAggregate>>());

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Test]
    public async Task AppendEventsAsync_MultipleEvents_AppliesAllEventsToReadModel()
    {
        var aggregateId = Guid.NewGuid();

        // First batch: create order
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

        // Second batch: ship order
        await _eventStore.AppendEventsAsync(
        [
            new OrderShippedEvent
            {
                Id = Guid.NewGuid(),
                AggregateId = aggregateId,
                Version = 2
            }
        ]);

        var aggregate = await _eventRepository.GetAsync(aggregateId);
        aggregate.Should().NotBeNull();
        aggregate.Version.Should().Be(2);
        aggregate.Status.Should().Be("Shipped");
        aggregate.CustomerName.Should().Be("Bob");
    }

    [Test]
    public async Task AppendEventsAsync_SetsAggregateTypeAutomatically()
    {
        var aggregateId = Guid.NewGuid();

        await _eventStore.AppendEventsAsync(
        [
            new OrderCreatedEvent
            {
                Id = Guid.NewGuid(),
                AggregateId = aggregateId,
                Version = 1,
                CustomerName = "Ivy",
                TotalAmount = 60.00m
            }
        ]);

        var events = new List<Event<OrderAggregate>>();
        await foreach (var evt in _eventStore.GetEventStream(aggregateId))
            events.Add(evt);

        events[0].AggregateType.Should().Be("OrderAggregate");
    }

    [Test]
    public async Task AppendEventsAsync_SetsCreatedUtcAutomatically()
    {
        var aggregateId = Guid.NewGuid();
        var before = DateTime.UtcNow;

        await _eventStore.AppendEventsAsync(
        [
            new OrderCreatedEvent
            {
                Id = Guid.NewGuid(),
                AggregateId = aggregateId,
                Version = 1,
                CustomerName = "Hank",
                TotalAmount = 55.00m
            }
        ]);

        var events = new List<Event<OrderAggregate>>();
        await foreach (var evt in _eventStore.GetEventStream(aggregateId))
            events.Add(evt);

        events.Should().HaveCount(1);
        events[0].CreatedUtc.Should().BeCloseTo(before, TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task AppendEventsAsync_SingleEvent_InsertsEventAndUpdatesReadModel()
    {
        var aggregateId = Guid.NewGuid();
        var events = new[]
        {
            new OrderCreatedEvent
            {
                Id = Guid.NewGuid(),
                AggregateId = aggregateId,
                Version = 1,
                CustomerName = "Alice",
                TotalAmount = 99.99m
            }
        };

        var resultVersion = await _eventStore.AppendEventsAsync(events);

        resultVersion.Should().Be(1);

        // Verify read model
        var aggregate = await _eventRepository.GetAsync(aggregateId);
        aggregate.Should().NotBeNull();
        aggregate.Id.Should().Be(aggregateId);
        aggregate.Version.Should().Be(1);
        aggregate.CustomerName.Should().Be("Alice");
        aggregate.TotalAmount.Should().Be(99.99m);
        aggregate.Status.Should().Be("Created");
    }

    [Test]
    public async Task AppendEventsAsync_ValidationFailure_ThrowsAndDoesNotPersist()
    {
        var aggregateId = Guid.NewGuid();

        // Create and ship an order
        await _eventStore.AppendEventsAsync(
        [
            new OrderCreatedEvent
            {
                Id = Guid.NewGuid(),
                AggregateId = aggregateId,
                Version = 1,
                CustomerName = "ValidateTest",
                TotalAmount = 100.00m
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

        // Try to cancel a shipped order - should throw validation exception
        var cancelEvent = new OrderCancelledEvent
        {
            Id = Guid.NewGuid(),
            AggregateId = aggregateId,
            Version = 3
        };

        var act = () => _eventStore.AppendEventsAsync([cancelEvent]);

        await act.Should().ThrowAsync<MongoEventValidationException>()
                 .WithMessage("*shipped*");

        // Verify the event was not persisted
        var events = new List<Event<OrderAggregate>>();
        await foreach (var evt in _eventStore.GetEventStream(aggregateId))
            events.Add(evt);

        events.Should().HaveCount(2);

        // Verify aggregate state unchanged
        var aggregate = await _eventRepository.GetAsync(aggregateId);
        aggregate!.Status.Should().Be("Shipped");
        aggregate.Version.Should().Be(2);
    }

    [Test]
    public async Task AppendEventsAsync_WithOnBeforeCommit_ExecutesCallbackInTransaction()
    {
        var aggregateId = Guid.NewGuid();
        var outboxCollection = _mongoHelper.Database.GetCollection<OutboxMessage>("TestOutbox");
        var outboxMessageId = Guid.NewGuid();

        await _eventStore.AppendEventsAsync(
            [
                new OrderCreatedEvent
                {
                    Id = Guid.NewGuid(),
                    AggregateId = aggregateId,
                    Version = 1,
                    CustomerName = "OutboxTest",
                    TotalAmount = 50.00m
                }
            ],
            async (session, _, ct) =>
            {
                var message = new OutboxMessage
                {
                    Id = outboxMessageId,
                    AggregateId = aggregateId,
                    MessageType = "OrderCreated",
                    CreatedUtc = DateTime.UtcNow
                };
                await outboxCollection.InsertOneAsync(session, message, cancellationToken: ct);
            });

        // Verify the outbox message was persisted
        var outboxMessage = await outboxCollection
                                  .Find(m => m.Id == outboxMessageId)
                                  .FirstOrDefaultAsync();

        outboxMessage.Should().NotBeNull();
        outboxMessage.AggregateId.Should().Be(aggregateId);
        outboxMessage.MessageType.Should().Be("OrderCreated");

        // Verify the event was also persisted
        var events = new List<Event<OrderAggregate>>();
        await foreach (var evt in _eventStore.GetEventStream(aggregateId))
            events.Add(evt);

        events.Should().HaveCount(1);
    }

    [Test]
    public async Task GetEventStream_ReturnsEventsInOrder()
    {
        var aggregateId = Guid.NewGuid();

        await _eventStore.AppendEventsAsync(
        [
            new OrderCreatedEvent
            {
                Id = Guid.NewGuid(),
                AggregateId = aggregateId,
                Version = 1,
                CustomerName = "Diana",
                TotalAmount = 75.00m
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

        var events = new List<Event<OrderAggregate>>();
        await foreach (var evt in _eventStore.GetEventStream(aggregateId))
            events.Add(evt);

        events.Should().HaveCount(3);
        events[0].Should().BeOfType<OrderCreatedEvent>();
        events[1].Should().BeOfType<OrderShippedEvent>();
        events[2].Should().BeOfType<OrderCompletedEvent>();
        events[0].Version.Should().Be(1);
        events[1].Version.Should().Be(2);
        events[2].Version.Should().Be(3);
    }

    [Test]
    public async Task GetEventStream_WithVersionRange_ReturnsFilteredEvents()
    {
        var aggregateId = Guid.NewGuid();

        await _eventStore.AppendEventsAsync(
        [
            new OrderCreatedEvent
            {
                Id = Guid.NewGuid(),
                AggregateId = aggregateId,
                Version = 1,
                CustomerName = "Eve",
                TotalAmount = 20.00m
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

        var events = new List<Event<OrderAggregate>>();
        await foreach (var evt in _eventStore.GetEventStream(aggregateId, 2, 2))
            events.Add(evt);

        events.Should().HaveCount(1);
        events[0].Should().BeOfType<OrderShippedEvent>();
        events[0].Version.Should().Be(2);
    }

    [Test]
    public async Task GetExpectedNextVersionAsync_AfterEvents_ReturnsCorrectVersion()
    {
        var aggregateId = Guid.NewGuid();

        await _eventStore.AppendEventsAsync(
        [
            new OrderCreatedEvent
            {
                Id = Guid.NewGuid(),
                AggregateId = aggregateId,
                Version = 1,
                CustomerName = "Charlie",
                TotalAmount = 10.00m
            }
        ]);

        var nextVersion = await _eventStore.GetExpectedNextVersionAsync(aggregateId);
        nextVersion.Should().Be(2);
    }

    [Test]
    public async Task GetExpectedNextVersionAsync_NoEvents_Returns1()
    {
        var aggregateId = Guid.NewGuid();

        var version = await _eventStore.GetExpectedNextVersionAsync(aggregateId);

        version.Should().Be(1);
    }

    [OneTimeSetUp]
    public async Task GetMongoDbContainer() => _container = await MongoDbTestContainer.StartContainerAsync();

    [Test]
    public async Task MultipleAggregates_IndependentEventStreams()
    {
        var aggregateId1 = Guid.NewGuid();
        var aggregateId2 = Guid.NewGuid();

        await _eventStore.AppendEventsAsync(
        [
            new OrderCreatedEvent
            {
                Id = Guid.NewGuid(),
                AggregateId = aggregateId1,
                Version = 1,
                CustomerName = "Alice",
                TotalAmount = 10.00m
            }
        ]);

        await _eventStore.AppendEventsAsync(
        [
            new OrderCreatedEvent
            {
                Id = Guid.NewGuid(),
                AggregateId = aggregateId2,
                Version = 1,
                CustomerName = "Bob",
                TotalAmount = 20.00m
            }
        ]);

        var agg1 = await _eventRepository.GetAsync(aggregateId1);
        var agg2 = await _eventRepository.GetAsync(aggregateId2);

        agg1!.CustomerName.Should().Be("Alice");
        agg2!.CustomerName.Should().Be("Bob");

        var nextV1 = await _eventStore.GetExpectedNextVersionAsync(aggregateId1);
        var nextV2 = await _eventStore.GetExpectedNextVersionAsync(aggregateId2);

        nextV1.Should().Be(2);
        nextV2.Should().Be(2);
    }

    [Test]
    public async Task Repository_GetAsync_NoAggregate_ReturnsNull()
    {
        var result = await _eventRepository.GetAsync(Guid.NewGuid());
        result.Should().BeNull();
    }

    [Test]
    public async Task Repository_GetAtVersionAsync_NoEvents_ReturnsNull()
    {
        var result = await _eventRepository.GetAtVersionAsync(Guid.NewGuid(), 1);
        result.Should().BeNull();
    }

    [Test]
    public async Task Repository_GetAtVersionAsync_RebuildsStateAtVersion()
    {
        var aggregateId = Guid.NewGuid();

        await _eventStore.AppendEventsAsync(
        [
            new OrderCreatedEvent
            {
                Id = Guid.NewGuid(),
                AggregateId = aggregateId,
                Version = 1,
                CustomerName = "Jack",
                TotalAmount = 100.00m
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

        // Get state at version 1
        var atV1 = await _eventRepository.GetAtVersionAsync(aggregateId, 1);
        atV1.Should().NotBeNull();
        atV1.Version.Should().Be(1);
        atV1.Status.Should().Be("Created");

        // Get state at version 2
        var atV2 = await _eventRepository.GetAtVersionAsync(aggregateId, 2);
        atV2.Should().NotBeNull();
        atV2.Version.Should().Be(2);
        atV2.Status.Should().Be("Shipped");
    }

    [SetUp]
    public void Setup()
    {
        var url = MongoUrl.Create(_container.GetConnectionString());
        var sp = new ServiceCollection()
                 .AddMongo(url, configure: options =>
                 {
                     options.DefaultDatabase = $"EventStoreTestDb_{Guid.NewGuid():N}";
                     options.RunConfiguratorsOnStartup = false;
                 })
                 .WithEventStore<OrderAggregate>(es => es
                                                       .WithEvent<OrderCreatedEvent>("OrderCreated")
                                                       .WithEvent<OrderShippedEvent>("OrderShipped")
                                                       .WithEvent<OrderCompletedEvent>("OrderCompleted")
                                                       .WithEvent<OrderCancelledEvent>("OrderCancelled")
                                                       .WithCollectionPrefix("Orders"))
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
