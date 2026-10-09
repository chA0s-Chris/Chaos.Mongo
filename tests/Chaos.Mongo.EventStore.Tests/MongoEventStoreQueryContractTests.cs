// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests;

using Chaos.Mongo.EventStore.Tests.Integration;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Search;
using Moq;
using NUnit.Framework;
using System.Reflection;

public class MongoEventStoreQueryContractTests
{
    /// <summary>
    /// Gets the expected exclusion of observational events: the concrete discriminator is the last element of a
    /// hierarchical discriminator array, or the scalar discriminator itself.
    /// </summary>
    private static BsonDocument ObservationalEventExclusion
        => new(
            "$not",
            new BsonArray
            {
                new BsonDocument(
                    "$in",
                    new BsonArray
                    {
                        new BsonDocument(
                            "$cond",
                            new BsonArray
                            {
                                new BsonDocument("$isArray", "$_t"),
                                new BsonDocument("$arrayElemAt", new BsonArray
                                {
                                    "$_t",
                                    -1
                                }),
                                "$_t"
                            }),
                        new BsonArray
                        {
                            "OrderViewed"
                        }
                    })
            });

    [Test]
    public async Task GetAtRevisionAsync_WhenCheckpointingEnabled_QueriesRevisionCheckpointThenRevisionBoundedEvents()
    {
        // Arrange
        var aggregateId = Guid.NewGuid();
        var options = CreateOptionsWithObservationalEvent();
        var checkpoint = new CheckpointDocument<OrderAggregate>
        {
            Id = new CheckpointId(aggregateId, 4),
            Revision = 3,
            State = new OrderAggregate
            {
                Id = aggregateId,
                CreatedUtc = DateTime.UtcNow,
                Status = "Shipped",
                Version = 4,
                Revision = 3
            }
        };

        var (checkpointCollection, checkpointQueryCapture) =
            CapturingMongoCollectionProxy<CheckpointDocument<OrderAggregate>>.Create(CreateCursor(checkpoint));
        var (eventsCollection, eventQueryCapture) =
            CapturingMongoCollectionProxy<Event<OrderAggregate>>.Create(CreateCursor<Event<OrderAggregate>>());

        var sut = CreateAggregateRepository(options, checkpointCollection, eventsCollection);

        // Act
        var aggregate = await sut.GetAtRevisionAsync(aggregateId, 5);

        // Assert
        aggregate.Should().NotBeNull();
        aggregate.Version.Should().Be(4);
        aggregate.Revision.Should().Be(3);

        var renderedCheckpointFilter = Render(checkpointQueryCapture.CapturedFilter!);
        ContainsEquality(renderedCheckpointFilter, "_id.AggregateId", CreateGuidValue(aggregateId)).Should().BeTrue();
        ContainsComparison(renderedCheckpointFilter, nameof(CheckpointDocument<>.Revision), "$lte", new BsonInt64(5)).Should().BeTrue();
        Render(checkpointQueryCapture.CapturedSort!).Should().BeEquivalentTo(new BsonDocument(nameof(CheckpointDocument<>.Revision), -1));

        var renderedEventFilter = Render(eventQueryCapture.CapturedFilter!);
        ContainsEquality(renderedEventFilter, nameof(Event<OrderAggregate>.AggregateId), CreateGuidValue(aggregateId)).Should().BeTrue();
        ContainsComparison(renderedEventFilter, nameof(Event<OrderAggregate>.Version), "$gte", new BsonInt64(5)).Should().BeTrue();
        ContainsComparison(renderedEventFilter, nameof(Event<OrderAggregate>.Revision), "$lte", new BsonInt64(5)).Should().BeTrue();
        ContainsComparison(renderedEventFilter, nameof(Event<OrderAggregate>.Revision), "$exists", BsonBoolean.False).Should().BeTrue();
        ContainsComparison(renderedEventFilter, nameof(Event<OrderAggregate>.Version), "$lte", new BsonInt64(5)).Should().BeTrue();
        ContainsEquality(renderedEventFilter, "$expr", ObservationalEventExclusion).Should().BeTrue();
        Render(eventQueryCapture.CapturedSort!).Should().BeEquivalentTo(new BsonDocument(nameof(Event<OrderAggregate>.Version), 1));
    }

