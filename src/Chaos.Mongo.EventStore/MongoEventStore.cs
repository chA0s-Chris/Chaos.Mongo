// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore;

using Chaos.Mongo.EventStore.Errors;
using Chaos.Mongo.EventStore.Integrity;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using System.Runtime.CompilerServices;

/// <summary>
/// MongoDB-backed event store implementation for a specific aggregate type.
/// </summary>
/// <typeparam name="TAggregate">The aggregate type.</typeparam>
public sealed class MongoEventStore<TAggregate> : IEventStore<TAggregate>
    where TAggregate : class, IAggregate, new()
{
    private readonly String _aggregateTypeName;
    private readonly IMongoHelper _mongoHelper;
    private readonly MongoEventStoreOptions<TAggregate> _options;

    public MongoEventStore(IMongoHelper mongoHelper, MongoEventStoreOptions<TAggregate> options)
    {
        ArgumentNullException.ThrowIfNull(mongoHelper);
        ArgumentNullException.ThrowIfNull(options);
        _mongoHelper = mongoHelper;
        _options = options;
        _aggregateTypeName = typeof(TAggregate).Name;
    }

    /// <summary>
    /// Creates an identifier for an event that was appended without one. Version 7 GUIDs are
    /// time-ordered and reduce fragmentation of the unique <c>_id</c> index on the events
    /// collection, so they are preferred wherever the target framework provides them.
    /// </summary>
    private static Guid CreateEventId()
#if NET9_0_OR_GREATER
        => Guid.CreateVersion7();
#else
        => Guid.NewGuid();
#endif

    private static String GetDuplicateKeyMessage(MongoException ex)
        => ex switch
        {
            MongoWriteException writeException => writeException.WriteError?.Message ?? ex.Message,
            MongoBulkWriteException bulkWriteException => String.Join(" | ", bulkWriteException.WriteErrors.Select(error => error.Message)),
            ClientBulkWriteException clientBulkWriteException => String.Join(" | ", clientBulkWriteException.WriteErrors.Values.Select(error => error.Message)),
            _ => ex.Message
        };

    private static Boolean IsDuplicateKeyException(MongoException ex)
        => ex switch
        {
            MongoCommandException { Code: 11000 } => true,
            MongoWriteException { WriteError.Category: ServerErrorCategory.DuplicateKey } => true,
            MongoBulkWriteException bulkWriteException => bulkWriteException.WriteErrors.Any(error => error.Category == ServerErrorCategory.DuplicateKey),
            ClientBulkWriteException clientBulkWriteException => clientBulkWriteException.WriteErrors.Values.Any(error => error.Category ==
                                                                                                                     ServerErrorCategory.DuplicateKey),
            _ => false
        };

    private static FilterDefinition<BsonDocument> RenderFilter<TDocument>(
        IMongoCollection<TDocument> collection,
        FilterDefinition<TDocument> filter)
    {
        var renderedFilter = filter.Render(
            new RenderArgs<TDocument>(collection.DocumentSerializer, collection.Settings.SerializerRegistry));
        return new BsonDocumentFilterDefinition<BsonDocument>(renderedFilter);
    }

    /// <summary>
    /// Serializes an event in the element order MongoDB stores it. The class-map serializer writes the
    /// discriminator before <c>_id</c>, but the server always moves <c>_id</c> to the front. Doing so
    /// before insertion keeps the bytes that are hashed identical to the bytes that are stored.
    /// </summary>
    private static BsonDocument SerializeEvent(Event<TAggregate> @event)
    {
        var document = @event.ToBsonDocument();
        var idIndex = document.IndexOfName("_id");
        if (idIndex > 0)
        {
            var idElement = document.GetElement(idIndex);
            document.RemoveAt(idIndex);
            document.InsertAt(0, idElement);
        }

        return document;
    }

    /// <summary>
    /// Creates the exception describing an append whose first event version was already committed.
    /// Resubmitting an event that is already stored is an idempotent retry rather than a conflict,
    /// so the stored event IDs decide which exception the caller sees.
    /// </summary>
    private async Task<MongoEventStoreException> CreateStaleVersionExceptionAsync(
        List<Event<TAggregate>> eventList,
        Int64 currentVersion,
        CancellationToken cancellationToken)
    {
        var eventIds = eventList.Select(e => e.Id).ToList();

        var alreadyStored = await GetEventsCollection()
                                  .Find(Builders<Event<TAggregate>>.Filter.In(e => e.Id, eventIds))
                                  .AnyAsync(cancellationToken);

        if (alreadyStored)
        {
            return new MongoDuplicateEventException(
                "An event with the same ID already exists (idempotency conflict).");
        }

        return new MongoConcurrencyException(
            $"A concurrency conflict occurred — version {eventList[0].Version} of this aggregate was already committed. " +
            $"The aggregate is at version {currentVersion}.");
    }

    private async Task EnsureBulkWriteOptimizationSupportedAsync(CancellationToken cancellationToken)
    {
        if (!_options.BulkWriteOptimizationEnabled)
        {
            return;
        }

        await MongoEventStoreBulkWriteSupport.EnsureSupportedAsync(
            _mongoHelper.Client,
            _mongoHelper.Database,
            cancellationToken);
    }

    private IMongoCollection<CheckpointDocument<TAggregate>> GetCheckpointCollection()
        => _mongoHelper.Database.GetCollection<CheckpointDocument<TAggregate>>(_options.CheckpointCollectionName);

    private IMongoCollection<BsonDocument> GetEventDocumentsCollection()
        => _mongoHelper.Database.GetCollection<BsonDocument>(_options.EventsCollectionName);

    private IMongoCollection<Event<TAggregate>> GetEventsCollection()
        => _mongoHelper.Database.GetCollection<Event<TAggregate>>(_options.EventsCollectionName);

    private IMongoCollection<TAggregate> GetReadModelCollection()
        => _mongoHelper.Database.GetCollection<TAggregate>(_options.ReadModelCollectionName);

    private async Task<Byte[]> ReadPredecessorHashAsync(
        IClientSessionHandle session,
        Guid aggregateId,
        Int64 version,
        CancellationToken cancellationToken)
    {
        var predecessor = await GetEventDocumentsCollection()
                                .Find(session, EventDocumentFields<TAggregate>.ForVersion<BsonDocument>(aggregateId, version))
                                .Project(Builders<BsonDocument>.Projection.Include(EventIntegrityChain.ElementName))
                                .FirstOrDefaultAsync(cancellationToken);

        if (predecessor is null)
        {
            throw new MongoEventStoreException(
                $"Version {version} of aggregate '{aggregateId}' is missing, so its integrity chain cannot be continued.");
        }

        if (!predecessor.TryGetValue(EventIntegrityChain.ElementName, out var integrityValue) ||
            integrityValue is not BsonDocument integrityDocument)
        {
            throw new MongoEventStoreException(
                $"Version {version} of aggregate '{aggregateId}' is not sealed, so its integrity chain cannot be continued.");
        }

        return BsonSerializer.Deserialize<EventIntegrity>(integrityDocument).Hash;
    }

    /// <summary>
    /// Seals the serialized events into the stream's hash chain. Runs inside the append transaction,
    /// so the predecessor is read with the same snapshot that the unique version index protects.
    /// Returns new documents and leaves <paramref name="eventDocuments"/> unchanged, keeping the
    /// callback repeatable when the driver retries the transaction.
    /// </summary>
    private async Task<List<BsonDocument>> SealEventDocumentsAsync(
        IClientSessionHandle session,
        List<Event<TAggregate>> eventList,
        List<BsonDocument> eventDocuments,
        CancellationToken cancellationToken)
    {
        var aggregateId = eventList[0].AggregateId;
        var firstVersion = eventList[0].Version;
        var previousHash = firstVersion == 1
            ? EventIntegrityChain.ComputeGenesis(_aggregateTypeName, aggregateId)
            : await ReadPredecessorHashAsync(session, aggregateId, firstVersion - 1, cancellationToken);

        var sealedDocuments = new List<BsonDocument>(eventDocuments.Count);
        for (var index = 0; index < eventDocuments.Count; index++)
        {
            var integrity = EventIntegrityChain.Seal(eventDocuments[index].ToBson(), previousHash, IntegritySealMode.Append);
            sealedDocuments.Add(new BsonDocument(eventDocuments[index].Elements)
            {
                { EventIntegrityChain.ElementName, integrity.ToBsonDocument() }
            });

            eventList[index].Integrity = integrity;
            previousHash = integrity.Hash;
        }

        return sealedDocuments;
    }

    /// <inheritdoc/>
    public async Task<TAggregate> AppendEventsAsync(
        IEnumerable<Event<TAggregate>> events,
        Func<IClientSessionHandle, TAggregate, IMongoHelper, CancellationToken, Task>? onBeforeCommit = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        var eventList = events.ToList();

        if (eventList.Count == 0)
            throw new ArgumentException("At least one event is required.", nameof(events));

        var now = DateTime.UtcNow;
        foreach (var @event in eventList)
        {
            @event.AggregateType = _aggregateTypeName;

            // Integrity data is owned by the event store and must never enter the hashed document.
            @event.Integrity = null;

            if (@event.CreatedUtc == default)
            {
                @event.CreatedUtc = now;
            }

            // The bulk-write path serializes events directly and bypasses the driver's
            // id generation, so assign the id here to keep both append paths identical.
            if (@event.Id == Guid.Empty)
            {
                @event.Id = CreateEventId();
            }
        }

        var aggregateId = eventList[0].AggregateId;
        var readModelCollection = GetReadModelCollection();

        // 1. Load or create aggregate (outside transaction for validation)
        var aggregate = await readModelCollection
                              .Find(Builders<TAggregate>.Filter.Eq(a => a.Id, aggregateId))
                              .FirstOrDefaultAsync(cancellationToken);

        aggregate ??= new TAggregate
        {
            Id = aggregateId,
            CreatedUtc = now
        };

        // 2. Validate events: same aggregate, sequential versions
        foreach (var @event in eventList)
        {
            if (@event.AggregateId != aggregateId)
            {
                throw new ArgumentException(
                    $"All events must target the same aggregate. Expected '{aggregateId}', but found '{@event.AggregateId}'.",
                    nameof(events));
            }
        }

        var firstVersion = eventList[0].Version;

        // Check the batch against itself before comparing it to the aggregate: a malformed batch is
        // a caller error no reload can fix, so it must never surface as a retryable conflict.
        for (var index = 1; index < eventList.Count; index++)
        {
            var expectedBatchVersion = firstVersion + index;
            if (eventList[index].Version != expectedBatchVersion)
            {
                throw new ArgumentException(
                    "Events must have sequential versions. " +
                    $"Expected version {expectedBatchVersion}, but found {eventList[index].Version}.",
                    nameof(events));
            }
        }

        if (firstVersion < 1)
        {
            throw new ArgumentException(
                $"Event versions must start at 1, but found {firstVersion}.",
                nameof(events));
        }

        // A version that is already committed is an optimistic-concurrency conflict, not invalid
        // input: the caller prepared the event against state another writer has since superseded.
        if (firstVersion <= aggregate.Version)
        {
            throw await CreateStaleVersionExceptionAsync(eventList, aggregate.Version, cancellationToken);
        }

        var expectedVersion = aggregate.Version + 1;
        if (firstVersion != expectedVersion)
        {
            throw new ArgumentException(
                $"Events must have sequential versions starting from {expectedVersion}. " +
                $"Expected version {expectedVersion}, but found {firstVersion}.",
                nameof(events));
        }

        // 3. Apply events in memory (may throw MongoEventValidationException)
        foreach (var @event in eventList)
        {
            @event.Execute(aggregate);
        }

        // 4. Update version
        var lastVersion = eventList[^1].Version;
        aggregate.Version = lastVersion;

        var shouldCreateCheckpoint = _options.CheckpointsEnabled && lastVersion % _options.CheckpointInterval == 0;
        var checkpoint = shouldCreateCheckpoint
            ? new CheckpointDocument<TAggregate>
            {
                Id = new CheckpointId(aggregateId, lastVersion),
                State = aggregate
            }
            : null;

        await EnsureBulkWriteOptimizationSupportedAsync(cancellationToken);

        // Both append paths insert the same serialized documents, so they persist identical bytes.
        var eventDocuments = eventList.Select(SerializeEvent).ToList();

        // 5. Persist changes inside transaction
        try
        {
            await _mongoHelper.ExecuteInTransaction(
                async (helper, session, ct) =>
                {
                    var eventsCollection = GetEventsCollection();
                    var documents = _options.IntegrityProtectionEnabled
                        ? await SealEventDocumentsAsync(session, eventList, eventDocuments, ct)
                        : eventDocuments;

                    if (_options.BulkWriteOptimizationEnabled)
                    {
                        var models = new List<BulkWriteModel>(documents.Count + (checkpoint is null ? 1 : 2));

                        foreach (var document in documents)
                        {
                            models.Add(new BulkWriteInsertOneModel<BsonDocument>(
                                           eventsCollection.CollectionNamespace,
                                           document));
                        }

                        models.Add(new BulkWriteReplaceOneModel<BsonDocument>(
                                       readModelCollection.CollectionNamespace,
                                       RenderFilter(readModelCollection, Builders<TAggregate>.Filter.Eq(a => a.Id, aggregateId)),
                                       aggregate.ToBsonDocument(),
                                       null,
                                       null,
                                       true));

                        if (checkpoint is not null)
                        {
                            models.Add(new BulkWriteInsertOneModel<BsonDocument>(
                                           GetCheckpointCollection().CollectionNamespace,
                                           checkpoint.ToBsonDocument()));
                        }

                        await helper.Client.BulkWriteAsync(
                            session,
                            models,
                            new ClientBulkWriteOptions
                            {
                                IsOrdered = true
                            },
                            ct);
                    }
                    else
                    {
                        // 5a. Insert events
                        await GetEventDocumentsCollection().InsertManyAsync(session, documents, cancellationToken: ct);

                        // 5b. Upsert read model
                        await readModelCollection.ReplaceOneAsync(
                            session,
                            Builders<TAggregate>.Filter.Eq(a => a.Id, aggregateId),
                            aggregate,
                            new ReplaceOptions
                            {
                                IsUpsert = true
                            },
                            ct);

                        // 5c. Create checkpoint if needed
                        if (checkpoint is not null)
                        {
                            var checkpointCollection = GetCheckpointCollection();
                            await checkpointCollection.InsertOneAsync(session, checkpoint, cancellationToken: ct);
                        }
                    }

                    // 5d. Invoke user callback for additional transactional operations
                    if (onBeforeCommit is not null)
                        await onBeforeCommit(session, aggregate, helper, ct);
                },
                cancellationToken: cancellationToken);

            return aggregate;
        }
        catch (MongoException ex) when (IsDuplicateKeyException(ex))
        {
            var message = GetDuplicateKeyMessage(ex);

            if (message.Contains("index: _id_", StringComparison.Ordinal))
            {
                throw new MongoDuplicateEventException(
                    "An event with the same ID already exists (idempotency conflict).", ex);
            }

            if (message.Contains(IndexNames.AggregateIdWithVersionUnique, StringComparison.Ordinal))
            {
                throw new MongoConcurrencyException(
                    "A concurrency conflict occurred — another process inserted an event for this aggregate version.", ex);
            }

            // Unknown duplicate key error (e.g. user-defined index) — let it propagate
            throw;
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<Event<TAggregate>> GetEventStream(
        Guid aggregateId,
        Int64 fromVersion = 0,
        Int64? toVersion = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var eventsCollection = GetEventsCollection();

        var filterBuilder = Builders<Event<TAggregate>>.Filter;
        var filter = filterBuilder.Eq(e => e.AggregateId, aggregateId) &
                     filterBuilder.Gte(e => e.Version, fromVersion);

        if (toVersion.HasValue)
            filter &= filterBuilder.Lte(e => e.Version, toVersion.Value);

        var cursor = await eventsCollection
                           .Find(filter)
                           .SortBy(e => e.Version)
                           .ToCursorAsync(cancellationToken);

        while (await cursor.MoveNextAsync(cancellationToken))
        {
            foreach (var @event in cursor.Current)
            {
                yield return @event;
            }
        }
    }

    /// <inheritdoc/>
    public async Task<Int64> GetExpectedNextVersionAsync(Guid aggregateId, CancellationToken cancellationToken = default)
    {
        var eventsCollection = GetEventsCollection();

        var lastEvent = await eventsCollection
                              .Find(Builders<Event<TAggregate>>.Filter.Eq(e => e.AggregateId, aggregateId))
                              .SortByDescending(e => e.Version)
                              .Limit(1)
                              .FirstOrDefaultAsync(cancellationToken);

        return (lastEvent?.Version ?? 0) + 1;
    }
}
