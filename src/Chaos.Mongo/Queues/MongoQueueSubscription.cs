// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Queues;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using System.Diagnostics;

/// <summary>
/// Index option names that the legacy, unnamed four-key queue processing index never requested. Their presence
/// on an index that otherwise matches the legacy key specification means it is a different, custom-configured
/// index a consumer created independently (for example a partial, unique, or sparse index sharing the same
/// keys), not the one the library created, so it must survive. Kept outside
/// <see cref="MongoQueueSubscription{TPayload}"/>, which is generic, so this does not become a separate static
/// field per closed payload type.
/// </summary>
file static class LegacyProcessingIndexOptions
{
    public static readonly String[] UnexpectedNames =
    [
        "unique", "prepareUnique", "sparse", "partialFilterExpression", "hidden",
        "expireAfterSeconds", "textIndexVersion", "weights", "2dsphereIndexVersion", "bits", "min", "max"
    ];
}

/// <summary>
/// Manages a MongoDB-based queue subscription that monitors and processes queue items.
/// </summary>
/// <typeparam name="TPayload">The type of payload stored in queue items.</typeparam>
/// <remarks>
/// This subscription uses a dual approach for queue processing:
/// <list type="bullet">
///     <item>MongoDB change streams to detect new items in real-time</item>
///     <item>Polling mechanism to process existing items and handle missed events</item>
/// </list>
/// Items are locked during processing to ensure single-consumer semantics.
/// </remarks>
internal class MongoQueueSubscription<TPayload> : IMongoQueueSubscription<TPayload>
    where TPayload : class, new()
{
    private const String ClosedItemTtlIndexName = "IX_Queue_ClosedUtc_TTL";
    private const String ProcessingIndexName = "IX_Queue_Processing";
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly ILogger _logger;
    private readonly IMongoHelper _mongoHelper;
    private readonly IMongoQueuePayloadHandlerFactory _payloadHandlerFactory;
    private readonly IMongoQueuePayloadPrioritizer _payloadPrioritizer;
    private readonly MongoQueueDefinition _queueDefinition;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly SemaphoreSlim _signalSemaphore = new(1, Int32.MaxValue);
    private readonly TimeProvider _timeProvider;
    private Boolean _isActive;
    private Boolean _isDisposed;
    private Task _monitorTask = Task.CompletedTask;
    private Task _processingTask = Task.CompletedTask;

    /// <summary>
    /// Initializes a new instance of the <see cref="MongoQueueSubscription{TPayload}"/> class.
    /// </summary>
    /// <param name="queueDefinition">The queue configuration.</param>
    /// <param name="mongoHelper">The MongoDB helper for database operations.</param>
    /// <param name="payloadHandlerFactory">The factory for creating payload handlers.</param>
    /// <param name="payloadPrioritizer">The prioritizer for sorting queue items.</param>
    /// <param name="serviceScopeFactory">The service scope factory for resolving a payload handler per queue item.</param>
    /// <param name="timeProvider">The time provider for timestamp operations.</param>
    /// <param name="logger">The logger for diagnostic messages.</param>
    /// <exception cref="ArgumentNullException">Thrown when any parameter is null.</exception>
    public MongoQueueSubscription(MongoQueueDefinition queueDefinition,
                                  IMongoHelper mongoHelper,
                                  IMongoQueuePayloadHandlerFactory payloadHandlerFactory,
                                  IMongoQueuePayloadPrioritizer payloadPrioritizer,
                                  IServiceScopeFactory serviceScopeFactory,
                                  TimeProvider timeProvider,
                                  ILogger<MongoQueueSubscription<TPayload>> logger)
    {
        ArgumentNullException.ThrowIfNull(queueDefinition);
        ArgumentNullException.ThrowIfNull(mongoHelper);
        ArgumentNullException.ThrowIfNull(payloadHandlerFactory);
        ArgumentNullException.ThrowIfNull(payloadPrioritizer);
        ArgumentNullException.ThrowIfNull(serviceScopeFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _queueDefinition = queueDefinition;
        _mongoHelper = mongoHelper;
        _payloadHandlerFactory = payloadHandlerFactory;
        _payloadPrioritizer = payloadPrioritizer;
        _serviceScopeFactory = serviceScopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Boolean IsActive => _isActive && !_isDisposed;

    /// <summary>
    /// Builds the acquire/claim aggregation-pipeline update that atomically decides, from the document's own
    /// pre-update <see cref="MongoQueueItem.AttemptCount"/> compared against <see cref="MongoQueueDefinition.MaxRetries"/>,
    /// whether this acquisition is <b>granted</b> a new attempt or the item transitions directly to
    /// <b>terminal</b> without granting a lock. This is the only place <see cref="MongoQueueItem.AttemptCount"/>
    /// is incremented, and it runs on every acquisition regardless of why a previous attempt stopped (a resolved
    /// failure, a crash, or a lease-expiry reclaim), which is what makes every acquisition count as a delivery
    /// attempt. The field paths below rely on <see cref="MongoQueueItem"/>'s class map pinning each element name
    /// to its member name (see <see cref="MongoSerializationSetup"/>).
    /// </summary>
    private static UpdateDefinition<MongoQueueItem<TPayload>> CreateAcquireUpdate(DateTime acquiredLockUtc, Int32? maxRetries)
    {
        BsonValue existingAttemptCount = new BsonDocument("$ifNull", new BsonArray
        {
            $"${nameof(MongoQueueItem.AttemptCount)}",
            0L
        });
        BsonValue grant = maxRetries.HasValue
            ? new BsonDocument("$lte", new BsonArray
            {
                existingAttemptCount,
                maxRetries.Value
            })
            : BsonBoolean.True;
        BsonValue nowLiteral = new BsonDateTime(acquiredLockUtc);

        var setStage = new BsonDocument("$set", new BsonDocument
        {
            {
                nameof(MongoQueueItem.AttemptCount), new BsonDocument("$cond", new BsonArray
                {
                    grant,
                    new BsonDocument("$add", new BsonArray
                    {
                        existingAttemptCount,
                        1L
                    }),
                    $"${nameof(MongoQueueItem.AttemptCount)}"
                })
            },
            {
                nameof(MongoQueueItem.IsClosed), new BsonDocument("$cond", new BsonArray
                {
                    grant,
                    $"${nameof(MongoQueueItem.IsClosed)}",
                    true
                })
            },
            {
                nameof(MongoQueueItem.IsTerminal), new BsonDocument("$cond", new BsonArray
                {
                    grant,
                    $"${nameof(MongoQueueItem.IsTerminal)}",
                    true
                })
            },
            {
                nameof(MongoQueueItem.ClosedUtc), new BsonDocument("$cond", new BsonArray
                {
                    grant,
                    "$$REMOVE",
                    nowLiteral
                })
            },
            {
                nameof(MongoQueueItem.IsLocked), new BsonDocument("$cond", new BsonArray
                {
                    grant,
                    true,
                    false
                })
            },
            {
                nameof(MongoQueueItem.LockedUtc), new BsonDocument("$cond", new BsonArray
                {
                    grant,
                    nowLiteral,
                    "$$REMOVE"
                })
            }
        });

        var pipeline = new EmptyPipelineDefinition<MongoQueueItem<TPayload>>()
            .AppendStage<MongoQueueItem<TPayload>, MongoQueueItem<TPayload>, MongoQueueItem<TPayload>>(setStage);

        return Builders<MongoQueueItem<TPayload>>.Update.Pipeline(pipeline);
    }

    /// <summary>
    /// Builds the closed-item TTL index's partial filter from the element names the registered class map of the
    /// non-generic <see cref="MongoQueueItem"/> base type actually uses, instead of raw <c>nameof(...)</c> string
    /// literals, so the filter keeps matching even if a member is ever pinned to a different element name.
    /// <see cref="MongoQueueItem.IsClosed"/>, <see cref="MongoQueueItem.IsTerminal"/> and
    /// <see cref="MongoQueueItem.ClosedUtc"/> are declared on <see cref="MongoQueueItem"/>, not on
    /// <see cref="MongoQueueItem{TPayload}"/>, and <see cref="BsonClassMap.GetMemberMap"/> only searches members
    /// declared on the class whose map it is called on, so the lookup is resolved through the base class's own
    /// registered map.
    /// </summary>
    private static BsonDocumentFilterDefinition<MongoQueueItem<TPayload>> CreateClosedItemTtlPartialFilter()
    {
        var baseClassMap = BsonClassMap.LookupClassMap(typeof(MongoQueueItem));
        var isClosedElementName = baseClassMap.GetMemberMap(nameof(MongoQueueItem.IsClosed)).ElementName;
        var isTerminalElementName = baseClassMap.GetMemberMap(nameof(MongoQueueItem.IsTerminal)).ElementName;
        var closedUtcElementName = baseClassMap.GetMemberMap(nameof(MongoQueueItem.ClosedUtc)).ElementName;

        // IsTerminal keeps the $in: [false, null] shape: it is a supported partialFilterExpression operator, and
        // MongoDB's equality-to-null semantics already make it match false, an explicit BSON null, and a
        // genuinely missing field (documents written before IsTerminal existed) in one expression. An $or of
        // $eq/$not($exists) would need $not/$exists:false, which is not a supported partial-filter operator.
        return new BsonDocumentFilterDefinition<MongoQueueItem<TPayload>>(new BsonDocument
        {
            { isClosedElementName, true },
            { isTerminalElementName, new BsonDocument("$in", new BsonArray([false, BsonNull.Value])) },
            {
                closedUtcElementName, new BsonDocument
                {
                    { "$exists", true },
                    { "$type", (Int32)BsonType.DateTime }
                }
            }
        });
    }

    private static FilterDefinition<MongoQueueItem<TPayload>> CreateLockOwnershipFilter(ObjectId queueItemId, DateTime acquiredLockUtc)
    {
        var filterBuilder = Builders<MongoQueueItem<TPayload>>.Filter;
        return filterBuilder.Eq(x => x.Id, queueItemId) &
               filterBuilder.Eq(x => x.IsClosed, false) &
               filterBuilder.Eq(x => x.IsLocked, true) &
               filterBuilder.Eq(x => x.LockedUtc, acquiredLockUtc);
    }

    /// <summary>
    /// Drops the processing index this library created before it had an explicit name and included the trailing
    /// <see cref="MongoQueueItem.Id"/> sort key: the exact four-key IsClosed/IsLocked/LockedUtc/IsTerminal index,
    /// under the exact name MongoDB generates for that key specification, with none of the extra options
    /// (partial filter, uniqueness, sparsity, collation, ...) that definition never requested. Its key
    /// specification differs from the current, five-key <see cref="ProcessingIndexName"/> definition, so
    /// <see cref="MongoIndexManagerExtensions"/>'s key-specification-based conflict resolution never sees it as
    /// conflicting and leaves it in place untouched. A consumer-created index that happens to share the same four
    /// keys under a custom name, or with extra options this library's definition never had, is left alone.
    /// Tolerates the index already being absent, whether because an earlier run already dropped it or because the
    /// collection never had the four-key shape to begin with.
    /// </summary>
    /// <remarks>
    /// Neither the legacy four-key definition nor the current <see cref="ProcessingIndexName"/> definition ever
    /// specifies an explicit <c>collation</c>, so both inherit whatever default collation the collection itself
    /// has. MongoDB's <c>listIndexes</c> reports that inherited collation on every index regardless, so requiring
    /// the legacy index to have none at all would wrongly leave it behind on any collection with a non-simple
    /// default collation. Comparing against the collation the just-created <see cref="ProcessingIndexName"/> index
    /// itself inherited (rather than requiring either index to have none) still rejects a consumer-created index
    /// that explicitly requested a genuinely different collation.
    /// </remarks>
    private static async Task DropLegacyProcessingIndexAsync(IMongoCollection<MongoQueueItem<TPayload>> collection, CancellationToken cancellationToken)
    {
        var legacyKeys = Builders<MongoQueueItem<TPayload>>.IndexKeys
                                                           .Ascending(x => x.IsClosed)
                                                           .Ascending(x => x.IsLocked)
                                                           .Ascending(x => x.LockedUtc)
                                                           .Ascending(x => x.IsTerminal)
                                                           .Render(new RenderArgs<MongoQueueItem<TPayload>>(
                                                                       collection.DocumentSerializer,
                                                                       collection.Settings.SerializerRegistry));
        var legacyGeneratedName = String.Join("_", legacyKeys.Select(element => $"{element.Name}_{element.Value}"));

        using var cursor = await collection.Indexes.ListAsync(cancellationToken);
        var existingIndexes = await cursor.ToListAsync(cancellationToken);
        var expectedCollation = existingIndexes.FirstOrDefault(index => index["name"].AsString == ProcessingIndexName)
                                               ?.GetValue("collation", null)
                                               ?.AsBsonDocument;

        foreach (var index in existingIndexes)
        {
            if (index["name"].AsString == legacyGeneratedName &&
                index["key"].AsBsonDocument.Equals(legacyKeys) &&
                Equals(index.GetValue("collation", null)?.AsBsonDocument, expectedCollation) &&
                !LegacyProcessingIndexOptions.UnexpectedNames.Any(index.Contains))
            {
                await collection.Indexes.DropOneIfExistsAsync(index["name"].AsString, cancellationToken);
            }
        }
    }

    private FilterDefinition<MongoQueueItem<TPayload>> CreateAvailableQueueItemFilter()
    {
        var filterBuilder = Builders<MongoQueueItem<TPayload>>.Filter;
        var lockExpiryUtc = _timeProvider.GetUtcNow().UtcDateTime - _queueDefinition.LockLeaseTime;

        return filterBuilder.Eq(x => x.IsClosed, false) &
               filterBuilder.Ne(x => x.IsTerminal, true) &
               (filterBuilder.Eq(x => x.IsLocked, false) |
                (filterBuilder.Eq(x => x.IsLocked, true) &
                 (filterBuilder.Eq(x => x.LockedUtc, null) |
                  filterBuilder.Lt(x => x.LockedUtc, lockExpiryUtc))));
    }

    private FilterDefinition<MongoQueueItem<TPayload>> CreateAvailableQueueItemFilter(ObjectId queueItemId)
        => Builders<MongoQueueItem<TPayload>>.Filter.Eq(x => x.Id, queueItemId) &
           CreateAvailableQueueItemFilter();

    /// <summary>
    /// Ensures that required indexes exist on the queue collection for efficient querying.
    /// </summary>
    /// <param name="collection">The MongoDB collection to create indexes on.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task EnsureIndexesAsync(IMongoCollection<MongoQueueItem<TPayload>> collection, CancellationToken cancellationToken)
    {
        await collection.Indexes
                        .CreateOneOrUpdateAsync(
                            new CreateIndexModel<MongoQueueItem<TPayload>>(
                                Builders<MongoQueueItem<TPayload>>.IndexKeys
                                                                  .Ascending(x => x.IsClosed)
                                                                  .Ascending(x => x.IsLocked)
                                                                  .Ascending(x => x.LockedUtc)
                                                                  .Ascending(x => x.IsTerminal)
                                                                  .Ascending(x => x.Id),
                                new CreateIndexOptions<MongoQueueItem<TPayload>>
                                {
                                    Name = ProcessingIndexName
                                }),
                            cancellationToken: cancellationToken);

        // The index above gained the trailing Id key (and its explicit name) after this library had already
        // created it, unnamed, with only the first four keys. CreateOneOrUpdateAsync resolves a conflicting
        // index by key specification, so a key specification it has never seen before raises no conflict and
        // would otherwise leave the old generated-name index behind forever. Find and drop it explicitly.
        await DropLegacyProcessingIndexAsync(collection, cancellationToken);

        _logger.LogDebug("Ensured queue processing index for collection {CollectionName}", _queueDefinition.CollectionName);

        if (_queueDefinition.RetentionPeriod.HasValue)
        {
            var ttlIndex = new CreateIndexModel<MongoQueueItem<TPayload>>(
                Builders<MongoQueueItem<TPayload>>.IndexKeys.Ascending(x => x.ClosedUtc),
                new CreateIndexOptions<MongoQueueItem<TPayload>>
                {
                    Name = ClosedItemTtlIndexName,
                    ExpireAfter = _queueDefinition.RetentionPeriod.Value,
                    PartialFilterExpression = CreateClosedItemTtlPartialFilter()
                });

            await collection.Indexes.CreateOneOrUpdateAsync(ttlIndex, cancellationToken: cancellationToken);
            _logger.LogInformation("Ensured TTL cleanup index {IndexName} for collection {CollectionName} with retention {RetentionPeriod}",
                                   ClosedItemTtlIndexName,
                                   _queueDefinition.CollectionName,
                                   _queueDefinition.RetentionPeriod.Value);
        }
        else
        {
            await collection.Indexes.DropOneIfExistsAsync(ClosedItemTtlIndexName, cancellationToken);
            _logger.LogInformation("Successful queue items for collection {CollectionName} will be deleted immediately after processing",
                                   _queueDefinition.CollectionName);
        }
    }

    /// <summary>
    /// Handles a resolved failure (a handler exception, a DI resolution failure, or a poison-payload secondary
    /// read failure) for the attempt the caller already knows was just granted. Unlike acquisition, a resolved
    /// failure never needs to increment <see cref="MongoQueueItem.AttemptCount"/> itself — that already happened
    /// atomically when this attempt was granted — so this method only decides, from the already-known
    /// <paramref name="attemptCount"/>, whether to retry (no database write; the item simply stays available
    /// once its lock lease expires) or to terminalize immediately via an ownership-checked write.
    /// </summary>
    private async Task HandleFailedQueueItemAsync(IMongoCollection<MongoQueueItem<TPayload>> collection,
                                                  ObjectId queueItemId,
                                                  DateTime acquiredLockUtc,
                                                  Int64 attemptCount,
                                                  Exception exception,
                                                  CancellationToken cancellationToken)
    {
        if (!_queueDefinition.MaxRetries.HasValue || attemptCount <= _queueDefinition.MaxRetries.Value)
        {
            MongoQueueDiagnostics.RecordProcessingFailed(_queueDefinition, "retry");
            _logger.LogError(exception,
                             "Processing queue item {QueueItemId} with payload {PayloadType} failed on attempt {AttemptCount}. The item will be retried after the lock lease expires",
                             queueItemId,
                             typeof(TPayload).FullName,
                             attemptCount);
            return;
        }

        var terminalUpdateSucceeded = (await collection.UpdateOneAsync(
            CreateLockOwnershipFilter(queueItemId, acquiredLockUtc),
            Builders<MongoQueueItem<TPayload>>.Update
                                              .Set(x => x.IsClosed, true)
                                              .Set(x => x.IsTerminal, true)
                                              .Set(x => x.ClosedUtc, _timeProvider.GetUtcNow().UtcDateTime)
                                              .Set(x => x.IsLocked, false)
                                              .Unset(x => x.LockedUtc),
            cancellationToken: cancellationToken)).ModifiedCount > 0;

        if (!terminalUpdateSucceeded)
        {
            _logger.LogWarning("Skipping terminal transition for queue item {QueueItemId} with payload {PayloadType} because lock ownership changed",
                               queueItemId,
                               typeof(TPayload).FullName);
            return;
        }

        MongoQueueDiagnostics.RecordProcessingFailed(_queueDefinition, "terminal");
        _logger.LogError(exception,
                         "Marking queue item {QueueItemId} with payload {PayloadType} as terminal after {AttemptCount} failed attempts (max retries: {MaxRetries})",
                         queueItemId,
                         typeof(TPayload).FullName,
                         attemptCount,
                         _queueDefinition.MaxRetries!.Value);
    }

    /// <summary>
    /// Monitors a MongoDB change stream for insert operations and signals the processing task when new items arrive.
    /// </summary>
    /// <param name="collection">The MongoDB collection to monitor for insert operations.</param>
    /// <param name="cancellationToken">The cancellation token to stop monitoring.</param>
    /// <returns>A task that completes when monitoring stops.</returns>
    private async Task MonitorChangeStreamAsync(IMongoCollection<MongoQueueItem<TPayload>> collection, CancellationToken cancellationToken)
    {
        var options = new ChangeStreamOptions
        {
            FullDocument = ChangeStreamFullDocumentOption.Default
        };
        var pipeline = new EmptyPipelineDefinition<ChangeStreamDocument<MongoQueueItem<TPayload>>>()
            .Match(x => x.OperationType == ChangeStreamOperationType.Insert);

        _logger.LogInformation("Watching change stream for collection {CollectionName}", _queueDefinition.CollectionName);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var cursor = await collection.WatchAsync(pipeline, options, cancellationToken);
                while (await cursor.MoveNextAsync(cancellationToken))
                {
                    var batch = cursor.Current;
                    if (batch.Any())
                    {
                        // signal only once per batch
                        _signalSemaphore.Release();
                    }
                }
            }
            catch (Exception e)
            {
                if (e is OperationCanceledException)
                    break;

                _logger.LogError(e, "Watching change stream for collection {CollectionName} failed", _queueDefinition.CollectionName);
                await Task.Delay(300, cancellationToken);
            }
        }

        _logger.LogInformation("Stopped watching change stream for collection {CollectionName}", _queueDefinition.CollectionName);
    }

    /// <summary>
    /// Processes a queue item with the given identifier.
    /// </summary>
    /// <param name="collection">The MongoDB collection to process queue items from.</param>
    /// <param name="queueItemId">The identifier of the queue item to process.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <remarks>
    /// The queue item is locked during processing and closed after processing has completed.
    /// If the queue item is already closed or still within its lease, it is skipped.
    /// </remarks>
    private async Task ProcessQueueItemAsync(IMongoCollection<MongoQueueItem<TPayload>> collection,
                                             ObjectId queueItemId,
                                             CancellationToken cancellationToken)
    {
        _logger.LogDebug("Processing queue item {QueueItemId} with payload {PayloadType}", queueItemId, typeof(TPayload).FullName);

        try
        {
            var acquiredLockUtc = _timeProvider.GetUtcNow().UtcDateTime;
            var lockExpiryUtc = acquiredLockUtc - _queueDefinition.LockLeaseTime;
            var processingStartedTimestamp = Stopwatch.GetTimestamp();

            // The acquire/claim itself decides, from the document's own pre-update AttemptCount, whether to
            // grant this acquisition a new attempt or to terminalize the item directly. The projection reads
            // back only the non-generic MongoQueueItem base type, so a payload that can never deserialize into
            // TPayload cannot fail this call: the outcome is decided and written without ever reading Payload.
            var projectedResult = await collection.FindOneAndUpdateAsync(
                CreateAvailableQueueItemFilter(queueItemId),
                CreateAcquireUpdate(acquiredLockUtc, _queueDefinition.MaxRetries),
                new FindOneAndUpdateOptions<MongoQueueItem<TPayload>, MongoQueueItem>
                {
                    ReturnDocument = ReturnDocument.Before,
                    Projection = Builders<MongoQueueItem<TPayload>>.Projection.As<MongoQueueItem>()
                },
                cancellationToken);

            if (projectedResult is null)
            {
                // skip item
                return;
            }

            // The filter above already requires a non-terminal, available document, so this re-derives exactly
            // the predicate the acquire pipeline itself evaluated from the same pre-update AttemptCount.
            var priorAttemptCount = projectedResult.AttemptCount;
            var grant = !_queueDefinition.MaxRetries.HasValue || priorAttemptCount <= _queueDefinition.MaxRetries.Value;
            var queueAge = acquiredLockUtc - projectedResult.CreatedUtc;

            if (!grant)
            {
                // This acquisition's own terminal branch already fired atomically (lock cleared, IsTerminal/
                // IsClosed set): no DI scope, no handler invocation, no payload read of any kind.
                MongoQueueDiagnostics.RecordProcessingFailed(_queueDefinition, "terminal");
                _logger.LogError(
                    "Marking queue item {QueueItemId} with payload {PayloadType} as terminal after {AttemptCount} attempts (max retries: {MaxRetries}) without granting a further attempt",
                    queueItemId,
                    typeof(TPayload).FullName,
                    priorAttemptCount,
                    _queueDefinition.MaxRetries);
                return;
            }

            var attemptCount = priorAttemptCount + 1;

            _logger.LogDebug(
                "Acquired queue item lock {QueueItemId} with payload {PayloadType} (recovered: {RecoveredLock}, attempt: {AttemptCount}, queue age: {QueueAgeMs} ms)",
                queueItemId,
                typeof(TPayload).FullName,
                projectedResult.IsLocked,
                attemptCount,
                queueAge.TotalMilliseconds);

            if (projectedResult.IsLocked)
            {
                var recoveredLockAge = acquiredLockUtc - (projectedResult.LockedUtc ?? projectedResult.CreatedUtc);
                MongoQueueDiagnostics.RecordLockRecovered(_queueDefinition, recoveredLockAge);
                _logger.LogWarning(
                    "Recovering queue item lock {QueueItemId} (previous LockedUtc: {PreviousLockedUtc}, expiry threshold: {ExpiryThreshold}) with payload {PayloadType}",
                    queueItemId,
                    projectedResult.LockedUtc?.ToString("O") ?? "null",
                    lockExpiryUtc.ToString("O"),
                    typeof(TPayload).FullName);
            }

            MongoQueueItem<TPayload> queueItem;
            try
            {
                // A second, separate, typed read: the only place Payload is deserialized, so a permanently
                // undeserializable payload fails here (handled exactly like a handler exception) instead of
                // ever failing the acquire itself. It always reads from the primary, which just applied the acquire:
                // under a non-primary read preference a lagging secondary may not have the item yet, and that miss
                // would be counted as a failed attempt.
                queueItem = await collection.WithReadPreference(ReadPreference.Primary)
                                            .Find(Builders<MongoQueueItem<TPayload>>.Filter.Eq(x => x.Id, queueItemId))
                                            .FirstAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await ReleaseLockForShutdownAsync(collection, queueItemId, acquiredLockUtc);
                return;
            }
            catch (Exception e)
            {
                await HandleFailedQueueItemAsync(collection, queueItemId, acquiredLockUtc, attemptCount, e, cancellationToken);
                return;
            }

            await using (var scope = _serviceScopeFactory.CreateAsyncScope())
            {
                IMongoQueuePayloadHandler<TPayload> payloadHandler;
                try
                {
                    payloadHandler = _payloadHandlerFactory.CreateHandler<TPayload>(scope.ServiceProvider);
                }
                catch (Exception e)
                {
                    await HandleFailedQueueItemAsync(collection, queueItemId, acquiredLockUtc, attemptCount, e, cancellationToken);
                    return;
                }

                try
                {
                    _logger.LogDebug("Handling payload {PayloadType} with handler {PayloadHandlerType}", typeof(TPayload).FullName,
                                     payloadHandler.GetType().FullName);
                    await payloadHandler.HandlePayloadAsync(queueItem.Payload, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    await ReleaseLockForShutdownAsync(collection, queueItemId, acquiredLockUtc);
                    return;
                }
                catch (Exception e)
                {
                    await HandleFailedQueueItemAsync(collection, queueItemId, acquiredLockUtc, attemptCount, e, cancellationToken);
                    return;
                }
            }

            var completionFilter = CreateLockOwnershipFilter(queueItemId, acquiredLockUtc);
            Boolean completionSucceeded;
            try
            {
                using var completionGracePeriodCts = new CancellationTokenSource(MongoQueueSubscriptionDefaults.CompletionGraceTimeout);
                completionSucceeded = _queueDefinition.RetentionPeriod.HasValue
                    ? (await collection.UpdateOneAsync(
                        completionFilter,
                        Builders<MongoQueueItem<TPayload>>.Update
                                                          .Set(x => x.IsClosed, true)
                                                          .Set(x => x.ClosedUtc, _timeProvider.GetUtcNow().UtcDateTime)
                                                          .Set(x => x.IsLocked, false)
                                                          .Unset(x => x.LockedUtc),
                        cancellationToken: completionGracePeriodCts.Token)).ModifiedCount > 0
                    : (await collection.DeleteOneAsync(completionFilter, completionGracePeriodCts.Token)).DeletedCount > 0;
            }
            catch (Exception e)
            {
                _logger.LogWarning(e,
                                   "Completing queue item {QueueItemId} with payload {PayloadType} failed; it will be reprocessed after the lock lease expires",
                                   queueItemId,
                                   typeof(TPayload).FullName);
                return;
            }

            if (!completionSucceeded)
            {
                _logger.LogWarning("Skipping completion for queue item {QueueItemId} with payload {PayloadType} because lock ownership changed",
                                   queueItemId,
                                   typeof(TPayload).FullName);
                return;
            }

            var cleanupMode = _queueDefinition.RetentionPeriod.HasValue ? "ttl-retention" : "immediate-delete";
            var processingDuration = Stopwatch.GetElapsedTime(processingStartedTimestamp);
            MongoQueueDiagnostics.RecordProcessingSucceeded(_queueDefinition, processingDuration, queueAge, cleanupMode);
            _logger.LogInformation(
                "Processed queue item {QueueItemId} with payload {PayloadType} in {ProcessingDurationMs} ms (attempt: {AttemptCount}, queue age: {QueueAgeMs} ms, cleanup mode: {CleanupMode})",
                queueItemId,
                typeof(TPayload).FullName,
                processingDuration.TotalMilliseconds,
                attemptCount,
                queueAge.TotalMilliseconds,
                cleanupMode);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            _logger.LogError(e, "Processing queue item {QueueItemId} with payload {PayloadType} failed", queueItemId, typeof(TPayload).FullName);
        }
    }

    /// <summary>
    /// Continuously processes queue items by polling for unlocked, unclosed items and invoking their handlers.
    /// </summary>
    /// <param name="collection">The MongoDB collection to process queue items from.</param>
    /// <param name="cancellationToken">The cancellation token to stop processing.</param>
    /// <returns>A task that completes when processing stops.</returns>
    /// <remarks>
    /// This method waits for signals from the change stream monitor or self-signals when more items exist.
    /// Items are locked during processing using optimistic concurrency with lease expiry.
    /// Failed operations are retried after a delay.
    /// </remarks>
    private async Task ProcessQueueItemsAsync(IMongoCollection<MongoQueueItem<TPayload>> collection, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Processing items with payload {PayloadType} for queue {CollectionName}",
                               _queueDefinition.PayloadType.FullName,
                               _queueDefinition.CollectionName);

        var sortDefinition = _payloadPrioritizer.CreateSortDefinition<TPayload>();
        var leaseRecoveryWakeInterval = _queueDefinition.LockLeaseTime < MongoQueueSubscriptionDefaults.MaxLeaseRecoveryWakeInterval
            ? _queueDefinition.LockLeaseTime
            : MongoQueueSubscriptionDefaults.MaxLeaseRecoveryWakeInterval;
        var countOptions = new CountOptions
        {
            Limit = 1
        };

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _ = await _signalSemaphore.WaitAsync(leaseRecoveryWakeInterval, cancellationToken);

                var queueItemIds = await collection.Find(CreateAvailableQueueItemFilter())
                                                   .Sort(sortDefinition)
                                                   .Project(x => x.Id)
                                                   .Limit(_queueDefinition.BatchSize)
                                                   .ToListAsync(cancellationToken);

                if (queueItemIds.Count == 0)
                {
                    // Brief delay to avoid busy-waiting, then re-signal to check again
                    // This prevents deadlock when messages are published before change stream starts
                    await Task.Delay(100, cancellationToken);
                    _signalSemaphore.Release();
                    continue;
                }

                foreach (var queueItemId in queueItemIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await ProcessQueueItemAsync(collection, queueItemId, cancellationToken);
                }

                // check if there are unprocessed items left so we don't wait
                var count = await collection.CountDocumentsAsync(CreateAvailableQueueItemFilter(), countOptions, cancellationToken);
                if (count > 0)
                {
                    _signalSemaphore.Release();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                _logger.LogError(e,
                                 "Processing items with payload {PayloadType} of queue {CollectionName} failed",
                                 _queueDefinition.PayloadType.FullName,
                                 _queueDefinition.CollectionName);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }

        _logger.LogInformation("Stopped processing items with payload {PayloadType} for queue {CollectionName}",
                               _queueDefinition.PayloadType.FullName,
                               _queueDefinition.CollectionName);
    }

    /// <summary>
    /// Releases the lock on a queue item whose handler was cancelled by subscription shutdown, so the item
    /// becomes available again immediately instead of waiting for the lock lease to expire.
    /// </summary>
    /// <param name="collection">The MongoDB collection containing the queue item.</param>
    /// <param name="queueItemId">The identifier of the queue item to release.</param>
    /// <param name="acquiredLockUtc">The timestamp at which the lock being released was acquired.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task ReleaseLockForShutdownAsync(IMongoCollection<MongoQueueItem<TPayload>> collection, ObjectId queueItemId, DateTime acquiredLockUtc)
    {
        try
        {
            using var gracePeriodCts = new CancellationTokenSource(MongoQueueSubscriptionDefaults.CompletionGraceTimeout);
            var releaseResult = await collection.UpdateOneAsync(
                CreateLockOwnershipFilter(queueItemId, acquiredLockUtc),
                Builders<MongoQueueItem<TPayload>>.Update
                                                  .Set(x => x.IsLocked, false)
                                                  .Unset(x => x.LockedUtc),
                cancellationToken: gracePeriodCts.Token);

            if (releaseResult.ModifiedCount == 0)
            {
                _logger.LogWarning(
                    "Skipping lock release for queue item {QueueItemId} with payload {PayloadType} after shutdown-caused cancellation because lock ownership changed",
                    queueItemId,
                    typeof(TPayload).FullName);
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e,
                               "Releasing the lock for queue item {QueueItemId} with payload {PayloadType} after shutdown-caused cancellation failed; "
                               + "it will be recovered after the lock lease expires",
                               queueItemId,
                               typeof(TPayload).FullName);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        await _cancellationTokenSource.CancelAsync();
        _cancellationTokenSource.Dispose();
        _signalSemaphore.Dispose();
    }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed)
        {
            throw new InvalidOperationException("Subscription is disposed.");
        }

        if (_isActive)
        {
            return;
        }

        _isActive = true;

        var collection = _mongoHelper.Database.GetCollection<MongoQueueItem<TPayload>>(_queueDefinition.CollectionName);
        await EnsureIndexesAsync(collection, cancellationToken);

        // Signal initially to start processing any existing messages
        _signalSemaphore.Release();

        _monitorTask = Task.Run(async () => await MonitorChangeStreamAsync(collection, _cancellationTokenSource.Token), cancellationToken);
        _processingTask = Task.Run(async () => await ProcessQueueItemsAsync(collection, _cancellationTokenSource.Token), cancellationToken);
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed)
        {
            return;
        }

        await _cancellationTokenSource.CancelAsync();
        _isActive = false;

        try
        {
            await Task.WhenAll(_monitorTask, _processingTask).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Stopping queue subscription for payload {PayloadType} has been cancelled, " +
                               "background tasks may still be running", _queueDefinition.PayloadType.FullName);
        }
    }
}
