// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore;

using Chaos.Mongo.EventStore.Errors;
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

    private IMongoCollection<Event<TAggregate>> GetEventsCollection()
        => _mongoHelper.Database.GetCollection<Event<TAggregate>>(_options.EventsCollectionName);

    private IMongoCollection<TAggregate> GetReadModelCollection()
        => _mongoHelper.Database.GetCollection<TAggregate>(_options.ReadModelCollectionName);

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

        // 5. Persist changes inside transaction
        try
        {
            await _mongoHelper.ExecuteInTransaction(
                async (helper, session, ct) =>
                {
                    var eventsCollection = GetEventsCollection();

                    if (_options.BulkWriteOptimizationEnabled)
                    {
                        var models = new List<BulkWriteModel>(eventList.Count + (checkpoint is null ? 1 : 2));

                        foreach (var @event in eventList)
                        {
                            models.Add(new BulkWriteInsertOneModel<BsonDocument>(
                                           eventsCollection.CollectionNamespace,
                                           @event.ToBsonDocument()));
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
                        await eventsCollection.InsertManyAsync(session, eventList, cancellationToken: ct);

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
