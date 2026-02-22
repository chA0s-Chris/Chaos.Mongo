// Copyright (c) 2025-2026 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using NUnit.Framework;
using Testcontainers.MongoDb;

public class EventStoreCreatedUtcTests
{
    private MongoDbContainer _container;
    private IEventStore<OrderAggregate> _eventStore;

    [OneTimeSetUp]
    public async Task GetMongoDbContainer() => _container = await MongoDbTestContainer.StartContainerAsync();

    [SetUp]
    public void Setup()
    {
        var url = MongoUrl.Create(_container.GetConnectionString());
        var sp = new ServiceCollection()
                 .AddMongo(url, configure: options =>
                 {
                     options.DefaultDatabase = $"CreatedUtcTestDb_{Guid.NewGuid():N}";
                     options.RunConfiguratorsOnStartup = false;
                 })
                 .WithEventStore<OrderAggregate>(es => es
                                                       .WithEvent<OrderCreatedEvent>("OrderCreated")
                                                       .WithCollectionPrefix("Orders"))
                 .Services
                 .BuildServiceProvider();

        _eventStore = sp.GetRequiredService<IEventStore<OrderAggregate>>();

        foreach (var configurator in sp.GetServices<Configuration.IMongoConfigurator>())
            configurator.ConfigureAsync(sp.GetRequiredService<IMongoHelper>()).GetAwaiter().GetResult();
    }

    [Test]
    public async Task AppendEventsAsync_PreservesExistingCreatedUtc()
    {
        var aggregateId = Guid.NewGuid();
        var explicitTimestamp = new DateTime(2020, 1, 15, 12, 30, 0, DateTimeKind.Utc);

        await _eventStore.AppendEventsAsync(
        [
            new OrderCreatedEvent
            {
                Id = Guid.NewGuid(),
                AggregateId = aggregateId,
                Version = 1,
                CustomerName = "Test",
                TotalAmount = 10.00m,
                CreatedUtc = explicitTimestamp
            }
        ]);

        var events = new List<Event<OrderAggregate>>();
        await foreach (var evt in _eventStore.GetEventStream(aggregateId))
            events.Add(evt);

        events.Should().HaveCount(1);
        events[0].CreatedUtc.Should().Be(explicitTimestamp);
    }

    [Test]
    public async Task AppendEventsAsync_NullEvents_ThrowsArgumentNullException()
    {
        var act = () => _eventStore.AppendEventsAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
