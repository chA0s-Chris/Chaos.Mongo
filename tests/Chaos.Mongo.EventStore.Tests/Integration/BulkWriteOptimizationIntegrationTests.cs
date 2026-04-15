// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Chaos.Mongo.EventStore.Errors;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using NUnit.Framework;
using Testcontainers.MongoDb;

public class BulkWriteOptimizationIntegrationTests
{
    private IAggregateRepository<OrderAggregate> _aggregateRepository = null!;
    private MongoDbContainer _container = null!;
    private IEventStore<OrderAggregate> _eventStore = null!;
    private IMongoHelper _mongoHelper = null!;

    [Test]
    public async Task AppendEventsAsync_WithBulkWriteOptimization_PersistsCheckpointAndCallbackWork()
    {
        var aggregateId = Guid.NewGuid();
        var outboxCollection = _mongoHelper.Database.GetCollection<OutboxMessage>("TestOutbox");
        var outboxMessageId = Guid.NewGuid();

        var aggregate = await _eventStore.AppendEventsAsync(
            [
                new OrderCreatedEvent
                {
                    Id = Guid.NewGuid(),
                    AggregateId = aggregateId,
                    Version = 1,
                    CustomerName = "BulkWrite",
                    TotalAmount = 120.00m
                },
                new OrderShippedEvent
                {
                    Id = Guid.NewGuid(),
                    AggregateId = aggregateId,
                    Version = 2
                },
                new OrderCompletedEvent
                {
                    Id = Guid.NewGuid(),
                    AggregateId = aggregateId,
                    Version = 3
                }
            ],
            async (session, _, _, ct) =>
            {
                await outboxCollection.InsertOneAsync(
                    session,
                    new OutboxMessage
                    {
                        Id = outboxMessageId,
                        AggregateId = aggregateId,
                        CreatedUtc = DateTime.UtcNow,
                        MessageType = "OrderCompleted"
                    },
                    cancellationToken: ct);
            });

        aggregate.Version.Should().Be(3);
        aggregate.Status.Should().Be("Completed");

        var readModel = await _aggregateRepository.GetAsync(aggregateId);
        readModel.Should().NotBeNull();
        readModel!.Version.Should().Be(3);
        readModel.Status.Should().Be("Completed");

        var checkpointCollection = _mongoHelper.Database.GetCollection<CheckpointDocument<OrderAggregate>>("Orders_Checkpoints");
        var checkpoint = await checkpointCollection.Find(c => c.Id.AggregateId == aggregateId && c.Id.Version == 3)
                                                  .FirstOrDefaultAsync();
        checkpoint.Should().NotBeNull();
        checkpoint!.State.Version.Should().Be(3);

        var outboxMessage = await outboxCollection.Find(m => m.Id == outboxMessageId).FirstOrDefaultAsync();
        outboxMessage.Should().NotBeNull();
        outboxMessage!.AggregateId.Should().Be(aggregateId);
    }

    [Test]
    public async Task AppendEventsAsync_WithBulkWriteOptimization_DuplicateEventId_ThrowsMongoDuplicateEventException()
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
    public async Task AppendEventsAsync_WithBulkWriteOptimization_RaceCondition_ThrowsMongoConcurrencyException()
    {
        var aggregateId = Guid.NewGuid();

        await _eventStore.AppendEventsAsync(
        [
            new OrderCreatedEvent
            {
                Id = Guid.NewGuid(),
                AggregateId = aggregateId,
                Version = 1,
                CustomerName = "RaceTest",
                TotalAmount = 100.00m
            }
        ]);

        var eventsCollection = _mongoHelper.Database.GetCollection<Event<OrderAggregate>>("Orders_Events");
        await eventsCollection.InsertOneAsync(new OrderCompletedEvent
        {
            Id = Guid.NewGuid(),
            AggregateId = aggregateId,
            AggregateType = "OrderAggregate",
            Version = 2,
            CreatedUtc = DateTime.UtcNow
        });

        var act = () => _eventStore.AppendEventsAsync(
        [
            new OrderShippedEvent
            {
                Id = Guid.NewGuid(),
                AggregateId = aggregateId,
                Version = 2
            }
        ]);

        await act.Should().ThrowAsync<MongoConcurrencyException>()
                 .WithMessage("*concurrency conflict*");
    }

    [OneTimeSetUp]
    public async Task GetMongoDbContainer() => _container = await MongoDbTestContainer.StartContainerAsync();

    [SetUp]
    public void Setup()
    {
        var url = MongoUrl.Create(_container.GetConnectionString());
        var sp = new ServiceCollection()
                 .AddMongo(url, configure: options =>
                 {
                     options.DefaultDatabase = $"EventStoreBulkWriteTestDb_{Guid.NewGuid():N}";
                     options.RunConfiguratorsOnStartup = false;
                 })
                 .WithEventStore<OrderAggregate>(es => es
                                                       .WithEvent<OrderCreatedEvent>("OrderCreated")
                                                       .WithEvent<OrderShippedEvent>("OrderShipped")
                                                       .WithEvent<OrderCompletedEvent>("OrderCompleted")
                                                       .WithCollectionPrefix("Orders")
                                                       .WithCheckpoints(3)
                                                       .WithBulkWriteOptimization())
                 .Services
                 .BuildServiceProvider();

        _eventStore = sp.GetRequiredService<IEventStore<OrderAggregate>>();
        _aggregateRepository = sp.GetRequiredService<IAggregateRepository<OrderAggregate>>();
        _mongoHelper = sp.GetRequiredService<IMongoHelper>();

        foreach (var configurator in sp.GetServices<Configuration.IMongoConfigurator>())
            configurator.ConfigureAsync(_mongoHelper).GetAwaiter().GetResult();
    }
}
