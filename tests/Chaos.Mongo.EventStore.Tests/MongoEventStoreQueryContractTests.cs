// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests;

using Chaos.Mongo.EventStore.Tests.Integration;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Moq;
using NUnit.Framework;

public class MongoEventStoreQueryContractTests
{
    [Test]
    public async Task GetAtVersionAsync_WhenCheckpointingEnabled_QueriesLatestCheckpointThenRemainingEvents()
    {
        // Arrange
        var aggregateId = Guid.NewGuid();
        var options = new MongoEventStoreOptions<OrderAggregate>
        {
            CollectionPrefix = "Orders",
            CheckpointInterval = 3
        };
        var checkpoint = new CheckpointDocument<OrderAggregate>
        {
            Id = new(aggregateId, 3),
            State = new()
            {
                Id = aggregateId,
                CreatedUtc = DateTime.UtcNow,
                CustomerName = "Charlie",
                Status = "Created",
                Version = 3
            }
        };

        var checkpointCollectionMock = new Mock<IMongoCollection<CheckpointDocument<OrderAggregate>>>();
        FilterDefinition<CheckpointDocument<OrderAggregate>>? capturedCheckpointFilter = null;
        FindOptions<CheckpointDocument<OrderAggregate>, CheckpointDocument<OrderAggregate>>? capturedCheckpointOptions = null;
        checkpointCollectionMock
            .Setup(c => c.FindAsync(
                       It.IsAny<FilterDefinition<CheckpointDocument<OrderAggregate>>>(),
                       It.IsAny<FindOptions<CheckpointDocument<OrderAggregate>, CheckpointDocument<OrderAggregate>>>(),
                       It.IsAny<CancellationToken>()))
            .Callback<FilterDefinition<CheckpointDocument<OrderAggregate>>, FindOptions<CheckpointDocument<OrderAggregate>, CheckpointDocument<OrderAggregate>>,
                CancellationToken>((filter, findOptions, _) =>
            {
                capturedCheckpointFilter = filter;
                capturedCheckpointOptions = findOptions;
            })
            .ReturnsAsync(CreateCursor(checkpoint));

        var eventsCollectionMock = new Mock<IMongoCollection<Event<OrderAggregate>>>();
        FilterDefinition<Event<OrderAggregate>>? capturedEventFilter = null;
        FindOptions<Event<OrderAggregate>, Event<OrderAggregate>>? capturedEventOptions = null;
        eventsCollectionMock
            .Setup(c => c.FindAsync(
                       It.IsAny<FilterDefinition<Event<OrderAggregate>>>(),
                       It.IsAny<FindOptions<Event<OrderAggregate>, Event<OrderAggregate>>>(),
                       It.IsAny<CancellationToken>()))
            .Callback<FilterDefinition<Event<OrderAggregate>>, FindOptions<Event<OrderAggregate>, Event<OrderAggregate>>,
                CancellationToken>((filter, findOptions, _) =>
            {
                capturedEventFilter = filter;
                capturedEventOptions = findOptions;
            })
            .ReturnsAsync(CreateCursor<Event<OrderAggregate>>());

        var sut = CreateAggregateRepository(options, checkpointCollectionMock.Object, eventsCollectionMock.Object);

        // Act
        var aggregate = await sut.GetAtVersionAsync(aggregateId, 7);

        // Assert
        aggregate.Should().NotBeNull();
        aggregate.Version.Should().Be(3);

        capturedCheckpointFilter.Should().NotBeNull();
        capturedCheckpointOptions.Should().NotBeNull();
        capturedCheckpointOptions!.Sort.Should().NotBeNull();
        var renderedCheckpointFilter = Render(capturedCheckpointFilter!);
        ContainsEquality(renderedCheckpointFilter, "_id.AggregateId", CreateGuidValue(aggregateId)).Should().BeTrue();
        ContainsComparison(renderedCheckpointFilter, "_id.Version", "$lte", new BsonInt64(7)).Should().BeTrue();
        Render(capturedCheckpointOptions.Sort!).Should().BeEquivalentTo(new BsonDocument("_id.Version", -1));

        capturedEventFilter.Should().NotBeNull();
        capturedEventOptions.Should().NotBeNull();
        capturedEventOptions!.Sort.Should().NotBeNull();
        var renderedEventFilter = Render(capturedEventFilter!);
        ContainsEquality(renderedEventFilter, nameof(Event<OrderAggregate>.AggregateId), CreateGuidValue(aggregateId)).Should().BeTrue();
        ContainsComparison(renderedEventFilter, nameof(Event<OrderAggregate>.Version), "$gte", new BsonInt64(4)).Should().BeTrue();
        ContainsComparison(renderedEventFilter, nameof(Event<OrderAggregate>.Version), "$lte", new BsonInt64(7)).Should().BeTrue();
        Render(capturedEventOptions.Sort!).Should().BeEquivalentTo(new BsonDocument(nameof(Event<OrderAggregate>.Version), 1));
    }