    [Test]
    public async Task GetAtVersionAsync_WhenCheckpointingEnabled_QueriesLatestCheckpointThenRemainingEvents()
    {
        // Arrange
        var aggregateId = Guid.NewGuid();
        var options = CreateOptionsWithObservationalEvent();
        var checkpoint = new CheckpointDocument<OrderAggregate>
        {
            Id = new CheckpointId(aggregateId, 3),
            State = new OrderAggregate
            {
                Id = aggregateId,
                CreatedUtc = DateTime.UtcNow,
                CustomerName = "Charlie",
                Status = "Created",
                Version = 3
            }
        };

        var (checkpointCollection, checkpointQueryCapture) =
            CapturingMongoCollectionProxy<CheckpointDocument<OrderAggregate>>.Create(CreateCursor(checkpoint));
        var (eventsCollection, eventQueryCapture) =
            CapturingMongoCollectionProxy<Event<OrderAggregate>>.Create(CreateCursor<Event<OrderAggregate>>());

        var sut = CreateAggregateRepository(options, checkpointCollection, eventsCollection);

        // Act
        var aggregate = await sut.GetAtVersionAsync(aggregateId, 7);

        // Assert
        aggregate.Should().NotBeNull();
        aggregate.Version.Should().Be(3);

        checkpointQueryCapture.CapturedFilter.Should().NotBeNull();
        checkpointQueryCapture.CapturedSort.Should().NotBeNull();
        var renderedCheckpointFilter = Render(checkpointQueryCapture.CapturedFilter!);
        ContainsEquality(renderedCheckpointFilter, "_id.AggregateId", CreateGuidValue(aggregateId)).Should().BeTrue();
        ContainsComparison(renderedCheckpointFilter, "_id.Version", "$lte", new BsonInt64(7)).Should().BeTrue();
        Render(checkpointQueryCapture.CapturedSort!).Should().BeEquivalentTo(new BsonDocument("_id.Version", -1));

        eventQueryCapture.CapturedFilter.Should().NotBeNull();
        eventQueryCapture.CapturedSort.Should().NotBeNull();
        var renderedEventFilter = Render(eventQueryCapture.CapturedFilter!);
        ContainsEquality(renderedEventFilter, nameof(Event<OrderAggregate>.AggregateId), CreateGuidValue(aggregateId)).Should().BeTrue();
        ContainsComparison(renderedEventFilter, nameof(Event<OrderAggregate>.Version), "$gte", new BsonInt64(4)).Should().BeTrue();
        ContainsComparison(renderedEventFilter, nameof(Event<OrderAggregate>.Version), "$lte", new BsonInt64(7)).Should().BeTrue();
        ContainsEquality(renderedEventFilter, "$expr", ObservationalEventExclusion).Should().BeTrue();
        Render(eventQueryCapture.CapturedSort!).Should().BeEquivalentTo(new BsonDocument(nameof(Event<OrderAggregate>.Version), 1));
    }

