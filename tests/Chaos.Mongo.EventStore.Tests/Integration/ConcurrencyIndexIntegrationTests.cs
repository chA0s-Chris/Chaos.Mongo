// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Chaos.Mongo.EventStore.Errors;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using NUnit.Framework;
using Testcontainers.MongoDb;

/// <summary>
/// Covers the automatic concurrency index check that <see cref="MongoEventStore{TAggregate}"/> performs before an
/// append relies on the unique <c>(AggregateId, Version)</c> index, independent of whether configurators ran.
/// </summary>
public class ConcurrencyIndexIntegrationTests
{
    private MongoDbContainer _container;

    [Test]
    public async Task AppendEventsAsync_ConflictingIndexUnderConcurrencyIndexName_ThrowsAndLeavesExistingIndexIntact()
    {
        var databaseName = $"ConcurrencyIndex_{Guid.NewGuid():N}";
        await using var serviceProvider = CreateServiceProvider(databaseName);
        var helper = serviceProvider.GetRequiredService<IMongoHelper>();
        var eventsCollection = helper.Database.GetCollection<BsonDocument>("Orders_Events");

        await eventsCollection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                                                          Builders<BsonDocument>.IndexKeys.Ascending(nameof(Event<OrderAggregate>.CreatedUtc)),
                                                          new CreateIndexOptions
                                                          {
                                                              Name = IndexNames.AggregateIdWithVersionUnique
                                                          }));

        var store = serviceProvider.GetRequiredService<IEventStore<OrderAggregate>>();
        var act = () => store.AppendEventsAsync([CreateOrder(Guid.NewGuid())]);

        await act.Should().ThrowAsync<MongoConcurrencyIndexMissingException>()
                 .WithMessage($"*{IndexNames.AggregateIdWithVersionUnique}*different keys or options*");
        (await eventsCollection.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty)).Should().Be(0);

        var indexes = await (await eventsCollection.Indexes.ListAsync()).ToListAsync();
        var conflictingIndex = indexes.Single(x => x["name"] == IndexNames.AggregateIdWithVersionUnique);
        conflictingIndex["key"].AsBsonDocument.Should().BeEquivalentTo(new BsonDocument
        {
            { nameof(Event<OrderAggregate>.CreatedUtc), 1 }
        });
        conflictingIndex.Contains("unique").Should().BeFalse();
    }

    [Test]
    public async Task AppendEventsAsync_QuickStartSetup_CreatesConcurrencyIndexAutomatically()
    {
        var databaseName = $"ConcurrencyIndex_{Guid.NewGuid():N}";
        await using var serviceProvider = CreateServiceProvider(databaseName);
        var helper = serviceProvider.GetRequiredService<IMongoHelper>();
        var eventsCollection = helper.Database.GetCollection<BsonDocument>("Orders_Events");
        var store = serviceProvider.GetRequiredService<IEventStore<OrderAggregate>>();

        // No configurator ever ran; the quick start's default registration must still protect the first append.
        await store.AppendEventsAsync([CreateOrder(Guid.NewGuid())]);

        var indexes = await (await eventsCollection.Indexes.ListAsync()).ToListAsync();
        var concurrencyIndex = indexes.Single(x => x["name"] == IndexNames.AggregateIdWithVersionUnique);
        concurrencyIndex["key"].AsBsonDocument.Should().BeEquivalentTo(new BsonDocument
        {
            { nameof(Event<OrderAggregate>.AggregateId), 1 },
            { nameof(Event<OrderAggregate>.Version), 1 }
        });
        concurrencyIndex["unique"].AsBoolean.Should().BeTrue();
    }

    [Test]
    public async Task AppendEventsAsync_SecondDatabaseOnSameClient_StillCreatesItsOwnConcurrencyIndex()
    {
        var firstDatabaseName = $"ConcurrencyIndexA_{Guid.NewGuid():N}";
        var secondDatabaseName = $"ConcurrencyIndexB_{Guid.NewGuid():N}";
        await using var serviceProvider = CreateServiceProvider(firstDatabaseName);
        var firstHelper = serviceProvider.GetRequiredService<IMongoHelper>();
        var options = serviceProvider.GetRequiredService<MongoEventStoreOptions<OrderAggregate>>();
        var firstStore = serviceProvider.GetRequiredService<IEventStore<OrderAggregate>>();

        var secondHelper = new DatabaseScopedMongoHelper(firstHelper.Client, firstHelper.Client.GetDatabase(secondDatabaseName));
        var secondStore = new MongoEventStore<OrderAggregate>(secondHelper, options);

        // Ensuring the index for the first database's events collection must not suppress the check for the
        // second database's same-named events collection on the same client.
        await firstStore.AppendEventsAsync([CreateOrder(Guid.NewGuid())]);
        await secondStore.AppendEventsAsync([CreateOrder(Guid.NewGuid())]);

        foreach (var database in new[]
                 {
                     firstHelper.Database,
                     secondHelper.Database
                 })
        {
            var eventsCollection = database.GetCollection<BsonDocument>("Orders_Events");
            var indexes = await (await eventsCollection.Indexes.ListAsync()).ToListAsync();
            indexes.Should().Contain(x => x["name"] == IndexNames.AggregateIdWithVersionUnique);
        }
    }

    [Test]
    public async Task AppendEventsAsync_VerificationOptedOut_DoesNotCreateConcurrencyIndex()
    {
        var databaseName = $"ConcurrencyIndex_{Guid.NewGuid():N}";
        await using var serviceProvider = CreateServiceProvider(databaseName, true);
        var helper = serviceProvider.GetRequiredService<IMongoHelper>();
        var eventsCollection = helper.Database.GetCollection<BsonDocument>("Orders_Events");
        var store = serviceProvider.GetRequiredService<IEventStore<OrderAggregate>>();

        await store.AppendEventsAsync([CreateOrder(Guid.NewGuid())]);

        var indexes = await (await eventsCollection.Indexes.ListAsync()).ToListAsync();
        indexes.Should().NotContain(x => x["name"] == IndexNames.AggregateIdWithVersionUnique);
    }

    [OneTimeSetUp]
    public async Task GetMongoDbContainer() => _container = await MongoDbTestContainer.StartContainerAsync();

    private static OrderCreatedEvent CreateOrder(Guid aggregateId)
        => new()
        {
            Id = Guid.NewGuid(),
            AggregateId = aggregateId,
            CustomerName = "Ada",
            TotalAmount = 12.34m
        };

    private ServiceProvider CreateServiceProvider(String databaseName, Boolean optOut = false)
        => new ServiceCollection()
           .AddMongo(MongoUrl.Create(_container.GetConnectionString()), configure: options =>
           {
               options.DefaultDatabase = databaseName;
               options.RunConfiguratorsOnStartup = false;
           })
           .WithEventStore<OrderAggregate>(es =>
           {
               es.WithEvent<OrderCreatedEvent>("OrderCreated")
                 .WithCollectionPrefix("Orders");

               if (optOut)
               {
                   es.WithoutConcurrencyIndexVerification();
               }
           })
           .Services
           .BuildServiceProvider();

    /// <summary>
    /// Wraps an existing <see cref="IMongoClient"/> with a different <see cref="IMongoDatabase"/>, so a test can
    /// exercise two databases served by the exact same client, as production code does when registering multiple
    /// aggregate types or tenants against one <see cref="IMongoClient"/>.
    /// </summary>
    private sealed class DatabaseScopedMongoHelper : IMongoHelper
    {
        public DatabaseScopedMongoHelper(IMongoClient client, IMongoDatabase database)
        {
            Client = client;
            Database = database;
        }

        public IMongoClient Client { get; }

        public IMongoDatabase Database { get; }

        public IMongoCollection<TDocument> GetCollection<TDocument>(MongoCollectionSettings? settings = null)
            => Database.GetCollection<TDocument>(typeof(TDocument).Name, settings);

        public Task<IMongoLock?> TryAcquireLockAsync(String lockName, TimeSpan? leaseTime = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Not needed by these tests.");
    }
}
