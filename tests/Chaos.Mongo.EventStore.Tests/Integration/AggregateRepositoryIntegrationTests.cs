// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using NUnit.Framework;
using Testcontainers.MongoDb;

public class AggregateRepositoryIntegrationTests
{
    private MongoDbContainer _container;
    private IEventStore<OrderAggregate> _eventStore;
    private IAggregateRepository<OrderAggregate> _repository;

    [Test]
    public async Task Collection_AllowsCustomQueries()
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
                TotalAmount = 100.00m
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
                TotalAmount = 200.00m
            }
        ]);

        // Use the exposed collection for a custom query
        var highValueOrders = await _repository.Collection
                                               .Find(Builders<OrderAggregate>.Filter.Gte(o => o.TotalAmount, 150m))
                                               .ToListAsync();

        highValueOrders.Should().HaveCount(1);
        highValueOrders[0].CustomerName.Should().Be("Bob");
    }

    [Test]
    public void Collection_ExposesUnderlyingMongoCollection()
    {
        var collection = _repository.Collection;

        collection.Should().NotBeNull();
        collection.Should().BeAssignableTo<IMongoCollection<OrderAggregate>>();
    }

    [Test]
    public async Task GetAsync_SetsCreatedUtcOnAggregate()
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
                CustomerName = "Charlie",
                TotalAmount = 50.00m
            }
        ]);

        var aggregate = await _repository.GetAsync(aggregateId);

        aggregate.Should().NotBeNull();
        aggregate.CreatedUtc.Should().BeCloseTo(before, TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task GetAtVersionAsync_SetsCreatedUtcFromFirstEvent()
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

        // Rebuild at version 2 from events
        var aggregate = await _repository.GetAtVersionAsync(aggregateId, 2);

        aggregate.Should().NotBeNull();
        aggregate.CreatedUtc.Should().BeCloseTo(before, TimeSpan.FromSeconds(2));
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
                     options.DefaultDatabase = $"RepoTestDb_{Guid.NewGuid():N}";
                     options.RunConfiguratorsOnStartup = false;
                 })
                 .WithEventStore<OrderAggregate>(es => es
                                                       .WithEvent<OrderCreatedEvent>("OrderCreated")
                                                       .WithEvent<OrderShippedEvent>("OrderShipped")
                                                       .WithCollectionPrefix("Orders"))
                 .Services
                 .BuildServiceProvider();

        _eventStore = sp.GetRequiredService<IEventStore<OrderAggregate>>();
        _repository = sp.GetRequiredService<IAggregateRepository<OrderAggregate>>();

        foreach (var configurator in sp.GetServices<Configuration.IMongoConfigurator>())
            configurator.ConfigureAsync(sp.GetRequiredService<IMongoHelper>()).GetAwaiter().GetResult();
    }
}