    [Test]
    public async Task GetAtVersionAsync_WithoutObservationalEventTypes_DoesNotFilterDiscriminators()
    {
        // Arrange
        var options = new MongoEventStoreOptions<OrderAggregate>
        {
            CollectionPrefix = "Orders"
        };
        var (checkpointCollection, _) =
            CapturingMongoCollectionProxy<CheckpointDocument<OrderAggregate>>.Create(CreateCursor<CheckpointDocument<OrderAggregate>>());
        var (eventsCollection, eventQueryCapture) =
            CapturingMongoCollectionProxy<Event<OrderAggregate>>.Create(CreateCursor<Event<OrderAggregate>>());

        var sut = CreateAggregateRepository(options, checkpointCollection, eventsCollection);

        // Act
        var aggregate = await sut.GetAtVersionAsync(Guid.NewGuid(), 7);

        // Assert
        aggregate.Should().BeNull();
        ContainsField(Render(eventQueryCapture.CapturedFilter!), "$expr").Should().BeFalse();
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
        var (eventsCollection, queryCapture) =
            CapturingMongoCollectionProxy<Event<OrderAggregate>>.Create(CreateCursor<Event<OrderAggregate>>());

        var sut = CreateEventStore(options, eventsCollection);

        // Act
        await foreach (var _ in sut.GetEventStream(aggregateId, 4, 8)) { }

        // Assert
        queryCapture.CapturedFilter.Should().NotBeNull();
        queryCapture.CapturedSort.Should().NotBeNull();

        var renderedFilter = Render(queryCapture.CapturedFilter!);
        ContainsEquality(renderedFilter, nameof(Event<OrderAggregate>.AggregateId), CreateGuidValue(aggregateId)).Should().BeTrue();
        ContainsComparison(renderedFilter, nameof(Event<OrderAggregate>.Version), "$gte", new BsonInt64(4)).Should().BeTrue();
        ContainsComparison(renderedFilter, nameof(Event<OrderAggregate>.Version), "$lte", new BsonInt64(8)).Should().BeTrue();
        Render(queryCapture.CapturedSort!).Should().BeEquivalentTo(new BsonDocument(nameof(Event<OrderAggregate>.Version), 1));
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
        var (eventsCollection, queryCapture) =
            CapturingMongoCollectionProxy<Event<OrderAggregate>>.Create(CreateCursor<Event<OrderAggregate>>());

        var sut = CreateEventStore(options, eventsCollection);

        // Act
        var nextVersion = await sut.GetExpectedNextVersionAsync(aggregateId);

        // Assert
        nextVersion.Should().Be(1);
        queryCapture.CapturedFilter.Should().NotBeNull();
        queryCapture.CapturedSort.Should().NotBeNull();
        queryCapture.CapturedLimit.Should().Be(1);

        var renderedFilter = Render(queryCapture.CapturedFilter!);
        ContainsEquality(renderedFilter, nameof(Event<OrderAggregate>.AggregateId), CreateGuidValue(aggregateId)).Should().BeTrue();
        Render(queryCapture.CapturedSort!).Should().BeEquivalentTo(new BsonDocument(nameof(Event<OrderAggregate>.Version), -1));
        Render(queryCapture.CapturedProjection!).Should().BeEquivalentTo(new BsonDocument
        {
            { nameof(Event<OrderAggregate>.Version), 1 },
            { nameof(Event<OrderAggregate>.Revision), 1 },
            { "_id", 0 }
        });
    }