    [Test]
    public async Task GetEventStream_UsesAggregateAndVersionWindowWithAscendingSort()
    {
        // Arrange
        var aggregateId = Guid.NewGuid();
        var options = new MongoEventStoreOptions<OrderAggregate>
        {
            CollectionPrefix = "Orders"
        };
        var eventsCollectionMock = new Mock<IMongoCollection<Event<OrderAggregate>>>();
        FilterDefinition<Event<OrderAggregate>>? capturedFilter = null;
        FindOptions<Event<OrderAggregate>, Event<OrderAggregate>>? capturedOptions = null;
        eventsCollectionMock
            .Setup(c => c.FindAsync(
                       It.IsAny<FilterDefinition<Event<OrderAggregate>>>(),
                       It.IsAny<FindOptions<Event<OrderAggregate>, Event<OrderAggregate>>>(),
                       It.IsAny<CancellationToken>()))
            .Callback<FilterDefinition<Event<OrderAggregate>>, FindOptions<Event<OrderAggregate>, Event<OrderAggregate>>,
                CancellationToken>((filter, findOptions, _) =>
            {
                capturedFilter = filter;
                capturedOptions = findOptions;
            })
            .ReturnsAsync(CreateCursor<Event<OrderAggregate>>());

        var sut = CreateEventStore(options, eventsCollectionMock.Object);

        // Act
        await foreach (var _ in sut.GetEventStream(aggregateId, 4, 8)) { }

        // Assert
        capturedFilter.Should().NotBeNull();
        capturedOptions.Should().NotBeNull();
        capturedOptions!.Sort.Should().NotBeNull();

        var renderedFilter = Render(capturedFilter!);
        ContainsEquality(renderedFilter, nameof(Event<OrderAggregate>.AggregateId), CreateGuidValue(aggregateId)).Should().BeTrue();
        ContainsComparison(renderedFilter, nameof(Event<OrderAggregate>.Version), "$gte", new BsonInt64(4)).Should().BeTrue();
        ContainsComparison(renderedFilter, nameof(Event<OrderAggregate>.Version), "$lte", new BsonInt64(8)).Should().BeTrue();
        Render(capturedOptions.Sort!).Should().BeEquivalentTo(new BsonDocument(nameof(Event<OrderAggregate>.Version), 1));
    }

    [Test]
    public async Task GetExpectedNextVersionAsync_UsesAggregateAndDescendingVersionSort()
    {
        // Arrange
        var aggregateId = Guid.NewGuid();
        var options = new MongoEventStoreOptions<OrderAggregate>
        {
            CollectionPrefix = "Orders"
        };
        var eventsCollectionMock = new Mock<IMongoCollection<Event<OrderAggregate>>>();
        FilterDefinition<Event<OrderAggregate>>? capturedFilter = null;
        FindOptions<Event<OrderAggregate>, Event<OrderAggregate>>? capturedOptions = null;
        eventsCollectionMock
            .Setup(c => c.FindAsync(
                       It.IsAny<FilterDefinition<Event<OrderAggregate>>>(),
                       It.IsAny<FindOptions<Event<OrderAggregate>, Event<OrderAggregate>>>(),
                       It.IsAny<CancellationToken>()))
            .Callback<FilterDefinition<Event<OrderAggregate>>, FindOptions<Event<OrderAggregate>, Event<OrderAggregate>>,
                CancellationToken>((filter, findOptions, _) =>
            {
                capturedFilter = filter;
                capturedOptions = findOptions;
            })
            .ReturnsAsync(CreateCursor<Event<OrderAggregate>>());

        var sut = CreateEventStore(options, eventsCollectionMock.Object);

        // Act
        var nextVersion = await sut.GetExpectedNextVersionAsync(aggregateId);

        // Assert
        nextVersion.Should().Be(1);
        capturedFilter.Should().NotBeNull();
        capturedOptions.Should().NotBeNull();
        capturedOptions!.Sort.Should().NotBeNull();
        capturedOptions.Limit.Should().Be(1);

        var renderedFilter = Render(capturedFilter!);
        ContainsEquality(renderedFilter, nameof(Event<OrderAggregate>.AggregateId), CreateGuidValue(aggregateId)).Should().BeTrue();
        Render(capturedOptions.Sort!).Should().BeEquivalentTo(new BsonDocument(nameof(Event<OrderAggregate>.Version), -1));
    }

