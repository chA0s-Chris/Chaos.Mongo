// Copyright (c) 2025-2026 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore;

using Chaos.Mongo.EventStore.Errors;
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

    private IMongoCollection<CheckpointDocument<TAggregate>> GetCheckpointCollection()
        => _mongoHelper.Database.GetCollection<CheckpointDocument<TAggregate>>(_options.CheckpointCollectionName);

    private IMongoCollection<Event<TAggregate>> GetEventsCollection()
        => _mongoHelper.Database.GetCollection<Event<TAggregate>>(_options.EventsCollectionName);

    private IMongoCollection<TAggregate> GetReadModelCollection()
        => _mongoHelper.Database.GetCollection<TAggregate>(_options.ReadModelCollectionName);

    /// <inheritdoc/>
    public async Task<Int64> AppendEventsAsync(
        IEnumerable<Event<TAggregate>> events,
        Func<IClientSessionHandle, IMongoHelper, CancellationToken, Task>? onBeforeCommit = null,
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
                @event.CreatedUtc = now;
        }

        var aggregateId = eventList[0].AggregateId;
        var readModelCollection = GetReadModelCollection();

        // 1. Load or create aggregate (outside transaction for validation)
        var aggregate = await readModelCollection
                              .Find(Builders<TAggregate>.Filter.Eq(a => a.Id, aggregateId))
                              .FirstOrDefaultAsync(cancellationToken);

        aggregate ??= new()
        {
            Id = aggregateId,
            CreatedUtc = now
        };

        // 2. Apply events in memory (may throw MongoEventValidationException)
        foreach (var @event in eventList)
            @event.Execute(aggregate);

        // 3. Update version
        var lastVersion = eventList[^1].Version;
        aggregate.Version = lastVersion;

        // 4. Persist changes inside transaction
        try
        {
            await _mongoHelper.ExecuteInTransaction(
                async (helper, session, ct) =>
                {
                    var eventsCollection = GetEventsCollection();

                    // 4a. Insert events
                    await eventsCollection.InsertManyAsync(session, eventList, cancellationToken: ct);

                    // 4b. Upsert read model
                    await readModelCollection.ReplaceOneAsync(
                        session,
                        Builders<TAggregate>.Filter.Eq(a => a.Id, aggregateId),
                        aggregate,
                        new ReplaceOptions
                        {
                            IsUpsert = true
                        },
                        ct);

                    // 4c. Create checkpoint if needed
                    if (_options.CheckpointsEnabled && lastVersion % _options.CheckpointInterval == 0)
                    {
                        var checkpointCollection = GetCheckpointCollection();
                        var checkpoint = new CheckpointDocument<TAggregate>
                        {
                            Id = new(aggregateId, lastVersion),
                            State = aggregate
                        };
                        await checkpointCollection.InsertOneAsync(session, checkpoint, cancellationToken: ct);
                    }

                    // 4d. Invoke user callback for additional transactional operations
                    if (onBeforeCommit is not null)
                        await onBeforeCommit(session, helper, ct);
                },
                cancellationToken: cancellationToken);

            return lastVersion;
        }
        catch (MongoException ex)
            when (ex is MongoCommandException { Code: 11000 } or
                        MongoWriteException { WriteError.Category: ServerErrorCategory.DuplicateKey } or
                        MongoBulkWriteException { WriteErrors: [{ Category: ServerErrorCategory.DuplicateKey }] })
        {
            var message = ex.Message;

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
    public async IAsyncEnumerable<Event<TAggregate>> GetEventStream(Guid aggregateId,
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
