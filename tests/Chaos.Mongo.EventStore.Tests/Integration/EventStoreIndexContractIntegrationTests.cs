// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Chaos.Mongo.Configuration;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using NUnit.Framework;
using Testcontainers.MongoDb;

public class EventStoreIndexContractIntegrationTests
{
    private MongoDbContainer _container;

    [Test]
    public async Task ConfigureAsync_CreatesUniqueAggregateVersionIndex()
    {
        // Arrange
        var databaseName = $"EventStoreIndexContract_{Guid.NewGuid():N}";
        await using var serviceProvider = CreateServiceProvider(databaseName);
        var helper = serviceProvider.GetRequiredService<IMongoHelper>();
        var eventsCollection = helper.Database.GetCollection<BsonDocument>("Orders_Events");

        // Act
        foreach (var configurator in serviceProvider.GetServices<IMongoConfigurator>())
        {
            await configurator.ConfigureAsync(helper);
        }

        var indexes = await (await eventsCollection.Indexes.ListAsync()).ToListAsync();

        // Assert
        var aggregateVersionIndex = indexes.Single(x => x["name"] == IndexNames.AggregateIdWithVersionUnique);
        aggregateVersionIndex["key"].AsBsonDocument.Should().BeEquivalentTo(new BsonDocument
        {
            { nameof(Event<>.AggregateId), 1 },
            { nameof(Event<>.Version), 1 }
        });
        aggregateVersionIndex["unique"].AsBoolean.Should().BeTrue();
    }

    [OneTimeSetUp]
    public async Task GetMongoDbContainer()
        => _container = await MongoDbTestContainer.StartContainerAsync();

    private ServiceProvider CreateServiceProvider(String databaseName)
    {
        var url = MongoUrl.Create(_container.GetConnectionString());
        return new ServiceCollection()
               .AddMongo(url, configure: options =>
               {
                   options.DefaultDatabase = databaseName;
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
    }
}