    private static void BootstrapSerialization(MongoEventStoreOptions<OrderAggregate> options)
    {
        MongoEventStoreSerializationSetup.EnsureGuidSerializer();
        MongoEventStoreSerializationSetup.RegisterClassMaps(options);
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

    private static Boolean ContainsField(BsonValue value, String field)
        => value switch
        {
            BsonDocument document => document.Contains(field) || document.Elements.Any(element => ContainsField(element.Value, field)),
            BsonArray array => array.Any(item => ContainsField(item, field)),
            _ => false
        };

    private static MongoAggregateRepository<OrderAggregate> CreateAggregateRepository(
        MongoEventStoreOptions<OrderAggregate> options,
        IMongoCollection<CheckpointDocument<OrderAggregate>> checkpointCollection,
        IMongoCollection<Event<OrderAggregate>> eventsCollection)
    {
        BootstrapSerialization(options);

        var databaseMock = new Mock<IMongoDatabase>();
        databaseMock.Setup(d => d.GetCollection<CheckpointDocument<OrderAggregate>>(options.CheckpointCollectionName, null))
                    .Returns(checkpointCollection);
        databaseMock.Setup(d => d.GetCollection<Event<OrderAggregate>>(options.EventsCollectionName, null))
                    .Returns(eventsCollection);

        var mongoHelperMock = new Mock<IMongoHelper>();
        mongoHelperMock.Setup(h => h.Database).Returns(databaseMock.Object);

        return new MongoAggregateRepository<OrderAggregate>(mongoHelperMock.Object, options);
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
        BootstrapSerialization(options);

        var databaseMock = new Mock<IMongoDatabase>();
        databaseMock.Setup(d => d.GetCollection<Event<OrderAggregate>>(options.EventsCollectionName, null))
                    .Returns(eventsCollection);

        var mongoHelperMock = new Mock<IMongoHelper>();
        mongoHelperMock.Setup(h => h.Database).Returns(databaseMock.Object);

        return new MongoEventStore<OrderAggregate>(mongoHelperMock.Object, options);
    }

    private static BsonBinaryData CreateGuidValue(Guid value)
        => new(value, GuidRepresentation.Standard);

    private static MongoEventStoreOptions<OrderAggregate> CreateOptionsWithObservationalEvent()
    {
        var options = new MongoEventStoreOptions<OrderAggregate>
        {
            CollectionPrefix = "Orders",
            CheckpointInterval = 3
        };
        options.EventTypes[typeof(OrderViewedEvent)] = "OrderViewed";
        return options;
    }

    private static BsonDocument Render(FilterDefinition<Event<OrderAggregate>> filter)
    {
        var serializerRegistry = BsonSerializer.SerializerRegistry;
        var serializer = serializerRegistry.GetSerializer<Event<OrderAggregate>>();
        return filter.Render(new RenderArgs<Event<OrderAggregate>>(serializer, serializerRegistry));
    }

    private static BsonDocument Render(FilterDefinition<CheckpointDocument<OrderAggregate>> filter)
    {
        var serializerRegistry = BsonSerializer.SerializerRegistry;
        var serializer = serializerRegistry.GetSerializer<CheckpointDocument<OrderAggregate>>();
        return filter.Render(new RenderArgs<CheckpointDocument<OrderAggregate>>(serializer, serializerRegistry));
    }

    private static BsonDocument Render(ProjectionDefinition<Event<OrderAggregate>, BsonDocument> projection)
    {
        var serializerRegistry = BsonSerializer.SerializerRegistry;
        var serializer = serializerRegistry.GetSerializer<Event<OrderAggregate>>();
        return projection.Render(new RenderArgs<Event<OrderAggregate>>(serializer, serializerRegistry)).Document;
    }

    private static BsonDocument Render(SortDefinition<Event<OrderAggregate>> sort)
    {
        var serializerRegistry = BsonSerializer.SerializerRegistry;
        var serializer = serializerRegistry.GetSerializer<Event<OrderAggregate>>();
        return sort.Render(new RenderArgs<Event<OrderAggregate>>(serializer, serializerRegistry));
    }

    private static BsonDocument Render(SortDefinition<CheckpointDocument<OrderAggregate>> sort)
    {
        var serializerRegistry = BsonSerializer.SerializerRegistry;
        var serializer = serializerRegistry.GetSerializer<CheckpointDocument<OrderAggregate>>();
        return sort.Render(new RenderArgs<CheckpointDocument<OrderAggregate>>(serializer, serializerRegistry));
    }

    private class CapturingMongoCollectionProxy<TDocument> : DispatchProxy
    {
        private static readonly IMongoIndexManager<TDocument> IndexManager = Mock.Of<IMongoIndexManager<TDocument>>();
        private readonly IMongoDatabase _database = Mock.Of<IMongoDatabase>();
        private readonly MongoCollectionSettings _settings = new();

        private IMongoCollection<TDocument> _collection = null!;
        private IAsyncCursor<TDocument> _cursor = null!;

        public FilterDefinition<TDocument>? CapturedFilter { get; private set; }

        public Int32? CapturedLimit { get; private set; }

        public ProjectionDefinition<TDocument, BsonDocument>? CapturedProjection { get; private set; }

        public SortDefinition<TDocument>? CapturedSort { get; private set; }

        public static (IMongoCollection<TDocument> Collection, CapturingMongoCollectionProxy<TDocument> Proxy) Create(
            IAsyncCursor<TDocument> cursor)
        {
            var instance = Create(typeof(IMongoCollection<TDocument>), typeof(CapturingMongoCollectionProxy<TDocument>));
            var collection = (IMongoCollection<TDocument>)instance;
            var proxy = (CapturingMongoCollectionProxy<TDocument>)instance;
            proxy._collection = collection;
            proxy._cursor = cursor;
            return (collection, proxy);
        }

        protected override Object? Invoke(MethodInfo? targetMethod, Object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);

            return targetMethod.Name switch
            {
                "FindAsync" when targetMethod.IsGenericMethod => HandleFindAsync(targetMethod.GetGenericArguments()[0], args),
                "FindSync" when targetMethod.IsGenericMethod => HandleFindSync(targetMethod.GetGenericArguments()[0], args),
                "get_CollectionNamespace" => new CollectionNamespace(new DatabaseNamespace("Tests"), typeof(TDocument).Name),
                "get_Database" => _database,
                "get_DocumentSerializer" => BsonSerializer.SerializerRegistry.GetSerializer<TDocument>(),
                "get_Indexes" => IndexManager,
                "get_SearchIndexes" => Mock.Of<IMongoSearchIndexManager>(),
                "get_Settings" => _settings,
                "WithReadConcern" or "WithReadPreference" or "WithWriteConcern" => _collection,
                _ => throw new NotSupportedException($"Method '{targetMethod.Name}' is not supported by the capturing test collection.")
            };
        }

        private static NotSupportedException UnsupportedProjection(Type projectionType)
            => new($"Projection '{projectionType}' is not supported by the capturing test collection.");

        /// <summary>
        /// Captures typed finds and finds that project elements into a <see cref="BsonDocument"/>, such as
        /// the stream-head read.
        /// </summary>
        private void CaptureFind(Object?[]? args)
        {
            var (filterIndex, optionsIndex) = args?.Length switch
            {
                3 => (0, 1),
                4 => (1, 2),
                _ => throw new NotSupportedException("Unexpected Find invocation shape.")
            };

            CapturedFilter = (FilterDefinition<TDocument>)args[filterIndex]!;
            switch (args[optionsIndex])
            {
                case FindOptions<TDocument, TDocument> options:
                    CapturedSort = options.Sort;
                    CapturedLimit = options.Limit;
                    break;
                case FindOptions<TDocument, BsonDocument> options:
                    CapturedSort = options.Sort;
                    CapturedLimit = options.Limit;
                    CapturedProjection = options.Projection;
                    break;
                default:
                    throw new NotSupportedException("Unexpected Find options.");
            }
        }

        private Object HandleFindAsync(Type projectionType, Object?[]? args)
        {
            CaptureFind(args);
            if (projectionType == typeof(TDocument))
            {
                return Task.FromResult(_cursor);
            }

            return projectionType == typeof(BsonDocument)
                ? Task.FromResult(CreateCursor<BsonDocument>())
                : throw UnsupportedProjection(projectionType);
        }

        private Object HandleFindSync(Type projectionType, Object?[]? args)
        {
            CaptureFind(args);
            if (projectionType == typeof(TDocument))
            {
                return _cursor;
            }

            return projectionType == typeof(BsonDocument)
                ? CreateCursor<BsonDocument>()
                : throw UnsupportedProjection(projectionType);
        }
    }
}
