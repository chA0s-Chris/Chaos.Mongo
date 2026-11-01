// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests.Integration;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using NUnit.Framework;
using Testcontainers.MongoDb;

public class MongoIndexManagerExtensionsIntegrationTests
{
    private MongoDbContainer _container;

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenCalledConcurrentlyForSameRename_ShouldAllSucceedWithSingleResultingIndex()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        var oldName = "idx_concurrent_old_" + Guid.NewGuid();
        var newName = "idx_concurrent_new_" + Guid.NewGuid();

        await indexManager.CreateOneAsync(new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = oldName,
                                                  Unique = true
                                              }));

        var renamedIndexModel = new CreateIndexModel<TestDocument>(
            Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
            new CreateIndexOptions
            {
                Name = newName,
                Unique = true
            });

        // Act - Issue the same rename concurrently, simulating two application instances starting at once
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => indexManager.CreateOneOrUpdateAsync(renamedIndexModel)));

        // Assert
        results.Should().AllSatisfy(name => name.Should().Be(newName));
        var indexes = await (await indexManager.ListAsync()).ToListAsync();
        indexes.Count(idx => idx["key"].AsBsonDocument.Contains("Name")).Should().Be(1);
        var index = indexes.First(idx => idx["name"].AsString == newName);
        (index.Contains("unique") && index["unique"].AsBoolean).Should().BeTrue();
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenCollationResolutionRequiresProbeAndCollectionIsAtIndexLimit_ShouldThrowOriginalConflictAndLeaveCollectionUnchanged()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        var oldName = "idx_capacity_old_" + Guid.NewGuid();
        var newName = "idx_capacity_new_" + Guid.NewGuid();

        // An explicit non-simple collation always needs the probe (see RequiresCollationProbe), so resolving
        // this rename's conflict will try to create the throwaway probe index.
        await indexManager.CreateOneAsync(new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = oldName,
                                                  Collation = new Collation("en")
                                              }));

        // Fill the collection to MongoDB's 64-index limit: _id_, the index above, and 62 unrelated filler
        // indexes on synthetic field names that cannot collide with any real index.
        var fillerModels = Enumerable.Range(0, 62)
                                     .Select(i => new CreateIndexModel<TestDocument>(
                                                 new BsonDocumentIndexKeysDefinition<TestDocument>(new BsonDocument($"filler_{i}", 1))))
                                     .ToList();
        await indexManager.CreateManyAsync(fillerModels);

        var indexesBefore = await (await indexManager.ListAsync()).ToListAsync();
        indexesBefore.Should().HaveCount(64);

        // Act - Rename the index, requesting the same collation again. Resolving it needs a spare index slot
        // for the throwaway probe, which the collection no longer has.
        var act = () => indexManager.CreateOneOrUpdateAsync(new CreateIndexModel<TestDocument>(
                                                                Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                                                new CreateIndexOptions
                                                                {
                                                                    Name = newName,
                                                                    Collation = new Collation("en")
                                                                }));

        // Assert - The original index conflict surfaces instead of the probe's own CannotCreateIndex failure,
        // and nothing was dropped or created.
        var assertion = await act.Should().ThrowAsync<MongoCommandException>();
        assertion.Which.CodeName.Should().BeOneOf("IndexOptionsConflict", "IndexKeySpecsConflict");
        var indexesAfter = await (await indexManager.ListAsync()).ToListAsync();
        indexesAfter.Should().HaveCount(64);
        indexesAfter.Should().Contain(idx => idx["name"].AsString == oldName);
        indexesAfter.Should().NotContain(idx => idx["name"].AsString == newName);
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenCollectionHasNonSimpleDefaultCollationAndSiblingHasExplicitSimpleCollation_ShouldPreserveSiblingAndRenameOldIndex()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        await mongoHelper.Database.CreateCollectionAsync(collectionName, new CreateCollectionOptions
        {
            Collation = new Collation("en")
        });
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        var unrelatedSimpleName = "idx_unrelated_simple_" + Guid.NewGuid();
        var oldName = "idx_inherited_collation_old_" + Guid.NewGuid();
        var newName = "idx_inherited_collation_new_" + Guid.NewGuid();

        // An explicit simple collation is a documented, supported way for a second index to coexist on the
        // same keys (listIndexes omits the collation field for it, the same as for no collation at all).
        await indexManager.CreateOneAsync(new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = unrelatedSimpleName,
                                                  Collation = Collation.Simple
                                              }));

        // No explicit per-index collation: this index inherits the collection's own "en" default collation, so
        // listIndexes reports a real, non-null collation document for it, distinct from the unrelated sibling.
        await indexManager.CreateOneAsync(new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = oldName
                                              }));

        // Act - Rename the inherited-collation index, keeping the collation omitted; the unrelated
        // explicitly-simple sibling on the same keys must survive, and the rename must still succeed.
        var result = await indexManager.CreateOneOrUpdateAsync(new CreateIndexModel<TestDocument>(
                                                                   Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                                                   new CreateIndexOptions
                                                                   {
                                                                       Name = newName
                                                                   }));

        // Assert
        result.Should().Be(newName);
        var indexes = await (await indexManager.ListAsync()).ToListAsync();
        indexes.Should().Contain(idx => idx["name"].AsString == unrelatedSimpleName);
        indexes.Should().NotContain(idx => idx["name"].AsString == oldName);
        indexes.Should().Contain(idx => idx["name"].AsString == newName);
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenDefaultNamedIndexOptionsChange_ShouldDropAndRecreateUnderSameDefaultName()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        await indexManager.CreateOneAsync(new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                              new CreateIndexOptions()));

        // Act - Create again with the same keys and no explicit name, but different options
        var result = await indexManager.CreateOneOrUpdateAsync(new CreateIndexModel<TestDocument>(
                                                                   Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                                                   new CreateIndexOptions
                                                                   {
                                                                       Unique = true
                                                                   }));

        // Assert
        result.Should().NotBeNullOrEmpty();
        var indexes = await (await indexManager.ListAsync()).ToListAsync();
        indexes.Count(idx => idx["key"].AsBsonDocument.Contains("Name")).Should().Be(1);
        var index = indexes.FirstOrDefault(idx => idx["name"].AsString == result);
        index.Should().NotBeNull();
        (index.Contains("unique") && index["unique"].AsBoolean).Should().BeTrue();
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenIndexDoesNotExist_ShouldCreateIndex()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        var indexModel = new CreateIndexModel<TestDocument>(
            Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
            new CreateIndexOptions
            {
                Name = "idx_name_unique_" + Guid.NewGuid(),
                Unique = true
            });

        // Act
        var result = await indexManager.CreateOneOrUpdateAsync(indexModel);

        // Assert
        result.Should().NotBeNullOrEmpty();
        var indexes = await (await indexManager.ListAsync()).ToListAsync();
        indexes.Should().Contain(idx => idx["name"].AsString == result);
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenIndexExistsWithDifferentKeySpecs_ShouldDropAndRecreate()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        var indexName = "idx_field_conflict_" + Guid.NewGuid();

        // Create index on Name field
        var firstIndexModel = new CreateIndexModel<TestDocument>(
            Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
            new CreateIndexOptions
            {
                Name = indexName
            });

        await indexManager.CreateOneAsync(firstIndexModel);

        // Act - Create index on Value field with same name (conflicting key spec)
        var secondIndexModel = new CreateIndexModel<TestDocument>(
            Builders<TestDocument>.IndexKeys.Ascending(x => x.Value),
            new CreateIndexOptions
            {
                Name = indexName
            });

        var result = await indexManager.CreateOneOrUpdateAsync(secondIndexModel);

        // Assert
        result.Should().Be(indexName);
        var indexes = await (await indexManager.ListAsync()).ToListAsync();
        var index = indexes.FirstOrDefault(idx => idx["name"].AsString == indexName);
        index.Should().NotBeNull();
        index["key"]["Value"].Should().NotBeNull();
        index["key"].AsBsonDocument.Contains("Name").Should().BeFalse();
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenIndexExistsWithDifferentOptions_ShouldDropAndRecreate()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        var indexName = "idx_name_different_" + Guid.NewGuid();

        // Create index with unique = true
        var firstIndexModel = new CreateIndexModel<TestDocument>(
            Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
            new CreateIndexOptions
            {
                Name = indexName,
                Unique = true
            });

        await indexManager.CreateOneAsync(firstIndexModel);

        // Act - Create index with unique = false (conflicting option)
        var secondIndexModel = new CreateIndexModel<TestDocument>(
            Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
            new CreateIndexOptions
            {
                Name = indexName,
                Unique = false
            });

        var result = await indexManager.CreateOneOrUpdateAsync(secondIndexModel);

        // Assert
        result.Should().Be(indexName);
        var indexes = await (await indexManager.ListAsync()).ToListAsync();
        var index = indexes.FirstOrDefault(idx => idx["name"].AsString == indexName);
        index.Should().NotBeNull();
        (index.Contains("unique") && index["unique"].AsBoolean).Should().BeFalse();
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenIndexExistsWithSameOptions_ShouldNotRecreate()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        var indexName = "idx_name_same_" + Guid.NewGuid();
        var indexModel = new CreateIndexModel<TestDocument>(
            Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
            new CreateIndexOptions
            {
                Name = indexName,
                Unique = true
            });

        // Create index first time
        var firstResult = await indexManager.CreateOneOrUpdateAsync(indexModel);

        // Act - Create same index again
        var secondResult = await indexManager.CreateOneOrUpdateAsync(indexModel);

        // Assert
        firstResult.Should().Be(indexName);
        secondResult.Should().Be(indexName);
        var indexes = await (await indexManager.ListAsync()).ToListAsync();
        indexes.Count(idx => idx["name"].AsString == indexName).Should().Be(1);
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenIndexIsRenamedWithDifferentExpireAfter_ShouldDropOldAndCreateNewWithNewExpireAfter()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        var oldName = "idx_ttl_old_" + Guid.NewGuid();
        var newName = "idx_ttl_new_" + Guid.NewGuid();

        await indexManager.CreateOneAsync(new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = oldName,
                                                  ExpireAfter = TimeSpan.FromSeconds(60)
                                              }));

        // Act - Rename the index, keeping the same key specification and identity options but a different TTL
        var result = await indexManager.CreateOneOrUpdateAsync(new CreateIndexModel<TestDocument>(
                                                                   Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                                                   new CreateIndexOptions
                                                                   {
                                                                       Name = newName,
                                                                       ExpireAfter = TimeSpan.FromSeconds(120)
                                                                   }));

        // Assert
        result.Should().Be(newName);
        var indexes = await (await indexManager.ListAsync()).ToListAsync();
        indexes.Should().NotContain(idx => idx["name"].AsString == oldName);
        var index = indexes.FirstOrDefault(idx => idx["name"].AsString == newName);
        index.Should().NotBeNull();
        index["expireAfterSeconds"].ToDouble().Should().Be(120);
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenIndexIsRenamedWithSameKeySpecs_ShouldDropOldNameAndCreateNewName()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        var oldName = "idx_rename_old_" + Guid.NewGuid();
        var newName = "idx_rename_new_" + Guid.NewGuid();

        await indexManager.CreateOneAsync(new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = oldName
                                              }));

        // Act - Rename the index while keeping the same key specification
        var result = await indexManager.CreateOneOrUpdateAsync(new CreateIndexModel<TestDocument>(
                                                                   Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                                                   new CreateIndexOptions
                                                                   {
                                                                       Name = newName
                                                                   }));

        // Assert
        result.Should().Be(newName);
        var indexes = await (await indexManager.ListAsync()).ToListAsync();
        indexes.Should().NotContain(idx => idx["name"].AsString == oldName);
        indexes.Should().Contain(idx => idx["name"].AsString == newName);
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenRenamedIndexDiffersOnlyBySparse_ShouldLeaveDifferentlySparseSiblingUntouched()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        var sparseSiblingName = "idx_sparse_sibling_" + Guid.NewGuid();
        var oldName = "idx_nonsparse_old_" + Guid.NewGuid();
        var newName = "idx_nonsparse_new_" + Guid.NewGuid();

        await indexManager.CreateOneAsync(new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = sparseSiblingName,
                                                  Sparse = true
                                              }));
        await indexManager.CreateOneAsync(new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = oldName,
                                                  Sparse = false
                                              }));

        // Act - Rename the non-sparse index; the differently-sparse sibling on the same keys must survive
        var result = await indexManager.CreateOneOrUpdateAsync(new CreateIndexModel<TestDocument>(
                                                                   Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                                                   new CreateIndexOptions
                                                                   {
                                                                       Name = newName,
                                                                       Sparse = false
                                                                   }));

        // Assert
        result.Should().Be(newName);
        var indexes = await (await indexManager.ListAsync()).ToListAsync();
        indexes.Should().Contain(idx => idx["name"].AsString == sparseSiblingName);
        indexes.Should().NotContain(idx => idx["name"].AsString == oldName);
        indexes.Should().Contain(idx => idx["name"].AsString == newName);
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenRenamedToMatchCollationWithSameLocaleDifferentStrength_ShouldLeaveDifferentlyStrengthSiblingUntouched()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        var strengthSiblingName = "idx_strength_sibling_" + Guid.NewGuid();
        var oldName = "idx_strength_old_" + Guid.NewGuid();
        var newName = "idx_strength_new_" + Guid.NewGuid();

        await indexManager.CreateOneAsync(new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = strengthSiblingName,
                                                  Collation = new Collation("en", strength: CollationStrength.Primary)
                                              }));
        await indexManager.CreateOneAsync(new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = oldName,
                                                  Collation = new Collation("en")
                                              }));

        // Act - Rename the default-strength ("en", Tertiary) index; the explicitly Primary-strength sibling on
        // the same keys and locale must survive, and the rename must still unambiguously succeed.
        var result = await indexManager.CreateOneOrUpdateAsync(new CreateIndexModel<TestDocument>(
                                                                   Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                                                   new CreateIndexOptions
                                                                   {
                                                                       Name = newName,
                                                                       Collation = new Collation("en")
                                                                   }));

        // Assert
        result.Should().Be(newName);
        var indexes = await (await indexManager.ListAsync()).ToListAsync();
        indexes.Should().Contain(idx => idx["name"].AsString == strengthSiblingName);
        indexes.Should().NotContain(idx => idx["name"].AsString == oldName);
        indexes.Should().Contain(idx => idx["name"].AsString == newName);
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenRenamedToMatchCollation_ShouldLeaveDifferentlyCollatedSiblingUntouched()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        var otherCollationName = "idx_collation_en_" + Guid.NewGuid();
        var oldName = "idx_collation_de_old_" + Guid.NewGuid();
        var newName = "idx_collation_de_new_" + Guid.NewGuid();

        await indexManager.CreateOneAsync(new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = otherCollationName,
                                                  Collation = new Collation("en")
                                              }));
        await indexManager.CreateOneAsync(new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = oldName,
                                                  Collation = new Collation("de")
                                              }));

        // Act - Rename the "de"-collated index; the "en"-collated sibling on the same keys must survive
        var result = await indexManager.CreateOneOrUpdateAsync(new CreateIndexModel<TestDocument>(
                                                                   Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                                                   new CreateIndexOptions
                                                                   {
                                                                       Name = newName,
                                                                       Collation = new Collation("de")
                                                                   }));

        // Assert
        result.Should().Be(newName);
        var indexes = await (await indexManager.ListAsync()).ToListAsync();
        indexes.Should().Contain(idx => idx["name"].AsString == otherCollationName);
        indexes.Should().NotContain(idx => idx["name"].AsString == oldName);
        indexes.Should().Contain(idx => idx["name"].AsString == newName);
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenRenamedWithExplicitSimpleCollation_ShouldDropOldNameAndCreateNewName()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        var oldName = "idx_simple_collation_old_" + Guid.NewGuid();
        var newName = "idx_simple_collation_new_" + Guid.NewGuid();

        await indexManager.CreateOneAsync(new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = oldName,
                                                  Collation = Collation.Simple
                                              }));

        // Act - Rename the index, keeping the same explicit simple collation (which the server omits from
        // listIndexes, the same way an entirely unspecified collation is omitted)
        var result = await indexManager.CreateOneOrUpdateAsync(new CreateIndexModel<TestDocument>(
                                                                   Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                                                   new CreateIndexOptions
                                                                   {
                                                                       Name = newName,
                                                                       Collation = Collation.Simple
                                                                   }));

        // Assert
        result.Should().Be(newName);
        var indexes = await (await indexManager.ListAsync()).ToListAsync();
        indexes.Should().NotContain(idx => idx["name"].AsString == oldName);
        indexes.Should().Contain(idx => idx["name"].AsString == newName);
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenRenamedWithLocaleCollationHavingNonDefaultParameters_ShouldDropOldNameAndCreateNewName()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        var oldName = "idx_locale_collation_old_" + Guid.NewGuid();
        var newName = "idx_locale_collation_new_" + Guid.NewGuid();

        // "fr_CA" only specifies a locale; the server fills every other collation parameter from its own
        // locale-specific defaults (notably "backwards", which defaults to true for this locale, unlike most
        // others), which this library cannot reproduce locally.
        await indexManager.CreateOneAsync(new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = oldName,
                                                  Collation = new Collation("fr_CA")
                                              }));

        // Act - Rename the index, requesting the same partially specified collation again
        var result = await indexManager.CreateOneOrUpdateAsync(new CreateIndexModel<TestDocument>(
                                                                   Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                                                   new CreateIndexOptions
                                                                   {
                                                                       Name = newName,
                                                                       Collation = new Collation("fr_CA")
                                                                   }));

        // Assert
        result.Should().Be(newName);
        var indexes = await (await indexManager.ListAsync()).ToListAsync();
        indexes.Should().NotContain(idx => idx["name"].AsString == oldName);
        var index = indexes.FirstOrDefault(idx => idx["name"].AsString == newName);
        index.Should().NotBeNull();
        index["collation"]["locale"].AsString.Should().Be("fr_CA");
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenSameName2dIndexBitsAndMinChange_ShouldDropAndRecreateWithNewValues()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        var indexName = "idx_2d_bits_min_" + Guid.NewGuid();
        var geoKeys = (IndexKeysDefinition<TestDocument>)new BsonDocument("Location", "2d");

        await indexManager.CreateOneAsync(new CreateIndexModel<TestDocument>(
                                              geoKeys,
                                              new CreateIndexOptions
                                              {
                                                  Name = indexName,
                                                  Bits = 26,
                                                  Min = -180,
                                                  Max = 180
                                              }));

        // Act - Request the same name/keys with different Bits/Min; the existing index's stored precision/bounds
        // must not be mistaken for a match with the requested ones, which would otherwise silently no-op and
        // leave the wrong precision/bounds in place.
        var result = await indexManager.CreateOneOrUpdateAsync(new CreateIndexModel<TestDocument>(
                                                                   geoKeys,
                                                                   new CreateIndexOptions
                                                                   {
                                                                       Name = indexName,
                                                                       Bits = 20,
                                                                       Min = -360,
                                                                       Max = 360
                                                                   }));

        // Assert
        result.Should().Be(indexName);
        var indexes = await (await indexManager.ListAsync()).ToListAsync();
        var index = indexes.FirstOrDefault(idx => idx["name"].AsString == indexName);
        index.Should().NotBeNull();
        index["bits"].ToInt32().Should().Be(20);
        index["min"].ToDouble().Should().Be(-360);
        index["max"].ToDouble().Should().Be(360);
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenSameNameCollationOmitsPreviouslySetStrength_ShouldDropAndRecreateWithResolvedDefaultStrength()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        var indexName = "idx_strength_reset_" + Guid.NewGuid();

        await indexManager.CreateOneAsync(new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = indexName,
                                                  Collation = new Collation("en", strength: CollationStrength.Primary)
                                              }));

        // Act - Request the same name/keys, omitting the explicit strength; the server's own locale default for
        // "en" (Tertiary = 3) differs from the existing index's Primary (1), so this must be treated as an actual
        // options change, not a false "already satisfied" match.
        var result = await indexManager.CreateOneOrUpdateAsync(new CreateIndexModel<TestDocument>(
                                                                   Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                                                   new CreateIndexOptions
                                                                   {
                                                                       Name = indexName,
                                                                       Collation = new Collation("en")
                                                                   }));

        // Assert
        result.Should().Be(indexName);
        var indexes = await (await indexManager.ListAsync()).ToListAsync();
        var index = indexes.FirstOrDefault(idx => idx["name"].AsString == indexName);
        index.Should().NotBeNull();
        index["collation"]["strength"].AsInt32.Should().Be(3);
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenSameNameIndexDiffersOnlyBySparse_ShouldDropAndRecreate()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        var indexName = "idx_sparse_" + Guid.NewGuid();

        await indexManager.CreateOneAsync(new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = indexName,
                                                  Sparse = true
                                              }));

        // Act - Create the same name with Sparse flipped (conflicting option)
        var result = await indexManager.CreateOneOrUpdateAsync(new CreateIndexModel<TestDocument>(
                                                                   Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                                                   new CreateIndexOptions
                                                                   {
                                                                       Name = indexName,
                                                                       Sparse = false
                                                                   }));

        // Assert
        result.Should().Be(indexName);
        var indexes = await (await indexManager.ListAsync()).ToListAsync();
        var index = indexes.FirstOrDefault(idx => idx["name"].AsString == indexName);
        index.Should().NotBeNull();
        (index.Contains("sparse") && index["sparse"].AsBoolean).Should().BeFalse();
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenSameNameIndexHiddenChanges_ShouldDropAndRecreateWithHiddenUpdated()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        var indexName = "idx_hidden_" + Guid.NewGuid();

        await indexManager.CreateOneAsync(new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = indexName,
                                                  Hidden = false
                                              }));

        // Act - Request the same name/keys with Hidden flipped to true; the existing index's stored (absent)
        // "hidden" field must not be mistaken for a match with the requested value, which would otherwise
        // silently no-op and leave the index visible to the query planner.
        var result = await indexManager.CreateOneOrUpdateAsync(new CreateIndexModel<TestDocument>(
                                                                   Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                                                   new CreateIndexOptions
                                                                   {
                                                                       Name = indexName,
                                                                       Hidden = true
                                                                   }));

        // Assert
        result.Should().Be(indexName);
        var indexes = await (await indexManager.ListAsync()).ToListAsync();
        var index = indexes.FirstOrDefault(idx => idx["name"].AsString == indexName);
        index.Should().NotBeNull();
        (index.Contains("hidden") && index["hidden"].AsBoolean).Should().BeTrue();
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenSameNameIndexStorageEngineChanges_ShouldDropAndRecreateWithNewStorageEngine()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        var indexName = "idx_storage_engine_" + Guid.NewGuid();

        await indexManager.CreateOneAsync(new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = indexName,
                                                  StorageEngine = new BsonDocument("wiredTiger", new BsonDocument("configString", "block_compressor=snappy"))
                                              }));

        // Act - Request the same name/keys with a different storage engine configuration; the existing index's
        // stored "snappy" compressor must not be mistaken for a match with the requested "zstd" one, which would
        // otherwise silently no-op and leave the old storage configuration in place.
        var result = await indexManager.CreateOneOrUpdateAsync(new CreateIndexModel<TestDocument>(
                                                                   Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                                                   new CreateIndexOptions
                                                                   {
                                                                       Name = indexName,
                                                                       StorageEngine = new BsonDocument("wiredTiger", new BsonDocument("configString", "block_compressor=zstd"))
                                                                   }));

        // Assert
        result.Should().Be(indexName);
        var indexes = await (await indexManager.ListAsync()).ToListAsync();
        var index = indexes.FirstOrDefault(idx => idx["name"].AsString == indexName);
        index.Should().NotBeNull();
        index["storageEngine"]["wiredTiger"]["configString"].AsString.Should().Be("block_compressor=zstd");
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenSameNameTextIndexDefaultLanguageChanges_ShouldDropAndRecreateWithNewLanguage()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        var indexName = "idx_text_language_" + Guid.NewGuid();

        await indexManager.CreateOneAsync(new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Text(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = indexName,
                                                  DefaultLanguage = "english"
                                              }));

        // Act - Request the same name/keys with a different DefaultLanguage; the existing index's stored
        // "english" stemming/stop-word behavior must not be mistaken for a match with the requested "german",
        // which would otherwise silently no-op and leave the wrong language in place.
        var result = await indexManager.CreateOneOrUpdateAsync(new CreateIndexModel<TestDocument>(
                                                                   Builders<TestDocument>.IndexKeys.Text(x => x.Name),
                                                                   new CreateIndexOptions
                                                                   {
                                                                       Name = indexName,
                                                                       DefaultLanguage = "german"
                                                                   }));

        // Assert
        result.Should().Be(indexName);
        var indexes = await (await indexManager.ListAsync()).ToListAsync();
        var index = indexes.FirstOrDefault(idx => idx["name"].AsString == indexName);
        index.Should().NotBeNull();
        index["default_language"].AsString.Should().Be("german");
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenSameNameTextIndexFieldMovesAcrossTextComponent_ShouldDropAndRecreateWithNewKeyOrder()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        var indexName = "idx_text_key_position_" + Guid.NewGuid();

        // {Value:1, Name:"text"} - the text component is a suffix of the compound key.
        await indexManager.CreateOneAsync(new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Combine(
                                                  Builders<TestDocument>.IndexKeys.Ascending(x => x.Value),
                                                  Builders<TestDocument>.IndexKeys.Text(x => x.Name)),
                                              new CreateIndexOptions
                                              {
                                                  Name = indexName
                                              }));

        // Act - Request the same name with the non-text field moved to a suffix instead of a prefix
        // ({Name:"text", Value:1}); the server reports this as a genuine IndexKeySpecsConflict, so the existing
        // index's stored key order must not be mistaken for a match with the requested one, which would
        // otherwise silently no-op and leave the wrong key layout in place.
        var result = await indexManager.CreateOneOrUpdateAsync(new CreateIndexModel<TestDocument>(
                                                                   Builders<TestDocument>.IndexKeys.Combine(
                                                                       Builders<TestDocument>.IndexKeys.Text(x => x.Name),
                                                                       Builders<TestDocument>.IndexKeys.Ascending(x => x.Value)),
                                                                   new CreateIndexOptions
                                                                   {
                                                                       Name = indexName
                                                                   }));

        // Assert
        result.Should().Be(indexName);
        var indexes = await (await indexManager.ListAsync()).ToListAsync();
        var index = indexes.FirstOrDefault(idx => idx["name"].AsString == indexName);
        index.Should().NotBeNull();
        var keyNames = index["key"].AsBsonDocument.Names.ToList();
        keyNames.Should().Equal("_fts", "_ftsx", "Value");
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenSameNameTextIndexGainsFieldThroughWeights_ShouldDropAndRecreateWithNewField()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        var indexName = "idx_text_weights_field_" + Guid.NewGuid();

        await indexManager.CreateOneAsync(new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Text(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = indexName,
                                                  Weights = new BsonDocument("Name", 1)
                                              }));

        // Act - Request the same name/keys, but add a weight for a field that is not part of the caller-facing
        // key specification at all ("ExtraField" is only declared through Weights). MongoDB accepts and actually
        // indexes this field; the existing index's stored weights (only "Name") must not be mistaken for a match
        // with the requested weights (both "Name" and "ExtraField"), which would otherwise silently no-op and
        // leave "ExtraField" unindexed.
        var result = await indexManager.CreateOneOrUpdateAsync(new CreateIndexModel<TestDocument>(
                                                                   Builders<TestDocument>.IndexKeys.Text(x => x.Name),
                                                                   new CreateIndexOptions
                                                                   {
                                                                       Name = indexName,
                                                                       Weights = new BsonDocument
                                                                       {
                                                                           { "Name", 1 },
                                                                           { "ExtraField", 5 }
                                                                       }
                                                                   }));

        // Assert
        result.Should().Be(indexName);
        var indexes = await (await indexManager.ListAsync()).ToListAsync();
        var index = indexes.FirstOrDefault(idx => idx["name"].AsString == indexName);
        index.Should().NotBeNull();
        index["weights"]["Name"].AsInt32.Should().Be(1);
        index["weights"]["ExtraField"].AsInt32.Should().Be(5);
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenSameNameTextIndexWeightsChange_ShouldDropAndRecreateWithNewWeights()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        var indexName = "idx_text_weights_" + Guid.NewGuid();

        await indexManager.CreateOneAsync(new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Text(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = indexName,
                                                  Weights = new BsonDocument("Name", 2)
                                              }));

        // Act - Request the same name/keys with a different weight; the existing index's stored weight (2) must
        // not be mistaken for a match with the requested weight (5), which would otherwise silently no-op.
        var result = await indexManager.CreateOneOrUpdateAsync(new CreateIndexModel<TestDocument>(
                                                                   Builders<TestDocument>.IndexKeys.Text(x => x.Name),
                                                                   new CreateIndexOptions
                                                                   {
                                                                       Name = indexName,
                                                                       Weights = new BsonDocument("Name", 5)
                                                                   }));

        // Assert
        result.Should().Be(indexName);
        var indexes = await (await indexManager.ListAsync()).ToListAsync();
        var index = indexes.FirstOrDefault(idx => idx["name"].AsString == indexName);
        index.Should().NotBeNull();
        index["weights"]["Name"].AsInt32.Should().Be(5);
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenSameNameWildcardIndexProjectionChanges_ShouldDropAndRecreateWithNewProjection()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        var indexName = "idx_wildcard_projection_" + Guid.NewGuid();
        var wildcardKeys = (IndexKeysDefinition<TestDocument>)new BsonDocument("$**", 1);

        await indexManager.CreateOneAsync(new CreateIndexModel<TestDocument>(
                                              wildcardKeys,
                                              new CreateIndexOptions<TestDocument>
                                              {
                                                  Name = indexName,
                                                  WildcardProjection = new BsonDocument("Name", 1)
                                              }));

        // Act - Request the same name/keys with a different WildcardProjection; the existing index's stored
        // projection must not be mistaken for a match with the requested one, which would otherwise silently
        // no-op and leave the wrong fields indexed.
        var result = await indexManager.CreateOneOrUpdateAsync(new CreateIndexModel<TestDocument>(
                                                                   wildcardKeys,
                                                                   new CreateIndexOptions<TestDocument>
                                                                   {
                                                                       Name = indexName,
                                                                       WildcardProjection = new BsonDocument("Value", 1)
                                                                   }));

        // Assert
        result.Should().Be(indexName);
        var indexes = await (await indexManager.ListAsync()).ToListAsync();
        var index = indexes.FirstOrDefault(idx => idx["name"].AsString == indexName);
        index.Should().NotBeNull();
        index["wildcardProjection"].AsBsonDocument.Should().BeEquivalentTo(new BsonDocument("Value", 1));
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenTextIndexIsRenamedWithSameKeySpecs_ShouldDropOldNameAndCreateNewName()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        var oldName = "idx_text_rename_old_" + Guid.NewGuid();
        var newName = "idx_text_rename_new_" + Guid.NewGuid();

        await indexManager.CreateOneAsync(new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Text(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = oldName
                                              }));

        // Act - Rename the text index while keeping the same key specification. The server stores a text
        // index's key as {_fts: "text", _ftsx: 1} with the actual field moved into "weights", so this exercises
        // the same name-independent lookup as a regular key specification.
        var result = await indexManager.CreateOneOrUpdateAsync(new CreateIndexModel<TestDocument>(
                                                                   Builders<TestDocument>.IndexKeys.Text(x => x.Name),
                                                                   new CreateIndexOptions
                                                                   {
                                                                       Name = newName
                                                                   }));

        // Assert
        result.Should().Be(newName);
        var indexes = await (await indexManager.ListAsync()).ToListAsync();
        indexes.Should().NotContain(idx => idx["name"].AsString == oldName);
        var index = indexes.FirstOrDefault(idx => idx["name"].AsString == newName);
        index.Should().NotBeNull();
        index["weights"]["Name"].Should().NotBeNull();
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenTextIndexWithFieldDeclaredThroughWeightsIsRenamed_ShouldDropOldNameAndCreateNewNameWithBothFields()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        var oldName = "idx_text_weights_rename_old_" + Guid.NewGuid();
        var newName = "idx_text_weights_rename_new_" + Guid.NewGuid();
        var weights = new BsonDocument
        {
            { "Name", 1 },
            { "ExtraField", 5 }
        };

        await indexManager.CreateOneAsync(new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Text(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = oldName,
                                                  Weights = weights
                                              }));

        // Act - Rename while keeping the same key specification and the same Weights, including the field
        // ("ExtraField") that is declared only through Weights. Before deriving the desired text field set from
        // both the key specification and Weights, the desired side would only see "Name" as a text field (the
        // existing side, read from listIndexes' "weights" document, already reports both), so no candidate would
        // be identified and the rename would fail with the original conflict instead of completing.
        var result = await indexManager.CreateOneOrUpdateAsync(new CreateIndexModel<TestDocument>(
                                                                   Builders<TestDocument>.IndexKeys.Text(x => x.Name),
                                                                   new CreateIndexOptions
                                                                   {
                                                                       Name = newName,
                                                                       Weights = weights
                                                                   }));

        // Assert
        result.Should().Be(newName);
        var indexes = await (await indexManager.ListAsync()).ToListAsync();
        indexes.Should().NotContain(idx => idx["name"].AsString == oldName);
        var index = indexes.FirstOrDefault(idx => idx["name"].AsString == newName);
        index.Should().NotBeNull();
        index["weights"]["Name"].AsInt32.Should().Be(1);
        index["weights"]["ExtraField"].AsInt32.Should().Be(5);
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenUnrelatedIndexSharesKeySpecs_ShouldLeaveUnrelatedIndexUntouched()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        var uniqueIndexName = "idx_unique_" + Guid.NewGuid();
        var oldName = "idx_nonunique_old_" + Guid.NewGuid();
        var newName = "idx_nonunique_new_" + Guid.NewGuid();

        await indexManager.CreateOneAsync(new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = uniqueIndexName,
                                                  Unique = true
                                              }));
        await indexManager.CreateOneAsync(new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = oldName
                                              }));

        // Act - Rename the non-unique index; the unrelated unique index on the same keys must survive
        var result = await indexManager.CreateOneOrUpdateAsync(new CreateIndexModel<TestDocument>(
                                                                   Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                                                   new CreateIndexOptions
                                                                   {
                                                                       Name = newName
                                                                   }));

        // Assert
        result.Should().Be(newName);
        var indexes = await (await indexManager.ListAsync()).ToListAsync();
        indexes.Should().Contain(idx => idx["name"].AsString == uniqueIndexName);
        indexes.Should().NotContain(idx => idx["name"].AsString == oldName);
        indexes.Should().Contain(idx => idx["name"].AsString == newName);
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WithCancellationToken_ShouldRespectCancellation()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        var indexModel = new CreateIndexModel<TestDocument>(
            Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
            new CreateIndexOptions
            {
                Name = "idx_cancel_" + Guid.NewGuid()
            });

        var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Act & Assert
        var act = async () => await indexManager.CreateOneOrUpdateAsync(indexModel, cancellationToken: cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WithSession_AndCancellationToken_ShouldRespectCancellation()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        using var session = await mongoHelper.Client.StartSessionAsync();

        var indexModel = new CreateIndexModel<TestDocument>(
            Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
            new CreateIndexOptions
            {
                Name = "idx_session_cancel_" + Guid.NewGuid()
            });

        var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Act & Assert
        // The assertion is awaited before session is disposed.
        // ReSharper disable once AccessToDisposedClosure
        var act = async () => await indexManager.CreateOneOrUpdateAsync(session, indexModel, cancellationToken: cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WithSession_WhenCalledConcurrentlyForSameRename_ShouldAllSucceedWithSingleResultingIndex()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        using var session = await mongoHelper.Client.StartSessionAsync();

        var oldName = "idx_session_concurrent_old_" + Guid.NewGuid();
        var newName = "idx_session_concurrent_new_" + Guid.NewGuid();

        await indexManager.CreateOneAsync(session, new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = oldName,
                                                  Unique = true
                                              }));

        var renamedIndexModel = new CreateIndexModel<TestDocument>(
            Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
            new CreateIndexOptions
            {
                Name = newName,
                Unique = true
            });

        // Act - Issue the same rename concurrently, each caller on its own session (a ClientSession supports only
        // sequential use), simulating two application instances starting at once
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            using var callerSession = await mongoHelper.Client.StartSessionAsync();
            return await indexManager.CreateOneOrUpdateAsync(callerSession, renamedIndexModel);
        }));

        // Assert
        results.Should().AllSatisfy(name => name.Should().Be(newName));
        var indexes = await (await indexManager.ListAsync(session, CancellationToken.None)).ToListAsync();
        indexes.Count(idx => idx["key"].AsBsonDocument.Contains("Name")).Should().Be(1);
        var index = indexes.First(idx => idx["name"].AsString == newName);
        (index.Contains("unique") && index["unique"].AsBoolean).Should().BeTrue();
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WithSession_WhenCollationResolutionRequiresProbeAndCollectionIsAtIndexLimit_ShouldThrowOriginalConflictAndLeaveCollectionUnchanged()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        using var session = await mongoHelper.Client.StartSessionAsync();

        var oldName = "idx_session_capacity_old_" + Guid.NewGuid();
        var newName = "idx_session_capacity_new_" + Guid.NewGuid();

        // An explicit non-simple collation always needs the probe (see RequiresCollationProbe), so resolving
        // this rename's conflict will try to create the throwaway probe index.
        await indexManager.CreateOneAsync(session, new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = oldName,
                                                  Collation = new Collation("en")
                                              }));

        // Fill the collection to MongoDB's 64-index limit: _id_, the index above, and 62 unrelated filler
        // indexes on synthetic field names that cannot collide with any real index.
        var fillerModels = Enumerable.Range(0, 62)
                                     .Select(i => new CreateIndexModel<TestDocument>(
                                                 new BsonDocumentIndexKeysDefinition<TestDocument>(new BsonDocument($"filler_{i}", 1))))
                                     .ToList();
        await indexManager.CreateManyAsync(session, fillerModels);

        var indexesBefore = await (await indexManager.ListAsync(session, CancellationToken.None)).ToListAsync();
        indexesBefore.Should().HaveCount(64);

        // Act - Rename the index, requesting the same collation again. Resolving it needs a spare index slot
        // for the throwaway probe, which the collection no longer has.
        // The assertion is awaited before session is disposed.
        // ReSharper disable once AccessToDisposedClosure
        var act = () => indexManager.CreateOneOrUpdateAsync(session, new CreateIndexModel<TestDocument>(
                                                                Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                                                new CreateIndexOptions
                                                                {
                                                                    Name = newName,
                                                                    Collation = new Collation("en")
                                                                }));

        // Assert - The original index conflict surfaces instead of the probe's own CannotCreateIndex failure,
        // and nothing was dropped or created.
        var assertion = await act.Should().ThrowAsync<MongoCommandException>();
        assertion.Which.CodeName.Should().BeOneOf("IndexOptionsConflict", "IndexKeySpecsConflict");
        var indexesAfter = await (await indexManager.ListAsync(session, CancellationToken.None)).ToListAsync();
        indexesAfter.Should().HaveCount(64);
        indexesAfter.Should().Contain(idx => idx["name"].AsString == oldName);
        indexesAfter.Should().NotContain(idx => idx["name"].AsString == newName);
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WithSession_WhenCollectionHasNonSimpleDefaultCollationAndSiblingHasExplicitSimpleCollation_ShouldPreserveSiblingAndRenameOldIndex()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        await mongoHelper.Database.CreateCollectionAsync(collectionName, new CreateCollectionOptions
        {
            Collation = new Collation("en")
        });
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        using var session = await mongoHelper.Client.StartSessionAsync();

        var unrelatedSimpleName = "idx_session_unrelated_simple_" + Guid.NewGuid();
        var oldName = "idx_session_inherited_collation_old_" + Guid.NewGuid();
        var newName = "idx_session_inherited_collation_new_" + Guid.NewGuid();

        await indexManager.CreateOneAsync(session, new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = unrelatedSimpleName,
                                                  Collation = Collation.Simple
                                              }));
        await indexManager.CreateOneAsync(session, new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = oldName
                                              }));

        // Act - Rename the inherited-collation index, keeping the collation omitted; the unrelated
        // explicitly-simple sibling on the same keys must survive, and the rename must still succeed.
        var result = await indexManager.CreateOneOrUpdateAsync(session, new CreateIndexModel<TestDocument>(
                                                                   Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                                                   new CreateIndexOptions
                                                                   {
                                                                       Name = newName
                                                                   }));

        // Assert
        result.Should().Be(newName);
        var indexes = await (await indexManager.ListAsync(session, CancellationToken.None)).ToListAsync();
        indexes.Should().Contain(idx => idx["name"].AsString == unrelatedSimpleName);
        indexes.Should().NotContain(idx => idx["name"].AsString == oldName);
        indexes.Should().Contain(idx => idx["name"].AsString == newName);
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WithSession_WhenDefaultNamedIndexOptionsChange_ShouldDropAndRecreateUnderSameDefaultName()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        using var session = await mongoHelper.Client.StartSessionAsync();

        await indexManager.CreateOneAsync(session, new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                              new CreateIndexOptions()));

        // Act - Create again with the same keys and no explicit name, but different options
        var result = await indexManager.CreateOneOrUpdateAsync(session, new CreateIndexModel<TestDocument>(
                                                                   Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                                                   new CreateIndexOptions
                                                                   {
                                                                       Unique = true
                                                                   }));

        // Assert
        result.Should().NotBeNullOrEmpty();
        var indexes = await (await indexManager.ListAsync(session, CancellationToken.None)).ToListAsync();
        indexes.Count(idx => idx["key"].AsBsonDocument.Contains("Name")).Should().Be(1);
        var index = indexes.FirstOrDefault(idx => idx["name"].AsString == result);
        index.Should().NotBeNull();
        (index.Contains("unique") && index["unique"].AsBoolean).Should().BeTrue();
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WithSession_WhenIndexDoesNotExist_ShouldCreateIndex()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        using var session = await mongoHelper.Client.StartSessionAsync();

        var indexModel = new CreateIndexModel<TestDocument>(
            Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
            new CreateIndexOptions
            {
                Name = "idx_session_new_" + Guid.NewGuid(),
                Unique = true
            });

        // Act
        var result = await indexManager.CreateOneOrUpdateAsync(session, indexModel);

        // Assert
        result.Should().NotBeNullOrEmpty();
        var indexes = await (await indexManager.ListAsync(session, CancellationToken.None)).ToListAsync();
        indexes.Should().Contain(idx => idx["name"].AsString == result);
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WithSession_WhenIndexExistsWithDifferentKeySpecs_ShouldDropAndRecreate()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        using var session = await mongoHelper.Client.StartSessionAsync();

        var indexName = "idx_session_keyspec_" + Guid.NewGuid();

        // Create index on Name field
        var firstIndexModel = new CreateIndexModel<TestDocument>(
            Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
            new CreateIndexOptions
            {
                Name = indexName
            });

        await indexManager.CreateOneAsync(session, firstIndexModel);

        // Act - Create index on Value field with same name (conflicting key spec)
        var secondIndexModel = new CreateIndexModel<TestDocument>(
            Builders<TestDocument>.IndexKeys.Ascending(x => x.Value),
            new CreateIndexOptions
            {
                Name = indexName
            });

        var result = await indexManager.CreateOneOrUpdateAsync(session, secondIndexModel);

        // Assert
        result.Should().Be(indexName);
        var indexes = await (await indexManager.ListAsync(session, CancellationToken.None)).ToListAsync();
        var index = indexes.FirstOrDefault(idx => idx["name"].AsString == indexName);
        index.Should().NotBeNull();
        index["key"]["Value"].Should().NotBeNull();
        index["key"].AsBsonDocument.Contains("Name").Should().BeFalse();
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WithSession_WhenIndexExistsWithDifferentOptions_ShouldDropAndRecreate()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        using var session = await mongoHelper.Client.StartSessionAsync();

        var indexName = "idx_session_conflict_" + Guid.NewGuid();

        // Create index with unique = true
        var firstIndexModel = new CreateIndexModel<TestDocument>(
            Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
            new CreateIndexOptions
            {
                Name = indexName,
                Unique = true
            });

        await indexManager.CreateOneAsync(session, firstIndexModel);

        // Act - Create index with unique = false (conflicting option)
        var secondIndexModel = new CreateIndexModel<TestDocument>(
            Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
            new CreateIndexOptions
            {
                Name = indexName,
                Unique = false
            });

        var result = await indexManager.CreateOneOrUpdateAsync(session, secondIndexModel);

        // Assert
        result.Should().Be(indexName);
        var indexes = await (await indexManager.ListAsync(session, CancellationToken.None)).ToListAsync();
        var index = indexes.FirstOrDefault(idx => idx["name"].AsString == indexName);
        index.Should().NotBeNull();
        (index.Contains("unique") && index["unique"].AsBoolean).Should().BeFalse();
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WithSession_WhenIndexIsRenamedWithDifferentExpireAfter_ShouldDropOldAndCreateNewWithNewExpireAfter()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        using var session = await mongoHelper.Client.StartSessionAsync();

        var oldName = "idx_session_ttl_old_" + Guid.NewGuid();
        var newName = "idx_session_ttl_new_" + Guid.NewGuid();

        await indexManager.CreateOneAsync(session, new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = oldName,
                                                  ExpireAfter = TimeSpan.FromSeconds(60)
                                              }));

        // Act - Rename the index, keeping the same key specification and identity options but a different TTL
        var result = await indexManager.CreateOneOrUpdateAsync(session, new CreateIndexModel<TestDocument>(
                                                                   Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                                                   new CreateIndexOptions
                                                                   {
                                                                       Name = newName,
                                                                       ExpireAfter = TimeSpan.FromSeconds(120)
                                                                   }));

        // Assert
        result.Should().Be(newName);
        var indexes = await (await indexManager.ListAsync(session, CancellationToken.None)).ToListAsync();
        indexes.Should().NotContain(idx => idx["name"].AsString == oldName);
        var index = indexes.FirstOrDefault(idx => idx["name"].AsString == newName);
        index.Should().NotBeNull();
        index["expireAfterSeconds"].ToDouble().Should().Be(120);
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WithSession_WhenIndexIsRenamedWithSameKeySpecs_ShouldDropOldNameAndCreateNewName()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        using var session = await mongoHelper.Client.StartSessionAsync();

        var oldName = "idx_session_rename_old_" + Guid.NewGuid();
        var newName = "idx_session_rename_new_" + Guid.NewGuid();

        await indexManager.CreateOneAsync(session, new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = oldName
                                              }));

        // Act - Rename the index while keeping the same key specification
        var result = await indexManager.CreateOneOrUpdateAsync(session, new CreateIndexModel<TestDocument>(
                                                                   Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                                                   new CreateIndexOptions
                                                                   {
                                                                       Name = newName
                                                                   }));

        // Assert
        result.Should().Be(newName);
        var indexes = await (await indexManager.ListAsync(session, CancellationToken.None)).ToListAsync();
        indexes.Should().NotContain(idx => idx["name"].AsString == oldName);
        indexes.Should().Contain(idx => idx["name"].AsString == newName);
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WithSession_WhenRenamedIndexDiffersOnlyBySparse_ShouldLeaveDifferentlySparseSiblingUntouched()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        using var session = await mongoHelper.Client.StartSessionAsync();

        var sparseSiblingName = "idx_session_sparse_sibling_" + Guid.NewGuid();
        var oldName = "idx_session_nonsparse_old_" + Guid.NewGuid();
        var newName = "idx_session_nonsparse_new_" + Guid.NewGuid();

        await indexManager.CreateOneAsync(session, new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = sparseSiblingName,
                                                  Sparse = true
                                              }));
        await indexManager.CreateOneAsync(session, new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = oldName,
                                                  Sparse = false
                                              }));

        // Act - Rename the non-sparse index; the differently-sparse sibling on the same keys must survive
        var result = await indexManager.CreateOneOrUpdateAsync(session, new CreateIndexModel<TestDocument>(
                                                                   Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                                                   new CreateIndexOptions
                                                                   {
                                                                       Name = newName,
                                                                       Sparse = false
                                                                   }));

        // Assert
        result.Should().Be(newName);
        var indexes = await (await indexManager.ListAsync(session, CancellationToken.None)).ToListAsync();
        indexes.Should().Contain(idx => idx["name"].AsString == sparseSiblingName);
        indexes.Should().NotContain(idx => idx["name"].AsString == oldName);
        indexes.Should().Contain(idx => idx["name"].AsString == newName);
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WithSession_WhenRenamedToMatchCollationWithSameLocaleDifferentStrength_ShouldLeaveDifferentlyStrengthSiblingUntouched()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        using var session = await mongoHelper.Client.StartSessionAsync();

        var strengthSiblingName = "idx_session_strength_sibling_" + Guid.NewGuid();
        var oldName = "idx_session_strength_old_" + Guid.NewGuid();
        var newName = "idx_session_strength_new_" + Guid.NewGuid();

        await indexManager.CreateOneAsync(session, new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = strengthSiblingName,
                                                  Collation = new Collation("en", strength: CollationStrength.Primary)
                                              }));
        await indexManager.CreateOneAsync(session, new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = oldName,
                                                  Collation = new Collation("en")
                                              }));

        // Act - Rename the default-strength index; the explicitly Primary-strength sibling must survive.
        var result = await indexManager.CreateOneOrUpdateAsync(session, new CreateIndexModel<TestDocument>(
                                                                   Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                                                   new CreateIndexOptions
                                                                   {
                                                                       Name = newName,
                                                                       Collation = new Collation("en")
                                                                   }));

        // Assert
        result.Should().Be(newName);
        var indexes = await (await indexManager.ListAsync(session, CancellationToken.None)).ToListAsync();
        indexes.Should().Contain(idx => idx["name"].AsString == strengthSiblingName);
        indexes.Should().NotContain(idx => idx["name"].AsString == oldName);
        indexes.Should().Contain(idx => idx["name"].AsString == newName);
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WithSession_WhenRenamedToMatchCollation_ShouldLeaveDifferentlyCollatedSiblingUntouched()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        using var session = await mongoHelper.Client.StartSessionAsync();

        var otherCollationName = "idx_session_collation_en_" + Guid.NewGuid();
        var oldName = "idx_session_collation_de_old_" + Guid.NewGuid();
        var newName = "idx_session_collation_de_new_" + Guid.NewGuid();

        await indexManager.CreateOneAsync(session, new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = otherCollationName,
                                                  Collation = new Collation("en")
                                              }));
        await indexManager.CreateOneAsync(session, new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = oldName,
                                                  Collation = new Collation("de")
                                              }));

        // Act - Rename the "de"-collated index; the "en"-collated sibling on the same keys must survive
        var result = await indexManager.CreateOneOrUpdateAsync(session, new CreateIndexModel<TestDocument>(
                                                                   Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                                                   new CreateIndexOptions
                                                                   {
                                                                       Name = newName,
                                                                       Collation = new Collation("de")
                                                                   }));

        // Assert
        result.Should().Be(newName);
        var indexes = await (await indexManager.ListAsync(session, CancellationToken.None)).ToListAsync();
        indexes.Should().Contain(idx => idx["name"].AsString == otherCollationName);
        indexes.Should().NotContain(idx => idx["name"].AsString == oldName);
        indexes.Should().Contain(idx => idx["name"].AsString == newName);
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WithSession_WhenRenamedWithExplicitSimpleCollation_ShouldDropOldNameAndCreateNewName()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        using var session = await mongoHelper.Client.StartSessionAsync();

        var oldName = "idx_session_simple_collation_old_" + Guid.NewGuid();
        var newName = "idx_session_simple_collation_new_" + Guid.NewGuid();

        await indexManager.CreateOneAsync(session, new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = oldName,
                                                  Collation = Collation.Simple
                                              }));

        // Act - Rename the index, keeping the same explicit simple collation (which the server omits from
        // listIndexes, the same way an entirely unspecified collation is omitted)
        var result = await indexManager.CreateOneOrUpdateAsync(session, new CreateIndexModel<TestDocument>(
                                                                   Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                                                   new CreateIndexOptions
                                                                   {
                                                                       Name = newName,
                                                                       Collation = Collation.Simple
                                                                   }));

        // Assert
        result.Should().Be(newName);
        var indexes = await (await indexManager.ListAsync(session, CancellationToken.None)).ToListAsync();
        indexes.Should().NotContain(idx => idx["name"].AsString == oldName);
        indexes.Should().Contain(idx => idx["name"].AsString == newName);
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WithSession_WhenRenamedWithLocaleCollationHavingNonDefaultParameters_ShouldDropOldNameAndCreateNewName()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        using var session = await mongoHelper.Client.StartSessionAsync();

        var oldName = "idx_session_locale_collation_old_" + Guid.NewGuid();
        var newName = "idx_session_locale_collation_new_" + Guid.NewGuid();

        // "fr_CA" only specifies a locale; the server fills every other collation parameter from its own
        // locale-specific defaults (notably "backwards", which defaults to true for this locale, unlike most
        // others), which this library cannot reproduce locally.
        await indexManager.CreateOneAsync(session, new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = oldName,
                                                  Collation = new Collation("fr_CA")
                                              }));

        // Act - Rename the index, requesting the same partially specified collation again
        var result = await indexManager.CreateOneOrUpdateAsync(session, new CreateIndexModel<TestDocument>(
                                                                   Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                                                   new CreateIndexOptions
                                                                   {
                                                                       Name = newName,
                                                                       Collation = new Collation("fr_CA")
                                                                   }));

        // Assert
        result.Should().Be(newName);
        var indexes = await (await indexManager.ListAsync(session, CancellationToken.None)).ToListAsync();
        indexes.Should().NotContain(idx => idx["name"].AsString == oldName);
        var index = indexes.FirstOrDefault(idx => idx["name"].AsString == newName);
        index.Should().NotBeNull();
        index["collation"]["locale"].AsString.Should().Be("fr_CA");
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WithSession_WhenSameName2dIndexBitsAndMinChange_ShouldDropAndRecreateWithNewValues()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        using var session = await mongoHelper.Client.StartSessionAsync();

        var indexName = "idx_session_2d_bits_min_" + Guid.NewGuid();
        var geoKeys = (IndexKeysDefinition<TestDocument>)new BsonDocument("Location", "2d");

        await indexManager.CreateOneAsync(session, new CreateIndexModel<TestDocument>(
                                              geoKeys,
                                              new CreateIndexOptions
                                              {
                                                  Name = indexName,
                                                  Bits = 26,
                                                  Min = -180,
                                                  Max = 180
                                              }));

        // Act - Request the same name/keys with different Bits/Min.
        var result = await indexManager.CreateOneOrUpdateAsync(session, new CreateIndexModel<TestDocument>(
                                                                   geoKeys,
                                                                   new CreateIndexOptions
                                                                   {
                                                                       Name = indexName,
                                                                       Bits = 20,
                                                                       Min = -360,
                                                                       Max = 360
                                                                   }));

        // Assert
        result.Should().Be(indexName);
        var indexes = await (await indexManager.ListAsync(session, CancellationToken.None)).ToListAsync();
        var index = indexes.FirstOrDefault(idx => idx["name"].AsString == indexName);
        index.Should().NotBeNull();
        index["bits"].ToInt32().Should().Be(20);
        index["min"].ToDouble().Should().Be(-360);
        index["max"].ToDouble().Should().Be(360);
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WithSession_WhenSameNameCollationOmitsPreviouslySetStrength_ShouldDropAndRecreateWithResolvedDefaultStrength()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        using var session = await mongoHelper.Client.StartSessionAsync();

        var indexName = "idx_session_strength_reset_" + Guid.NewGuid();

        await indexManager.CreateOneAsync(session, new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = indexName,
                                                  Collation = new Collation("en", strength: CollationStrength.Primary)
                                              }));

        // Act - Request the same name/keys, omitting the explicit strength.
        var result = await indexManager.CreateOneOrUpdateAsync(session, new CreateIndexModel<TestDocument>(
                                                                   Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                                                   new CreateIndexOptions
                                                                   {
                                                                       Name = indexName,
                                                                       Collation = new Collation("en")
                                                                   }));

        // Assert
        result.Should().Be(indexName);
        var indexes = await (await indexManager.ListAsync(session, CancellationToken.None)).ToListAsync();
        var index = indexes.FirstOrDefault(idx => idx["name"].AsString == indexName);
        index.Should().NotBeNull();
        index["collation"]["strength"].AsInt32.Should().Be(3);
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WithSession_WhenSameNameIndexDiffersOnlyBySparse_ShouldDropAndRecreate()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        using var session = await mongoHelper.Client.StartSessionAsync();

        var indexName = "idx_session_sparse_" + Guid.NewGuid();

        await indexManager.CreateOneAsync(session, new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = indexName,
                                                  Sparse = true
                                              }));

        // Act - Create the same name with Sparse flipped (conflicting option)
        var result = await indexManager.CreateOneOrUpdateAsync(session, new CreateIndexModel<TestDocument>(
                                                                   Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                                                   new CreateIndexOptions
                                                                   {
                                                                       Name = indexName,
                                                                       Sparse = false
                                                                   }));

        // Assert
        result.Should().Be(indexName);
        var indexes = await (await indexManager.ListAsync(session, CancellationToken.None)).ToListAsync();
        var index = indexes.FirstOrDefault(idx => idx["name"].AsString == indexName);
        index.Should().NotBeNull();
        (index.Contains("sparse") && index["sparse"].AsBoolean).Should().BeFalse();
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WithSession_WhenSameNameIndexHiddenChanges_ShouldDropAndRecreateWithHiddenUpdated()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        using var session = await mongoHelper.Client.StartSessionAsync();

        var indexName = "idx_session_hidden_" + Guid.NewGuid();

        await indexManager.CreateOneAsync(session, new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = indexName,
                                                  Hidden = false
                                              }));

        // Act - Request the same name/keys with Hidden flipped to true.
        var result = await indexManager.CreateOneOrUpdateAsync(session, new CreateIndexModel<TestDocument>(
                                                                   Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                                                   new CreateIndexOptions
                                                                   {
                                                                       Name = indexName,
                                                                       Hidden = true
                                                                   }));

        // Assert
        result.Should().Be(indexName);
        var indexes = await (await indexManager.ListAsync(session, CancellationToken.None)).ToListAsync();
        var index = indexes.FirstOrDefault(idx => idx["name"].AsString == indexName);
        index.Should().NotBeNull();
        (index.Contains("hidden") && index["hidden"].AsBoolean).Should().BeTrue();
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WithSession_WhenSameNameIndexStorageEngineChanges_ShouldDropAndRecreateWithNewStorageEngine()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        using var session = await mongoHelper.Client.StartSessionAsync();

        var indexName = "idx_session_storage_engine_" + Guid.NewGuid();

        await indexManager.CreateOneAsync(session, new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = indexName,
                                                  StorageEngine = new BsonDocument("wiredTiger", new BsonDocument("configString", "block_compressor=snappy"))
                                              }));

        // Act - Request the same name/keys with a different storage engine configuration.
        var result = await indexManager.CreateOneOrUpdateAsync(session, new CreateIndexModel<TestDocument>(
                                                                   Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                                                   new CreateIndexOptions
                                                                   {
                                                                       Name = indexName,
                                                                       StorageEngine = new BsonDocument("wiredTiger", new BsonDocument("configString", "block_compressor=zstd"))
                                                                   }));

        // Assert
        result.Should().Be(indexName);
        var indexes = await (await indexManager.ListAsync(session, CancellationToken.None)).ToListAsync();
        var index = indexes.FirstOrDefault(idx => idx["name"].AsString == indexName);
        index.Should().NotBeNull();
        index["storageEngine"]["wiredTiger"]["configString"].AsString.Should().Be("block_compressor=zstd");
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WithSession_WhenSameNameTextIndexDefaultLanguageChanges_ShouldDropAndRecreateWithNewLanguage()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        using var session = await mongoHelper.Client.StartSessionAsync();

        var indexName = "idx_session_text_language_" + Guid.NewGuid();

        await indexManager.CreateOneAsync(session, new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Text(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = indexName,
                                                  DefaultLanguage = "english"
                                              }));

        // Act - Request the same name/keys with a different DefaultLanguage.
        var result = await indexManager.CreateOneOrUpdateAsync(session, new CreateIndexModel<TestDocument>(
                                                                   Builders<TestDocument>.IndexKeys.Text(x => x.Name),
                                                                   new CreateIndexOptions
                                                                   {
                                                                       Name = indexName,
                                                                       DefaultLanguage = "german"
                                                                   }));

        // Assert
        result.Should().Be(indexName);
        var indexes = await (await indexManager.ListAsync(session, CancellationToken.None)).ToListAsync();
        var index = indexes.FirstOrDefault(idx => idx["name"].AsString == indexName);
        index.Should().NotBeNull();
        index["default_language"].AsString.Should().Be("german");
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WithSession_WhenSameNameTextIndexFieldMovesAcrossTextComponent_ShouldDropAndRecreateWithNewKeyOrder()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        using var session = await mongoHelper.Client.StartSessionAsync();

        var indexName = "idx_session_text_key_position_" + Guid.NewGuid();

        // {Value:1, Name:"text"} - the text component is a suffix of the compound key.
        await indexManager.CreateOneAsync(session, new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Combine(
                                                  Builders<TestDocument>.IndexKeys.Ascending(x => x.Value),
                                                  Builders<TestDocument>.IndexKeys.Text(x => x.Name)),
                                              new CreateIndexOptions
                                              {
                                                  Name = indexName
                                              }));

        // Act - Request the same name with the non-text field moved to a suffix instead of a prefix
        // ({Name:"text", Value:1}).
        var result = await indexManager.CreateOneOrUpdateAsync(session, new CreateIndexModel<TestDocument>(
                                                                   Builders<TestDocument>.IndexKeys.Combine(
                                                                       Builders<TestDocument>.IndexKeys.Text(x => x.Name),
                                                                       Builders<TestDocument>.IndexKeys.Ascending(x => x.Value)),
                                                                   new CreateIndexOptions
                                                                   {
                                                                       Name = indexName
                                                                   }));

        // Assert
        result.Should().Be(indexName);
        var indexes = await (await indexManager.ListAsync(session, CancellationToken.None)).ToListAsync();
        var index = indexes.FirstOrDefault(idx => idx["name"].AsString == indexName);
        index.Should().NotBeNull();
        var keyNames = index["key"].AsBsonDocument.Names.ToList();
        keyNames.Should().Equal("_fts", "_ftsx", "Value");
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WithSession_WhenSameNameTextIndexGainsFieldThroughWeights_ShouldDropAndRecreateWithNewField()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        using var session = await mongoHelper.Client.StartSessionAsync();

        var indexName = "idx_session_text_weights_field_" + Guid.NewGuid();

        await indexManager.CreateOneAsync(session, new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Text(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = indexName,
                                                  Weights = new BsonDocument("Name", 1)
                                              }));

        // Act - Add a weight for a field that is not part of the caller-facing key specification at all.
        var result = await indexManager.CreateOneOrUpdateAsync(session, new CreateIndexModel<TestDocument>(
                                                                   Builders<TestDocument>.IndexKeys.Text(x => x.Name),
                                                                   new CreateIndexOptions
                                                                   {
                                                                       Name = indexName,
                                                                       Weights = new BsonDocument
                                                                       {
                                                                           { "Name", 1 },
                                                                           { "ExtraField", 5 }
                                                                       }
                                                                   }));

        // Assert
        result.Should().Be(indexName);
        var indexes = await (await indexManager.ListAsync(session, CancellationToken.None)).ToListAsync();
        var index = indexes.FirstOrDefault(idx => idx["name"].AsString == indexName);
        index.Should().NotBeNull();
        index["weights"]["Name"].AsInt32.Should().Be(1);
        index["weights"]["ExtraField"].AsInt32.Should().Be(5);
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WithSession_WhenSameNameTextIndexWeightsChange_ShouldDropAndRecreateWithNewWeights()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        using var session = await mongoHelper.Client.StartSessionAsync();

        var indexName = "idx_session_text_weights_" + Guid.NewGuid();

        await indexManager.CreateOneAsync(session, new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Text(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = indexName,
                                                  Weights = new BsonDocument("Name", 2)
                                              }));

        // Act - Request the same name/keys with a different weight.
        var result = await indexManager.CreateOneOrUpdateAsync(session, new CreateIndexModel<TestDocument>(
                                                                   Builders<TestDocument>.IndexKeys.Text(x => x.Name),
                                                                   new CreateIndexOptions
                                                                   {
                                                                       Name = indexName,
                                                                       Weights = new BsonDocument("Name", 5)
                                                                   }));

        // Assert
        result.Should().Be(indexName);
        var indexes = await (await indexManager.ListAsync(session, CancellationToken.None)).ToListAsync();
        var index = indexes.FirstOrDefault(idx => idx["name"].AsString == indexName);
        index.Should().NotBeNull();
        index["weights"]["Name"].AsInt32.Should().Be(5);
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WithSession_WhenSameNameWildcardIndexProjectionChanges_ShouldDropAndRecreateWithNewProjection()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        using var session = await mongoHelper.Client.StartSessionAsync();

        var indexName = "idx_session_wildcard_projection_" + Guid.NewGuid();
        var wildcardKeys = (IndexKeysDefinition<TestDocument>)new BsonDocument("$**", 1);

        await indexManager.CreateOneAsync(session, new CreateIndexModel<TestDocument>(
                                              wildcardKeys,
                                              new CreateIndexOptions<TestDocument>
                                              {
                                                  Name = indexName,
                                                  WildcardProjection = new BsonDocument("Name", 1)
                                              }));

        // Act - Request the same name/keys with a different WildcardProjection.
        var result = await indexManager.CreateOneOrUpdateAsync(session, new CreateIndexModel<TestDocument>(
                                                                   wildcardKeys,
                                                                   new CreateIndexOptions<TestDocument>
                                                                   {
                                                                       Name = indexName,
                                                                       WildcardProjection = new BsonDocument("Value", 1)
                                                                   }));

        // Assert
        result.Should().Be(indexName);
        var indexes = await (await indexManager.ListAsync(session, CancellationToken.None)).ToListAsync();
        var index = indexes.FirstOrDefault(idx => idx["name"].AsString == indexName);
        index.Should().NotBeNull();
        index["wildcardProjection"].AsBsonDocument.Should().BeEquivalentTo(new BsonDocument("Value", 1));
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WithSession_WhenTextIndexIsRenamedWithSameKeySpecs_ShouldDropOldNameAndCreateNewName()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        using var session = await mongoHelper.Client.StartSessionAsync();

        var oldName = "idx_session_text_rename_old_" + Guid.NewGuid();
        var newName = "idx_session_text_rename_new_" + Guid.NewGuid();

        await indexManager.CreateOneAsync(session, new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Text(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = oldName
                                              }));

        // Act - Rename the text index while keeping the same key specification. The server stores a text
        // index's key as {_fts: "text", _ftsx: 1} with the actual field moved into "weights", so this exercises
        // the same name-independent lookup as a regular key specification.
        var result = await indexManager.CreateOneOrUpdateAsync(session, new CreateIndexModel<TestDocument>(
                                                                   Builders<TestDocument>.IndexKeys.Text(x => x.Name),
                                                                   new CreateIndexOptions
                                                                   {
                                                                       Name = newName
                                                                   }));

        // Assert
        result.Should().Be(newName);
        var indexes = await (await indexManager.ListAsync(session, CancellationToken.None)).ToListAsync();
        indexes.Should().NotContain(idx => idx["name"].AsString == oldName);
        var index = indexes.FirstOrDefault(idx => idx["name"].AsString == newName);
        index.Should().NotBeNull();
        index["weights"]["Name"].Should().NotBeNull();
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WithSession_WhenTextIndexWithFieldDeclaredThroughWeightsIsRenamed_ShouldDropOldNameAndCreateNewNameWithBothFields()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        using var session = await mongoHelper.Client.StartSessionAsync();

        var oldName = "idx_session_text_weights_rename_old_" + Guid.NewGuid();
        var newName = "idx_session_text_weights_rename_new_" + Guid.NewGuid();
        var weights = new BsonDocument
        {
            { "Name", 1 },
            { "ExtraField", 5 }
        };

        await indexManager.CreateOneAsync(session, new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Text(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = oldName,
                                                  Weights = weights
                                              }));

        // Act - Rename while keeping the same key specification and the same Weights, including the field
        // declared only through Weights.
        var result = await indexManager.CreateOneOrUpdateAsync(session, new CreateIndexModel<TestDocument>(
                                                                   Builders<TestDocument>.IndexKeys.Text(x => x.Name),
                                                                   new CreateIndexOptions
                                                                   {
                                                                       Name = newName,
                                                                       Weights = weights
                                                                   }));

        // Assert
        result.Should().Be(newName);
        var indexes = await (await indexManager.ListAsync(session, CancellationToken.None)).ToListAsync();
        indexes.Should().NotContain(idx => idx["name"].AsString == oldName);
        var index = indexes.FirstOrDefault(idx => idx["name"].AsString == newName);
        index.Should().NotBeNull();
        index["weights"]["Name"].AsInt32.Should().Be(1);
        index["weights"]["ExtraField"].AsInt32.Should().Be(5);
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WithSession_WhenUnrelatedIndexSharesKeySpecs_ShouldLeaveUnrelatedIndexUntouched()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"IndexTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var collectionName = "test_" + Guid.NewGuid().ToString("N");
        var collection = mongoHelper.Database.GetCollection<TestDocument>(collectionName);
        var indexManager = collection.Indexes;

        using var session = await mongoHelper.Client.StartSessionAsync();

        var uniqueIndexName = "idx_session_unique_" + Guid.NewGuid();
        var oldName = "idx_session_nonunique_old_" + Guid.NewGuid();
        var newName = "idx_session_nonunique_new_" + Guid.NewGuid();

        await indexManager.CreateOneAsync(session, new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = uniqueIndexName,
                                                  Unique = true
                                              }));
        await indexManager.CreateOneAsync(session, new CreateIndexModel<TestDocument>(
                                              Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                              new CreateIndexOptions
                                              {
                                                  Name = oldName
                                              }));

        // Act - Rename the non-unique index; the unrelated unique index on the same keys must survive
        var result = await indexManager.CreateOneOrUpdateAsync(session, new CreateIndexModel<TestDocument>(
                                                                   Builders<TestDocument>.IndexKeys.Ascending(x => x.Name),
                                                                   new CreateIndexOptions
                                                                   {
                                                                       Name = newName
                                                                   }));

        // Assert
        result.Should().Be(newName);
        var indexes = await (await indexManager.ListAsync(session, CancellationToken.None)).ToListAsync();
        indexes.Should().Contain(idx => idx["name"].AsString == uniqueIndexName);
        indexes.Should().NotContain(idx => idx["name"].AsString == oldName);
        indexes.Should().Contain(idx => idx["name"].AsString == newName);
    }

    [OneTimeSetUp]
    public async Task GetMongoDbContainer() => _container = await MongoDbTestContainer.StartContainerAsync();

    private class TestDocument
    {
        // MongoDB accesses this identifier through BSON serialization.
        // ReSharper disable once UnusedMember.Local
        [BsonId]
        public ObjectId Id { get; init; }

        public String Name { get; init; } = String.Empty;

        public Int32 Value { get; init; }
    }
}
