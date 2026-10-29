// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests;

using Chaos.Mongo.EventStore.Integrity;
using Chaos.Mongo.EventStore.Tests.Integration;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Search;
using Moq;
using NUnit.Framework;

public class MongoEventStoreQueryContractTests
{
    /// <summary>
    /// Gets the expected exclusion of observational events: the concrete discriminator is the last element of a
    /// hierarchical discriminator array, or the scalar discriminator itself. The erasure event of the event store
    /// is always excluded.
    /// </summary>
    private static BsonDocument ObservationalEventExclusion
        => CreateObservationalEventExclusion("OrderViewed", PersonalDataErased<OrderAggregate>.Discriminator);

    [Test]
    [TestCase(true)]
    [TestCase(false)]
    public async Task GetAtRevisionAsync_TargetBelowStreamRevision_BoundsReplayByFirstEventAboveTarget(Boolean eventAboveTargetFound)
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

        var (checkpointCollection, _) =
            CapturingMongoCollectionProxy<CheckpointDocument<OrderAggregate>>.Create(CreateCursor(checkpoint));
        var (eventsCollection, eventQueryCapture) =
            CapturingMongoCollectionProxy<Event<OrderAggregate>>.Create(CreateCursor<Event<OrderAggregate>>());
        eventQueryCapture.EnqueueProjectedResult(new BsonDocument
        {
            { nameof(Event<OrderAggregate>.Version), 40L },
            { nameof(Event<OrderAggregate>.Revision), 20L }
        });
        if (eventAboveTargetFound)
        {
            eventQueryCapture.EnqueueProjectedResult(new BsonDocument(nameof(Event<OrderAggregate>.Version), 9L));
        }

        var sut = CreateAggregateRepository(options, checkpointCollection, eventsCollection);

        // Act
        await sut.GetAtRevisionAsync(aggregateId, 5);

        // Assert
        eventQueryCapture.CapturedFinds.Should().HaveCount(3);
        var (headRead, boundaryLookup, replay) = (eventQueryCapture.CapturedFinds[0],
                                                  eventQueryCapture.CapturedFinds[1],
                                                  eventQueryCapture.CapturedFinds[2]);

        ContainsEquality(Render(headRead.Filter), nameof(Event<OrderAggregate>.AggregateId), CreateGuidValue(aggregateId)).Should().BeTrue();
        Render(headRead.Sort!).Should().BeEquivalentTo(new BsonDocument(nameof(Event<OrderAggregate>.Version), -1));
        headRead.Limit.Should().Be(1);

        // The boundary lookup is an index scan from the checkpoint that stops at the first event above the target.
        var renderedBoundary = Render(boundaryLookup.Filter);
        ContainsEquality(renderedBoundary, nameof(Event<OrderAggregate>.AggregateId), CreateGuidValue(aggregateId)).Should().BeTrue();
        ContainsComparison(renderedBoundary, nameof(Event<OrderAggregate>.Version), "$gte", new BsonInt64(5)).Should().BeTrue();
        ContainsComparison(renderedBoundary, nameof(Event<OrderAggregate>.Revision), "$gt", new BsonInt64(5)).Should().BeTrue();
        ContainsComparison(renderedBoundary, nameof(Event<OrderAggregate>.Revision), "$exists", BsonBoolean.False).Should().BeTrue();
        ContainsComparison(renderedBoundary, nameof(Event<OrderAggregate>.Version), "$gt", new BsonInt64(5)).Should().BeTrue();
        Render(boundaryLookup.Sort!).Should().BeEquivalentTo(new BsonDocument(nameof(Event<OrderAggregate>.Version), 1));
        boundaryLookup.Limit.Should().Be(1);
        Render(boundaryLookup.Projection!).Should().BeEquivalentTo(new BsonDocument
        {
            { nameof(Event<OrderAggregate>.Version), 1 },
            { "_id", 0 }
        });