    private static Boolean ContainsComparison(BsonValue value, String field, String comparisonOperator, BsonValue expected)
        => value switch
        {
            BsonDocument document => (document.TryGetValue(field, out var filter) &&
                                      filter is BsonDocument filterDocument &&
                                      filterDocument.TryGetValue(comparisonOperator, out var comparisonValue) &&
                                      comparisonValue == expected) ||
                                     document.Elements.Any(element => ContainsComparison(element.Value, field, comparisonOperator, expected)),
            BsonArray array => array.Any(item => ContainsComparison(item, field, comparisonOperator, expected)),
            _ => false
        };

    private static Boolean ContainsEquality(BsonValue value, String field, BsonValue expected)
        => value switch
        {
            BsonDocument document => (document.TryGetValue(field, out var directValue) &&
                                      directValue == expected) ||
                                     document.Elements.Any(element => ContainsEquality(element.Value, field, expected)),
            BsonArray array => array.Any(item => ContainsEquality(item, field, expected)),
            _ => false
        };

    private static MongoAggregateRepository<OrderAggregate> CreateAggregateRepository(
        MongoEventStoreOptions<OrderAggregate> options,
        IMongoCollection<CheckpointDocument<OrderAggregate>> checkpointCollection,
        IMongoCollection<Event<OrderAggregate>> eventsCollection)
    {
        var databaseMock = new Mock<IMongoDatabase>();
        databaseMock.Setup(d => d.GetCollection<CheckpointDocument<OrderAggregate>>(options.CheckpointCollectionName, null))
                    .Returns(checkpointCollection);
        databaseMock.Setup(d => d.GetCollection<Event<OrderAggregate>>(options.EventsCollectionName, null))
                    .Returns(eventsCollection);

        var mongoHelperMock = new Mock<IMongoHelper>();
        mongoHelperMock.Setup(h => h.Database).Returns(databaseMock.Object);

        return new(mongoHelperMock.Object, options);
    }

    private static IAsyncCursor<TDocument> CreateCursor<TDocument>(params TDocument[] documents)
    {
        var returned = false;
        var cursorMock = new Mock<IAsyncCursor<TDocument>>();
        cursorMock.Setup(c => c.MoveNext(It.IsAny<CancellationToken>()))
                  .Returns(() =>
                  {
                      if (returned)
                      {
                          return false;
                      }

                      returned = true;
                      return documents.Length > 0;
                  });
        cursorMock.Setup(c => c.MoveNextAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(() =>
                  {
                      if (returned)
                      {
                          return false;
                      }

                      returned = true;
                      return documents.Length > 0;
                  });
        cursorMock.Setup(c => c.Current).Returns(documents);
        return cursorMock.Object;
    }

    private static MongoEventStore<OrderAggregate> CreateEventStore(
        MongoEventStoreOptions<OrderAggregate> options,
        IMongoCollection<Event<OrderAggregate>> eventsCollection)
    {
        var databaseMock = new Mock<IMongoDatabase>();
        databaseMock.Setup(d => d.GetCollection<Event<OrderAggregate>>(options.EventsCollectionName, null))
                    .Returns(eventsCollection);

        var mongoHelperMock = new Mock<IMongoHelper>();
        mongoHelperMock.Setup(h => h.Database).Returns(databaseMock.Object);

        return new(mongoHelperMock.Object, options);
    }

    private static BsonBinaryData CreateGuidValue(Guid value)
        => new(value, GuidRepresentation.Standard);

    private static BsonDocument Render(FilterDefinition<Event<OrderAggregate>> filter)
    {
        var serializerRegistry = BsonSerializer.SerializerRegistry;
        var serializer = serializerRegistry.GetSerializer<Event<OrderAggregate>>();
        return filter.Render(new(serializer, serializerRegistry));
    }

    private static BsonDocument Render(FilterDefinition<CheckpointDocument<OrderAggregate>> filter)
    {
        var serializerRegistry = BsonSerializer.SerializerRegistry;
        var serializer = serializerRegistry.GetSerializer<CheckpointDocument<OrderAggregate>>();
        return filter.Render(new(serializer, serializerRegistry));
    }

    private static BsonDocument Render(SortDefinition<Event<OrderAggregate>> sort)
    {
        var serializerRegistry = BsonSerializer.SerializerRegistry;
        var serializer = serializerRegistry.GetSerializer<Event<OrderAggregate>>();
        return sort.Render(new(serializer, serializerRegistry));
    }

    private static BsonDocument Render(SortDefinition<CheckpointDocument<OrderAggregate>> sort)
    {
        var serializerRegistry = BsonSerializer.SerializerRegistry;
        var serializer = serializerRegistry.GetSerializer<CheckpointDocument<OrderAggregate>>();
        return sort.Render(new(serializer, serializerRegistry));
    }
}
