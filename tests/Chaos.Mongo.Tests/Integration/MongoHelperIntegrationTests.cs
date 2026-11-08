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

[Category("Integration")]
public class MongoHelperIntegrationTests
{
    private MongoDbContainer _container;

    [OneTimeSetUp]
    public async Task GetMongoDbContainer() => _container = await MongoDbTestContainer.StartContainerAsync();

    [Test]
    public async Task MappingTypToCollectionAndInserting5000Documents_GetCollectionAndFind_ShouldReturnAllDocuments()
    {
        var url = MongoUrl.Create(_container.GetConnectionString());

        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = $"BasicInsertingTestDb_{Guid.NewGuid():N}";
                              options.AddMapping<TestDocument>("TestDocuments");
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var testDocuments = Enumerable.Range(0, 5000)
                                      .Select(n => new TestDocument
                                      {
                                          Id = ObjectId.GenerateNewId(),
                                          Value = n
                                      })
                                      .ToList();

        var collection = mongoHelper.GetCollection<TestDocument>();
        await collection.InsertManyAsync(testDocuments);

        var collectionNames = await (await mongoHelper.Database.ListCollectionNamesAsync()).ToListAsync();
        collectionNames.Should().Contain("TestDocuments");
        collectionNames.Should().NotContain("TestDocument");

        var testDocumentIds = await collection.Find(FilterDefinition<TestDocument>.Empty)
                                              .SortByDescending(x => x.Value)
                                              .Project(x => x.Id)
                                              .ToListAsync();

        var expectedIds = testDocuments.OrderByDescending(x => x.Value)
                                       .Select(x => x.Id)
                                       .ToList();

        testDocumentIds.Should().BeEquivalentTo(expectedIds);
    }

    private class TestDocument
    {
        [BsonId]
        public ObjectId Id { get; init; }

        public Int32 Value { get; init; }
    }
}
