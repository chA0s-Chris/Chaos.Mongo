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
    public async Task ConfigureAsync_CreatesNearestCheckpointLookupIndexes()
    {
        // Arrange
        var databaseName = $"EventStoreIndexContract_{Guid.NewGuid():N}";
        await using var serviceProvider = CreateServiceProvider(databaseName);
        var helper = serviceProvider.GetRequiredService<IMongoHelper>();
        var checkpointCollection = helper.Database.GetCollection<BsonDocument>("Orders_Checkpoints");

        // Act
        foreach (var configurator in serviceProvider.GetServices<IMongoConfigurator>())
        {
            await configurator.ConfigureAsync(helper);
        }

        var indexes = await (await checkpointCollection.Indexes.ListAsync()).ToListAsync();

        // Assert
        indexes.Single(x => x["name"] == IndexNames.CheckpointAggregateIdWithVersion)["key"]
               .AsBsonDocument.Should().BeEquivalentTo(new BsonDocument
               {
                   { "_id.AggregateId", 1 },
                   { "_id.Version", -1 }
               });
        indexes.Single(x => x["name"] == IndexNames.CheckpointAggregateIdWithRevision)["key"]
               .AsBsonDocument.Should().BeEquivalentTo(new BsonDocument
               {
                   { "_id.AggregateId", 1 },
                   { nameof(CheckpointDocument<>.Revision), -1 }
               });
    }

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

    [Test]
    public async Task ConfigureAsync_WithoutCheckpoints_CreatesNoCheckpointCollection()
    {
        // Arrange
        var databaseName = $"EventStoreIndexContract_{Guid.NewGuid():N}";
        await using var serviceProvider = CreateServiceProvider(databaseName, 0);
        var helper = serviceProvider.GetRequiredService<IMongoHelper>();

        // Act
        foreach (var configurator in serviceProvider.GetServices<IMongoConfigurator>())
        {
            await configurator.ConfigureAsync(helper);
        }

        // Assert
        var collectionNames = await (await helper.Database.ListCollectionNamesAsync()).ToListAsync();
        collectionNames.Should().NotContain("Orders_Checkpoints");
    }

    [OneTimeSetUp]
    public async Task GetMongoDbContainer()
        => _container = await MongoDbTestContainer.StartContainerAsync();

    private ServiceProvider CreateServiceProvider(String databaseName, Int32 checkpointInterval = 3)
    {
        var url = MongoUrl.Create(_container.GetConnectionString());
        return new ServiceCollection()
               .AddMongo(url, configure: options =>
               {
                   options.DefaultDatabase = databaseName;
                   options.RunConfiguratorsOnStartup = false;
               })
               .WithEventStore<OrderAggregate>(es =>
               {
                   es.WithEvent<OrderCreatedEvent>("OrderCreated")
                     .WithEvent<OrderShippedEvent>("OrderShipped")
                     .WithEvent<OrderCompletedEvent>("OrderCompleted")
                     .WithCollectionPrefix("Orders");

                   if (checkpointInterval > 0)
                   {
                       es.WithCheckpoints(checkpointInterval);
                   }
               })
               .Services
               .BuildServiceProvider();
    }
}
