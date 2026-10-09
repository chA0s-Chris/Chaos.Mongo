// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore;

using Chaos.Mongo.EventStore.Errors;
using Chaos.Mongo.EventStore.Integrity;
using MongoDB.Bson;
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
    private readonly EventStreamSealer<TAggregate> _sealer;

    public MongoEventStore(IMongoHelper mongoHelper, MongoEventStoreOptions<TAggregate> options)
    {
        ArgumentNullException.ThrowIfNull(mongoHelper);
        ArgumentNullException.ThrowIfNull(options);
        _mongoHelper = mongoHelper;
        _options = options;
        _aggregateTypeName = typeof(TAggregate).Name;
        _sealer = new EventStreamSealer<TAggregate>(mongoHelper, options);
    }

    /// <summary>
    /// Gets or sets a callback invoked between reading the stream head and reading the read model, which
    /// lets tests commit a competing event deterministically at that point.
    /// </summary>
    internal Func<CancellationToken, Task>? AfterStreamHeadRead { get; set; }

    /// <summary>
    /// Applies the events to the aggregate in order and assigns each event the revision after it. A
    /// state-changing event increments the revision and moves the aggregate's version to its position; an
    /// observational event records the revision it observed and changes neither.
    /// </summary>
    private static void ApplyEvents(List<Event<TAggregate>> eventList, TAggregate aggregate)
    {
        foreach (var @event in eventList)
        {
            if (@event is ObservationalEvent<TAggregate>)
            {
                // Without a preceding state-changing event there is no aggregate to observe, and a non-empty
                // stream would have no read model.
                if (aggregate.Revision == 0)
                {
                    throw new MongoEventValidationException(
                        $"The observational event at version {@event.Version} of aggregate '{@event.AggregateId}' " +
                        "requires a preceding state-changing event.");
                }

                @event.Execute(aggregate);
            }
            else
            {
                @event.Execute(aggregate);
                aggregate.Revision++;
                aggregate.Version = @event.Version;
            }

            @event.Revision = aggregate.Revision;
        }
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

    private static void PrepareEvents(List<Event<TAggregate>> eventList, String aggregateTypeName, DateTime now)
    {
        foreach (var @event in eventList)
        {
            @event.AggregateType = aggregateTypeName;

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
    }

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
    /// Checks the batch against itself: one aggregate, sequential positions starting at one or above. A
    /// malformed batch is a caller error no reload can fix, so it must never surface as a retryable conflict.
    /// </summary>
    private static void ValidateBatch(List<Event<TAggregate>> events, Guid aggregateId)
    {
        foreach (var @event in events)
        {
            if (@event.AggregateId != aggregateId)
            {
                throw new ArgumentException(
                    $"All events must target the same aggregate. Expected '{aggregateId}', but found '{@event.AggregateId}'.",
                    nameof(events));
            }
        }

        var firstVersion = events[0].Version;
        for (var index = 1; index < events.Count; index++)
        {
            var expectedBatchVersion = firstVersion + index;
            if (events[index].Version != expectedBatchVersion)
            {
                throw new ArgumentException(
                    "Events must have sequential versions. " +
                    $"Expected version {expectedBatchVersion}, but found {events[index].Version}.",
                    nameof(events));
            }
        }

        if (firstVersion < 1)
        {
            throw new ArgumentException(
                $"Event versions must start at 1, but found {firstVersion}.",
                nameof(events));
        }
    }

    /// <summary>
    /// Creates the exception describing an append whose first event version was already committed.
    /// Resubmitting an event that is already stored is an idempotent retry rather than a conflict,
    /// so the stored event IDs decide which exception the caller sees.
    /// </summary>
    private async Task<MongoEventStoreException> CreateStaleVersionExceptionAsync(
        List<Event<TAggregate>> eventList,
        Int64 headVersion,
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
            $"The stream is at version {headVersion}.");
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

    /// <summary>
    /// Loads the read model the batch executes against. A non-empty stream always has a read model whose
    /// revision matches the stream head; a newer read model means a state change was committed after the
    /// head was read.
    /// </summary>
    private async Task<TAggregate> LoadAggregateAsync(
        Guid aggregateId,
        StreamHead head,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var aggregate = await GetReadModelCollection()
                              .Find(Builders<TAggregate>.Filter.Eq(a => a.Id, aggregateId))
                              .FirstOrDefaultAsync(cancellationToken);

        if (aggregate is null)
        {
            if (head.Version > 0)
            {
                throw new MongoEventStoreException(
                    $"The stream of aggregate '{aggregateId}' is at version {head.Version}, but its read model is missing.");
            }

            return new TAggregate
            {
                Id = aggregateId,
                CreatedUtc = now
            };
        }

        LegacyRevision.Normalize(aggregate);
        if (aggregate.Revision != head.Revision)
        {
            throw new MongoConcurrencyException(
                $"A concurrency conflict occurred — the read model of aggregate '{aggregateId}' is at revision {aggregate.Revision}, " +
                $"but its stream was read at revision {head.Revision}.");
        }

        return aggregate;
    }

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

        if (!predecessor.TryGetValue(EventIntegrityChain.ElementName, out var integrityValue))
        {
            // Safety net: seal the unsealed prefix in this transaction, bounded by one sealing chunk.
            return await _sealer.SealForAppendAsync(session, aggregateId, version, cancellationToken);
        }

        if (!EventIntegrityChain.TryRead(integrityValue, out var integrity))
        {
            throw new MongoEventStoreException(
                $"The integrity data of version {version} of aggregate '{aggregateId}' is malformed, so its integrity chain cannot be continued.");
        }

        return integrity.Hash;
    }

    /// <summary>
    /// Reads the position and normalized revision of the last event in the stream through an indexed
    /// point read on <c>(AggregateId, Version)</c>. Only these two elements are projected, so the head is
    /// read without deserializing the event itself.
    /// </summary>
    private async Task<StreamHead> ReadStreamHeadAsync(Guid aggregateId, CancellationToken cancellationToken)
    {
        var versionElement = EventDocumentFields<TAggregate>.Version;
        var revisionElement = EventDocumentFields<TAggregate>.Revision;

        var lastEvent = await GetEventsCollection()
                              .Find(Builders<Event<TAggregate>>.Filter.Eq(e => e.AggregateId, aggregateId))
                              .SortByDescending(e => e.Version)
                              .Limit(1)
                              .Project(Builders<Event<TAggregate>>.Projection
                                                                  .Include(versionElement)
                                                                  .Include(revisionElement)
                                                                  .Exclude("_id"))
                              .FirstOrDefaultAsync(cancellationToken);

        if (lastEvent is null)
        {
            return new StreamHead(0, 0);
        }

        var version = lastEvent[versionElement].ToInt64();
        var revision = lastEvent.TryGetValue(revisionElement, out var revisionValue) ? revisionValue.ToInt64() : 0;
        return new StreamHead(version, LegacyRevision.Of(revision, version));
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

    /// <summary>
    /// Validates the batch's positions against the stream head. A position that is already committed is an
    /// optimistic-concurrency conflict, not invalid input: the caller prepared the event against state another
    /// writer has since superseded. A position above the head + 1 is a gap in the caller's own numbering.
    /// </summary>
    private async Task ValidatePositionsAsync(
        List<Event<TAggregate>> events,
        StreamHead head,
        CancellationToken cancellationToken)
    {
        var firstVersion = events[0].Version;
        if (firstVersion <= head.Version)
        {
            throw await CreateStaleVersionExceptionAsync(events, head.Version, cancellationToken);
        }

        var expectedVersion = head.Version + 1;
        if (firstVersion != expectedVersion)
        {
            throw new ArgumentException(
                $"Events must have sequential versions starting from {expectedVersion}. " +
                $"Expected version {expectedVersion}, but found {firstVersion}.",
                nameof(events));
        }
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
        PrepareEvents(eventList, _aggregateTypeName, now);

        var aggregateId = eventList[0].AggregateId;
        ValidateBatch(eventList, aggregateId);

        // 1. Read the stream head, then the read model (outside the transaction for validation). Reading
        // the head first guarantees that the read model is never older than the head it is checked against.
        var head = await ReadStreamHeadAsync(aggregateId, cancellationToken);
        await ValidatePositionsAsync(eventList, head, cancellationToken);

        if (AfterStreamHeadRead is not null)
        {
            await AfterStreamHeadRead(cancellationToken);
        }

        var aggregate = await LoadAggregateAsync(aggregateId, head, now, cancellationToken);

        // 2. Apply events in memory (may throw MongoEventValidationException)
        var previousRevision = aggregate.Revision;
        ApplyEvents(eventList, aggregate);
        var stateChanged = aggregate.Revision > previousRevision;

        // 3. A checkpoint is due whenever the batch crosses a multiple of the interval, even when it
        // skips over the exact multiple. Batches without state-changing events never cross one.
        var checkpoint = _options.CheckpointsEnabled &&
                         (aggregate.Revision / _options.CheckpointInterval) > (previousRevision / _options.CheckpointInterval)
            ? new CheckpointDocument<TAggregate>
            {
                Id = new CheckpointId(aggregateId, aggregate.Version),
                Revision = aggregate.Revision,
                State = aggregate
            }
            : null;

        await EnsureBulkWriteOptimizationSupportedAsync(cancellationToken);

        // Serialized documents are only needed to seal events or to feed the client bulk write. Without
        // either, events are inserted typed, which avoids an intermediate document per event. The server
        // stores _id first in every case, so all paths persist identical bytes.
        var eventDocuments = _options.IntegrityProtectionEnabled || _options.BulkWriteOptimizationEnabled
            ? eventList.Select(SerializeEvent).ToList()
            : null;

        // 4. Persist changes inside transaction
        try
        {
            await _mongoHelper.ExecuteInTransaction(
                async (helper, session, ct) =>
                {
                    var eventsCollection = GetEventsCollection();
                    var readModelCollection = GetReadModelCollection();
                    var documents = _options.IntegrityProtectionEnabled && eventDocuments is not null
                        ? await SealEventDocumentsAsync(session, eventList, eventDocuments, ct)
                        : eventDocuments;

                    // Documents always exist when the bulk write is enabled; the null check only proves it.
                    if (_options.BulkWriteOptimizationEnabled && documents is not null)
                    {
                        var models = new List<BulkWriteModel>(documents.Count + (stateChanged ? 1 : 0) + (checkpoint is null ? 0 : 1));

                        foreach (var document in documents)
                        {
                            models.Add(new BulkWriteInsertOneModel<BsonDocument>(
                                           eventsCollection.CollectionNamespace,
                                           document));
                        }

                        if (stateChanged)
                        {
                            models.Add(new BulkWriteReplaceOneModel<BsonDocument>(
                                           readModelCollection.CollectionNamespace,
                                           RenderFilter(readModelCollection, Builders<TAggregate>.Filter.Eq(a => a.Id, aggregateId)),
                                           aggregate.ToBsonDocument(),
                                           null,
                                           null,
                                           true));
                        }

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
                        // 4a. Insert events
                        if (documents is null)
                        {
                            await eventsCollection.InsertManyAsync(session, eventList, cancellationToken: ct);
                        }
                        else
                        {
                            await GetEventDocumentsCollection().InsertManyAsync(session, documents, cancellationToken: ct);
                        }

                        // 4b. Upsert read model; observational events leave it untouched
                        if (stateChanged)
                        {
                            await readModelCollection.ReplaceOneAsync(
                                session,
                                Builders<TAggregate>.Filter.Eq(a => a.Id, aggregateId),
                                aggregate,
                                new ReplaceOptions
                                {
                                    IsUpsert = true
                                },
                                ct);
                        }

                        // 4c. Create checkpoint if needed
                        if (checkpoint is not null)
                        {
                            var checkpointCollection = GetCheckpointCollection();
                            await checkpointCollection.InsertOneAsync(session, checkpoint, cancellationToken: ct);
                        }
                    }

                    // 4d. Invoke user callback for additional transactional operations
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
                LegacyRevision.Normalize(@event);
                yield return @event;
            }
        }
    }

    /// <inheritdoc/>
    public async Task<Int64> GetExpectedNextVersionAsync(Guid aggregateId, CancellationToken cancellationToken = default)
    {
        var head = await ReadStreamHeadAsync(aggregateId, cancellationToken);
        return head.Version + 1;
    }

    /// <summary>
    /// The position and revision of the last event in a stream; both are zero for an empty stream.
    /// </summary>
    private readonly record struct StreamHead(Int64 Version, Int64 Revision);
}