        // The replay keeps the revision filter and adds the upper bound only when the boundary was found.
        var renderedReplay = Render(replay.Filter);
        ContainsComparison(renderedReplay, nameof(Event<OrderAggregate>.Version), "$gte", new BsonInt64(5)).Should().BeTrue();
        ContainsComparison(renderedReplay, nameof(Event<OrderAggregate>.Revision), "$lte", new BsonInt64(5)).Should().BeTrue();
        ContainsComparison(renderedReplay, nameof(Event<OrderAggregate>.Version), "$lt", new BsonInt64(9)).Should().Be(eventAboveTargetFound);
        ContainsEquality(renderedReplay, "$expr", ObservationalEventExclusion).Should().BeTrue();
    }

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

        // The stream has no event above the target, so only the head is read and the replay stays unbounded.
        eventQueryCapture.CapturedFinds.Should().HaveCount(2);
        ContainsOperator(renderedEventFilter, nameof(Event<OrderAggregate>.Version), "$lt").Should().BeFalse();
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
    public async Task GetAtVersionAsync_WithoutObservationalEventTypes_ExcludesOnlyTheErasureEvent()
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
        ContainsEquality(Render(eventQueryCapture.CapturedFilter!),
                         "$expr",
                         CreateObservationalEventExclusion(PersonalDataErased<OrderAggregate>.Discriminator))
            .Should().BeTrue();
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
        await foreach (var _ in sut.GetEventStreamAsync(aggregateId, 4, 8)) { }

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

    private static Boolean ContainsOperator(BsonValue value, String field, String comparisonOperator)
        => value switch
        {
            BsonDocument document => (document.TryGetValue(field, out var filter) &&
                                      filter is BsonDocument filterDocument &&
                                      filterDocument.Contains(comparisonOperator)) ||
                                     document.Elements.Any(element => ContainsOperator(element.Value, field, comparisonOperator)),
            BsonArray array => array.Any(item => ContainsOperator(item, field, comparisonOperator)),
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

    /// <summary>
    /// Creates a cursor without Moq, because Moq's dynamically generated proxy assembly cannot access an internal
    /// <typeparamref name="TDocument"/>, such as <see cref="CheckpointDocument{TAggregate}"/>.
    /// </summary>
    private static IAsyncCursor<TDocument> CreateCursor<TDocument>(params TDocument[] documents)
        => new SingleBatchAsyncCursor<TDocument>(documents);

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

    private static BsonDocument CreateObservationalEventExclusion(params String[] discriminators)
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
                        new BsonDocument("$literal", new BsonArray(discriminators))
                    })
            });

    private static MongoEventStoreOptions<OrderAggregate> CreateOptionsWithObservationalEvent()
    {
        var options = new MongoEventStoreOptions<OrderAggregate>
        {
            CollectionPrefix = "Orders",
            CheckpointInterval = 3
        };
        options.AddEventType(typeof(OrderViewedEvent), "OrderViewed");
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

    private sealed record CapturedFind<TDocument>(
        FilterDefinition<TDocument> Filter,
        SortDefinition<TDocument>? Sort,
        Int32? Limit,
        ProjectionDefinition<TDocument, BsonDocument>? Projection);

    /// <summary>
    /// Implemented directly on <see cref="NotSupportedMongoCollection{TDocument}"/>, instead of through
    /// <see cref="System.Reflection.DispatchProxy"/>, because <see cref="System.Reflection.DispatchProxy"/>'s
    /// dynamically generated proxy assembly cannot access an internal <typeparamref name="TDocument"/>, such as
    /// <see cref="CheckpointDocument{TAggregate}"/>.
    /// </summary>
    private sealed class CapturingMongoCollectionProxy<TDocument> : NotSupportedMongoCollection<TDocument>
    {
        private readonly IAsyncCursor<TDocument> _cursor;
        private readonly IMongoDatabase _database = Mock.Of<IMongoDatabase>();
        private readonly IMongoIndexManager<TDocument> _indexManager = new PlaceholderMongoIndexManager<TDocument>();

        private readonly Queue<BsonDocument[]> _projectedResults = new();
        private readonly MongoCollectionSettings _settings = new();

        private CapturingMongoCollectionProxy(IAsyncCursor<TDocument> cursor) => _cursor = cursor;

        public FilterDefinition<TDocument>? CapturedFilter { get; private set; }

        /// <summary>
        /// Gets every captured find in invocation order; the other captured properties describe the last one.
        /// </summary>
        public List<CapturedFind<TDocument>> CapturedFinds { get; } = [];

        public Int32? CapturedLimit { get; private set; }

        public ProjectionDefinition<TDocument, BsonDocument>? CapturedProjection { get; private set; }

        public SortDefinition<TDocument>? CapturedSort { get; private set; }

        public override CollectionNamespace CollectionNamespace { get; } = new(new DatabaseNamespace("Tests"), typeof(TDocument).Name);

        public override IMongoDatabase Database => _database;

        public override IBsonSerializer<TDocument> DocumentSerializer => BsonSerializer.SerializerRegistry.GetSerializer<TDocument>();

        public override IMongoIndexManager<TDocument> Indexes => _indexManager;

        public override IMongoSearchIndexManager SearchIndexes { get; } = Mock.Of<IMongoSearchIndexManager>();

        public override MongoCollectionSettings Settings => _settings;

        public static (IMongoCollection<TDocument> Collection, CapturingMongoCollectionProxy<TDocument> Proxy) Create(
            IAsyncCursor<TDocument> cursor)
        {
            var proxy = new CapturingMongoCollectionProxy<TDocument>(cursor);
            return (proxy, proxy);
        }

        /// <summary>
        /// Queues the documents returned by the next find that projects into a <see cref="BsonDocument"/>. Finds
        /// without a queued result return no documents.
        /// </summary>
        public void EnqueueProjectedResult(params BsonDocument[] documents) => _projectedResults.Enqueue(documents);

        public override Task<IAsyncCursor<TProjection>> FindAsync<TProjection>(
            FilterDefinition<TDocument> filter, FindOptions<TDocument, TProjection> options, CancellationToken cancellationToken)
            => Task.FromResult(HandleFind<TProjection>(filter, options));

        public override Task<IAsyncCursor<TProjection>> FindAsync<TProjection>(
            IClientSessionHandle session, FilterDefinition<TDocument> filter, FindOptions<TDocument, TProjection> options,
            CancellationToken cancellationToken)
            => Task.FromResult(HandleFind<TProjection>(filter, options));

        public override IAsyncCursor<TProjection> FindSync<TProjection>(
            FilterDefinition<TDocument> filter, FindOptions<TDocument, TProjection> options, CancellationToken cancellationToken)
            => HandleFind<TProjection>(filter, options);

        public override IAsyncCursor<TProjection> FindSync<TProjection>(
            IClientSessionHandle session, FilterDefinition<TDocument> filter, FindOptions<TDocument, TProjection> options,
            CancellationToken cancellationToken)
            => HandleFind<TProjection>(filter, options);

        public override IMongoCollection<TDocument> WithReadConcern(ReadConcern readConcern) => this;

        public override IMongoCollection<TDocument> WithReadPreference(ReadPreference readPreference) => this;

        public override IMongoCollection<TDocument> WithWriteConcern(WriteConcern writeConcern) => this;

        private static NotSupportedException UnsupportedProjection(Type projectionType)
            => new($"Projection '{projectionType}' is not supported by the capturing test collection.");

        /// <summary>
        /// Captures typed finds and finds that project elements into a <see cref="BsonDocument"/>, such as
        /// the stream-head read.
        /// </summary>
        private void CaptureFind(FilterDefinition<TDocument> filter, Object? options)
        {
            CapturedFilter = filter;
            switch (options)
            {
                case FindOptions<TDocument, TDocument> typed:
                    CapturedSort = typed.Sort;
                    CapturedLimit = typed.Limit;
                    CapturedProjection = null;
                    break;
                case FindOptions<TDocument, BsonDocument> projected:
                    CapturedSort = projected.Sort;
                    CapturedLimit = projected.Limit;
                    CapturedProjection = projected.Projection;
                    break;
                default:
                    throw new NotSupportedException("Unexpected Find options.");
            }

            CapturedFinds.Add(new CapturedFind<TDocument>(CapturedFilter, CapturedSort, CapturedLimit, CapturedProjection));
        }

        private IAsyncCursor<TProjection> HandleFind<TProjection>(FilterDefinition<TDocument> filter, Object? options)
        {
            CaptureFind(filter, options);
            if (typeof(TProjection) == typeof(TDocument))
            {
                return (IAsyncCursor<TProjection>)_cursor;
            }

            return typeof(TProjection) == typeof(BsonDocument)
                ? (IAsyncCursor<TProjection>)NextProjectedCursor()
                : throw UnsupportedProjection(typeof(TProjection));
        }

        private IAsyncCursor<BsonDocument> NextProjectedCursor()
            => CreateCursor(_projectedResults.Count > 0 ? _projectedResults.Dequeue() : []);
    }

    /// <summary>
    /// A minimal <see cref="IAsyncCursor{TDocument}"/> that returns its documents in a single batch, hand-written
    /// instead of mocked, because Moq's dynamically generated proxy assembly cannot access an internal
    /// <typeparamref name="TDocument"/>, such as <see cref="CheckpointDocument{TAggregate}"/>.
    /// </summary>
    private sealed class SingleBatchAsyncCursor<TDocument> : IAsyncCursor<TDocument>
    {
        private readonly TDocument[] _documents;
        private Boolean _returned;

        public SingleBatchAsyncCursor(TDocument[] documents) => _documents = documents;

        public IEnumerable<TDocument> Current => _documents;

        public Boolean MoveNext(CancellationToken cancellationToken = default)
        {
            if (_returned)
            {
                return false;
            }

            _returned = true;
            return _documents.Length > 0;
        }

        public Task<Boolean> MoveNextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(MoveNext(cancellationToken));

        public void Dispose() { }
    }
}
