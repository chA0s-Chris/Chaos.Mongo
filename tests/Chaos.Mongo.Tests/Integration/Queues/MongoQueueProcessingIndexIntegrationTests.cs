// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests.Integration.Queues;

using Chaos.Mongo.Queues;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using NUnit.Framework;
using Testcontainers.MongoDb;

/// <summary>
/// Verifies that the queue's processing index has an explicit, frozen name, and that an existing collection
/// whose processing index was created under the old server-generated name is transparently migrated to it.
/// </summary>
[Category("Integration")]
public class MongoQueueProcessingIndexIntegrationTests
{
    private const String ProcessingIndexName = "IX_Queue_Processing";
    private MongoDbContainer _container;

    [OneTimeSetUp]
    public async Task GetMongoDbContainer()
        => _container = await MongoDbTestContainer.StartContainerAsync();

    [Test]
    public async Task StartSubscription_OnCollectionWithLegacyGeneratedNameIndex_RenamesToExplicitNameAndDropsOldIndex()
    {
        // Arrange: pre-create the exact old, unnamed, four-key processing index this library has always created
        // here (as it did before this library named it and added the Id sort key), letting MongoDB assign it a
        // generated name, simulating an existing database from before this change. Also seed indexes a consumer
        // could have created independently that must survive: one sharing the same four keys but under its own
        // custom name with a different partial filter, one sharing the same four keys but with a unique option
        // the legacy definition never had, and one with an unrelated key shape entirely.
        var uniqueDbName = $"QueueLegacyProcessingIndexTest_{Guid.NewGuid():N}";
        const String collectionName = "processing-index-queue";
        const String unrelatedCustomNamedIndexName = "IX_App_OpenItems";
        const String unrelatedUniqueIndexName = "IX_App_UniqueShape";
        const String unrelatedDifferentKeysIndexName = "IX_App_DifferentKeys";

        await using var serviceProvider = CreateServiceProvider(uniqueDbName, collectionName);
        var helper = serviceProvider.GetRequiredService<IMongoHelper>();
        var rawCollection = helper.Database.GetCollection<BsonDocument>(collectionName);
        var legacyName = await rawCollection.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys
                                                                     .Ascending("IsClosed")
                                                                     .Ascending("IsLocked")
                                                                     .Ascending("LockedUtc")
                                                                     .Ascending("IsTerminal")));
        legacyName.Should().NotBe(ProcessingIndexName, "the server must have assigned a generated name, not the explicit one under test");

        await rawCollection.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                                      .Ascending("IsClosed")
                                      .Ascending("IsLocked")
                                      .Ascending("LockedUtc")
                                      .Ascending("IsTerminal"),
                new CreateIndexOptions<BsonDocument>
                {
                    Name = unrelatedCustomNamedIndexName,
                    PartialFilterExpression = new BsonDocument("IsClosed", false)
                }));
        await rawCollection.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                                      .Ascending("IsClosed")
                                      .Ascending("IsLocked")
                                      .Ascending("LockedUtc")
                                      .Ascending("IsTerminal"),
                new CreateIndexOptions<BsonDocument>
                {
                    Name = unrelatedUniqueIndexName,
                    Unique = true,
                    Sparse = true
                }));
        await rawCollection.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("SomeOtherField"),
                new CreateIndexOptions<BsonDocument>
                {
                    Name = unrelatedDifferentKeysIndexName
                }));

        var queue = serviceProvider.GetRequiredService<IMongoQueue<ProcessingIndexPayload>>();

        // Act
        await queue.StartSubscriptionAsync();
        try
        {
            var indexes = await (await rawCollection.Indexes.ListAsync()).ToListAsync();

            // Assert
            indexes.Should().NotContain(x => x["name"].AsString == legacyName, "the old generated-name index must be dropped, not left behind");
            var processingIndex = indexes.Should().ContainSingle(x => x["name"].AsString == ProcessingIndexName).Subject;
            processingIndex["key"].AsBsonDocument.Should().BeEquivalentTo(new BsonDocument
            {
                { "IsClosed", 1 },
                { "IsLocked", 1 },
                { "LockedUtc", 1 },
                { "IsTerminal", 1 },
                { "_id", 1 }
            });
            indexes.Should().Contain(x => x["name"].AsString == unrelatedCustomNamedIndexName,
                                     "a custom-named index sharing the legacy keys under a different partial filter must survive");
            indexes.Should().Contain(x => x["name"].AsString == unrelatedUniqueIndexName,
                                     "a unique/sparse index sharing the legacy keys must survive, since the legacy definition never requested those options");
            indexes.Should().Contain(x => x["name"].AsString == unrelatedDifferentKeysIndexName,
                                     "an index with an unrelated key shape must survive");
            indexes.Count(x => KeysMatchProcessingShape(x["key"].AsBsonDocument)).Should().Be(3,
                                                                                              "only the legacy generated-name index must be dropped: the new processing index plus the two surviving same-key unrelated indexes remain");
        }
        finally
        {
            await queue.StopSubscriptionAsync();
        }
    }

    [Test]
    public async Task StartSubscription_OnCollectionWithNonSimpleDefaultCollationAndLegacyIndex_StillDropsLegacyIndexAndIsIdempotent()
    {
        // Arrange: pre-create the collection with a non-simple default collation (MongoDB's listIndexes reports
        // that inherited collation on every index, including ones that never requested one explicitly), then
        // pre-create the exact old, unnamed, four-key processing index without an explicit collation, so it
        // inherits the collection's default exactly as a pre-#243 release's index would have.
        var uniqueDbName = $"QueueCollationLegacyIndexTest_{Guid.NewGuid():N}";
        const String collectionName = "processing-index-queue";

        await using var serviceProvider = CreateServiceProvider(uniqueDbName, collectionName);
        var helper = serviceProvider.GetRequiredService<IMongoHelper>();
        await helper.Database.CreateCollectionAsync(collectionName,
                                                    new CreateCollectionOptions
                                                    {
                                                        Collation = new Collation("en", strength: CollationStrength.Secondary)
                                                    });
        var rawCollection = helper.Database.GetCollection<BsonDocument>(collectionName);
        var legacyName = await rawCollection.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys
                                                                     .Ascending("IsClosed")
                                                                     .Ascending("IsLocked")
                                                                     .Ascending("LockedUtc")
                                                                     .Ascending("IsTerminal")));
        legacyName.Should().NotBe(ProcessingIndexName, "the server must have assigned a generated name, not the explicit one under test");

        var queue = serviceProvider.GetRequiredService<IMongoQueue<ProcessingIndexPayload>>();

        // Act
        await queue.StartSubscriptionAsync();
        try
        {
            var indexes = await (await rawCollection.Indexes.ListAsync()).ToListAsync();

            // Assert: the legacy index is gone even though it (like the new index) inherited the collection's
            // non-simple default collation, and the new named index exists.
            indexes.Should().NotContain(x => x["name"].AsString == legacyName,
                                        "the old generated-name index must be dropped despite inheriting the collection's default collation");
            indexes.Should().ContainSingle(x => x["name"].AsString == ProcessingIndexName);
        }
        finally
        {
            await queue.StopSubscriptionAsync();
        }

        // Act: a second subscription against the same, now-migrated collection must not fail now that the
        // legacy index is already gone.
        await using var secondServiceProvider = CreateServiceProvider(uniqueDbName, collectionName);
        var secondQueue = secondServiceProvider.GetRequiredService<IMongoQueue<ProcessingIndexPayload>>();
        var act = async () => await secondQueue.StartSubscriptionAsync();

        // Assert
        await act.Should().NotThrowAsync("repeated initialization must stay safe once the legacy index is already gone");
        await secondQueue.StopSubscriptionAsync();
    }

    [Test]
    public async Task StartSubscription_OnCollectionWithPrepareUniqueLegacyIndex_PreservesConstraintAndDoesNotDropIt()
    {
        // Arrange: pre-create the exact old, unnamed, four-key processing index, then enable prepareUnique on
        // it directly via collMod, simulating a consumer who turned the unmodified legacy index into a
        // soon-to-be-unique constraint. MongoDB's listIndexes reports prepareUnique on the index itself without
        // ever adding a "unique" key, so this is a distinct, server-recognized option the legacy definition
        // never had and must not be mistaken for the unmodified legacy index this library owns.
        var uniqueDbName = $"QueuePrepareUniqueTest_{Guid.NewGuid():N}";
        const String collectionName = "processing-index-queue";

        await using var serviceProvider = CreateServiceProvider(uniqueDbName, collectionName);
        var helper = serviceProvider.GetRequiredService<IMongoHelper>();
        var rawCollection = helper.Database.GetCollection<BsonDocument>(collectionName);
        var legacyName = await rawCollection.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys
                                                                     .Ascending("IsClosed")
                                                                     .Ascending("IsLocked")
                                                                     .Ascending("LockedUtc")
                                                                     .Ascending("IsTerminal")));
        legacyName.Should().NotBe(ProcessingIndexName, "the server must have assigned a generated name, not the explicit one under test");

        await helper.Database.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "collMod", collectionName },
            {
                "index", new BsonDocument
                {
                    { "name", legacyName },
                    { "prepareUnique", true }
                }
            }
        });
        await rawCollection.InsertOneAsync(new BsonDocument
        {
            { "IsClosed", false },
            { "IsLocked", false },
            { "IsTerminal", false }
        });

        var queue = serviceProvider.GetRequiredService<IMongoQueue<ProcessingIndexPayload>>();

        // Act
        await queue.StartSubscriptionAsync();
        try
        {
            var indexes = await (await rawCollection.Indexes.ListAsync()).ToListAsync();

            // Assert: the prepareUnique index survives under its original generated name, alongside the new
            // explicitly named processing index.
            var survivingLegacyIndex = indexes.Should().ContainSingle(x => x["name"].AsString == legacyName).Subject;
            survivingLegacyIndex["prepareUnique"].AsBoolean.Should().BeTrue(
                "a consumer-modified prepareUnique index must not be mistaken for the unmodified legacy index and dropped");
            indexes.Should().ContainSingle(x => x["name"].AsString == ProcessingIndexName);

            // Assert: the constraint prepareUnique was preparing for is still enforced; inserting another
            // document with the same four key values must still be rejected.
            var duplicateInsert = async () => await rawCollection.InsertOneAsync(
                new BsonDocument
                {
                    { "IsClosed", false },
                    { "IsLocked", false },
                    { "IsTerminal", false }
                });
            var exception = await duplicateInsert.Should().ThrowAsync<MongoWriteException>(
                "the surviving prepareUnique index must still reject a duplicate of the four key values");
            exception.Which.WriteError.Category.Should().Be(ServerErrorCategory.DuplicateKey);
        }
        finally
        {
            await queue.StopSubscriptionAsync();
        }
    }

    [Test]
    public async Task StartSubscription_OnFreshCollection_CreatesExplicitlyNamedProcessingIndex()
    {
        // Arrange
        var uniqueDbName = $"QueueFreshProcessingIndexTest_{Guid.NewGuid():N}";
        const String collectionName = "processing-index-queue";

        await using var serviceProvider = CreateServiceProvider(uniqueDbName, collectionName);
        var helper = serviceProvider.GetRequiredService<IMongoHelper>();
        var rawCollection = helper.Database.GetCollection<BsonDocument>(collectionName);
        var queue = serviceProvider.GetRequiredService<IMongoQueue<ProcessingIndexPayload>>();

        // Act
        await queue.StartSubscriptionAsync();
        try
        {
            var indexes = await (await rawCollection.Indexes.ListAsync()).ToListAsync();

            // Assert
            var processingIndex = indexes.Should().ContainSingle(x => x["name"].AsString == ProcessingIndexName).Subject;
            processingIndex["key"].AsBsonDocument.Should().BeEquivalentTo(new BsonDocument
            {
                { "IsClosed", 1 },
                { "IsLocked", 1 },
                { "LockedUtc", 1 },
                { "IsTerminal", 1 },
                { "_id", 1 }
            });
        }
        finally
        {
            await queue.StopSubscriptionAsync();
        }
    }

    private static Boolean KeysMatchProcessingShape(BsonDocument keys)
        => keys.Contains("IsClosed") && keys.Contains("IsLocked") && keys.Contains("LockedUtc") && keys.Contains("IsTerminal");

    private ServiceProvider CreateServiceProvider(String databaseName, String collectionName)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var url = MongoUrl.Create(_container.GetConnectionString());
        services.AddMongo(url, databaseName)
                .WithQueue<ProcessingIndexPayload>(queue => queue.WithCollectionName(collectionName)
                                                                 .WithPayloadHandler(_ => new PassiveProcessingIndexPayloadHandler())
                                                                 .WithoutAutoStartSubscription());

        return services.BuildServiceProvider();
    }

    private sealed class PassiveProcessingIndexPayloadHandler : IMongoQueuePayloadHandler<ProcessingIndexPayload>
    {
        public Task HandlePayloadAsync(ProcessingIndexPayload payload, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ProcessingIndexPayload;
}
