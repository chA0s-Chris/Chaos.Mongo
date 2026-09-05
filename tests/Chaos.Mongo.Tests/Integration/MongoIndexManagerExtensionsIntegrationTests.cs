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
        var act = async () => await indexManager.CreateOneOrUpdateAsync(session, indexModel, cancellationToken: cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
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
