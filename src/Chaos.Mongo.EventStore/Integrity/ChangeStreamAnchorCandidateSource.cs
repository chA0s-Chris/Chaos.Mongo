// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

/// <summary>
/// Reads anchor candidates from one database-wide change stream over the protected events collections.
/// </summary>
/// <remarks>
///     <para>
///     The persisted position is the post-batch resume token of the last acknowledged batch, so even an idle
///     stream keeps a recent token. A read without a preceding acknowledgement, after an acknowledgement whose
///     transaction did not commit, after a failed read, or with a different batch size reopens the stream at the
///     persisted position.
///     </para>
///     <para>
///     Without a position, or when the position is no longer in the oplog, the source recovers: it takes the
///     cluster time, scans every stream head and then starts the stream at that cluster time. Each batch of the
///     scan examines a bounded number of streams, and acknowledging persists the scan position, so a restart
///     continues the scan instead of repeating it.
///     </para>
///     <para>
///     Operating requirements:
///     </para>
///     <list type="bullet">
///         <item>
///             <description>
///             A replica set or a sharded cluster, as for the event store's transactions; a single-node replica
///             set suffices.
///             </description>
///         </item>
///         <item>
///             <description>
///             An oplog window that covers the longest expected anchoring downtime plus the duration of a full
///             recovery scan. A shorter window forces a recovery, and a scan that outlasts the window repeats.
///             </description>
///         </item>
///         <item>
///             <description>
///             The <c>changeStream</c> and <c>find</c> actions on the database, because the change stream
///             covers the whole database.
///             </description>
///         </item>
///         <item>
///             <description>
///             MongoDB-compatible services such as Amazon DocumentDB or Azure Cosmos DB restrict change streams,
///             for example by per-collection activation or a short retention; they are not supported without
///             verification.
///             </description>
///         </item>
///         <item>
///             <description>
///             On sharded clusters, <c>mongos</c> merges the change streams of all shards, which adds server load.
///             </description>
///         </item>
///     </list>
/// </remarks>
internal sealed class ChangeStreamAnchorCandidateSource : IAnchorCandidateSource
{
    /// <summary>
    /// The server error code for a resume point that is no longer in the oplog.
    /// </summary>
    internal const Int32 ChangeStreamHistoryLostCode = 286;

    private const String CollectionsElement = "Collections";
    private const String OperationTimeElement = "OperationTime";
    private const String RecoveryElement = "Recovery";
    private const String ResumeTokenElement = "ResumeToken";
    private const String ScanCompletedElement = "ScanCompletedUtc";
    private const String ScanStartedElement = "ScanStartedUtc";
    private const String StateDocumentId = "changeStream";
    private const String VersionElement = "Version";

    private readonly ILogger<ChangeStreamAnchorCandidateSource> _logger;
    private readonly IMongoHelper _mongoHelper;
    private readonly IntegrityAnchoringOptions _options;
    private readonly ProtectedStreamRegistry _registry;
    private readonly TimeProvider _timeProvider;
    private IChangeStreamCursor<BsonDocument>? _cursor;
    private Int32 _cursorBatchSize;
    private BsonDocument? _expectedState;
    private Boolean _hasOutstandingBatch;
    private Int64? _readVersion;

    public ChangeStreamAnchorCandidateSource(IMongoHelper mongoHelper,
                                             IntegrityAnchoringOptions options,
                                             ProtectedStreamRegistry registry,
                                             TimeProvider timeProvider,
                                             ILogger<ChangeStreamAnchorCandidateSource> logger)
    {
        ArgumentNullException.ThrowIfNull(mongoHelper);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _mongoHelper = mongoHelper;
        _options = options;
        _registry = registry;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    private static InvalidOperationException CreateConflictException(Exception? innerException = null)
        => new("Another consumer changed the anchor candidate state since this batch was read. " +
               "Only one consumer may read anchor candidates at a time.",
               innerException);

    private static ScanPosition ReadScanPosition(BsonDocument recovery)
        => new(recovery[OperationTimeElement].AsBsonTimestamp,
               recovery["TypeIndex"].ToInt32(),
               recovery["Cursor"].IsBsonNull ? null : recovery["Cursor"].AsGuid,
               recovery[ScanStartedElement].ToUniversalTime(),
               null);

    private static AnchorCandidate? TryCreateCandidate(BsonDocument change, Dictionary<String, ProtectedStreamType> types)
    {
        if (!change.TryGetValue("fullDocument", out var fullDocumentValue) ||
            fullDocumentValue is not BsonDocument fullDocument ||
            !types.TryGetValue(change["ns"]["coll"].AsString, out var type) ||
            !fullDocument.TryGetValue(EventIntegrityChain.ElementName, out var integrityValue) ||
            !EventIntegrityChain.TryRead(integrityValue, out var integrity))
        {
            // Unsealed inserts are anchored once the sweep seals them; deleted documents have no head to anchor.
            return null;
        }

        return new AnchorCandidate(type.AggregateTypeName,
                                   fullDocument[type.AggregateIdElement].AsGuid,
                                   fullDocument[type.VersionElement].ToInt64(),
                                   integrity.Hash);
    }

    /// <summary>
    /// Takes the cluster time before scanning, so the stream started at it afterwards misses no commit. A snapshot
    /// read's cluster time lies behind every uncommitted write, so the scan cannot miss a commit that the stream
    /// then skips. A read of a collection that does not exist yet reports no snapshot time, so the read is a
    /// collection-less aggregate.
    /// </summary>
    private async Task<ScanPosition> BeginRecoveryAsync(CancellationToken cancellationToken)
    {
        var command = new BsonDocument
        {
            { "aggregate", 1 },
            {
                "pipeline", new BsonArray
                {
                    new BsonDocument("$documents", new BsonArray())
                }
            },
            { "cursor", new BsonDocument() },
            { "readConcern", new BsonDocument("level", "snapshot") }
        };
        var reply = await _mongoHelper.Database.RunCommandAsync<BsonDocument>(command, cancellationToken: cancellationToken);
        var snapshotTime = reply["cursor"].AsBsonDocument.GetValue("atClusterTime", BsonNull.Value) as BsonTimestamp ??
                           throw new InvalidOperationException("The server did not report a snapshot time; change streams need a replica set.");
        return new ScanPosition(snapshotTime, 0, null, _timeProvider.GetUtcNow().UtcDateTime, null);
    }

    private void CloseCursor()
    {
        _cursor?.Dispose();
        _cursor = null;
    }

    private PipelineDefinition<ChangeStreamDocument<BsonDocument>, BsonDocument> CreatePipeline()
    {
        var collections = new BsonArray(_registry.Types.Select(type => type.EventsCollectionName));
        var match = new BsonDocument("$match", new BsonDocument
        {
            { "ns.db", _mongoHelper.Database.DatabaseNamespace.DatabaseName },
            { "ns.coll", new BsonDocument("$in", collections) },
            {
                "$or", new BsonArray
                {
                    new BsonDocument("operationType", "insert"),
                    new BsonDocument
                    {
                        { "operationType", "update" },
                        { $"updateDescription.updatedFields.{EventIntegrityChain.ElementName}", new BsonDocument("$exists", true) }
                    }
                }
            }
        });

        var projection = new BsonDocument("ns", 1);
        foreach (var element in _registry.Types.SelectMany(type => new[]
                 {
                     type.AggregateIdElement,
                     type.VersionElement
                 }).Distinct())
        {
            projection[$"fullDocument.{element}"] = 1;
        }

        projection[$"fullDocument.{EventIntegrityChain.ElementName}"] = 1;

        return PipelineDefinition<ChangeStreamDocument<BsonDocument>, BsonDocument>.Create([match, new BsonDocument("$project", projection)]);
    }

    private BsonDocument CreateScanState(ScanPosition position)
    {
        if (position.CompletedUtc is { } completed)
        {
            return CreateStreamState(new StreamPosition(null, position.OperationTime, position.StartedUtc, completed));
        }

        return new BsonDocument
        {
            { "_id", StateDocumentId },
            { CollectionsElement, GetCoveredCollections() },
            {
                RecoveryElement, new BsonDocument
                {
                    { OperationTimeElement, position.OperationTime },
                    { "TypeIndex", position.TypeIndex },
                    { "Cursor", position.Cursor is { } cursor ? new BsonBinaryData(cursor, GuidRepresentation.Standard) : BsonNull.Value },
                    { ScanStartedElement, position.StartedUtc }
                }
            }
        };
    }

    private BsonDocument CreateStreamState(StreamPosition position)
    {
        var state = new BsonDocument
        {
            { "_id", StateDocumentId },
            { CollectionsElement, GetCoveredCollections() }
        };
        if (position.ResumeToken is not null)
        {
            state.Add(ResumeTokenElement, position.ResumeToken);
            return state;
        }

        // Without a resume token, the stream keeps starting at the cluster time of the completed scan.
        state.Add(OperationTimeElement,
                  position.OperationTime ?? throw new InvalidOperationException("A stream position needs a resume token or an operation time."));
        if (position.ScanStartedUtc is { } started && position.ScanCompletedUtc is { } completed)
        {
            state.Add(ScanStartedElement, started);
            state.Add(ScanCompletedElement, completed);
        }

        return state;
    }

    /// <summary>
    /// The events collections the stream and the recovery scan cover, in registry order, which is the order the
    /// scan's type index refers to.
    /// </summary>
    private BsonArray GetCoveredCollections()
        => new(_registry.Types.Select(type => type.EventsCollectionName));

    private IMongoCollection<BsonDocument> GetStateCollection()
        => _mongoHelper.Database.GetCollection<BsonDocument>(_options.ChangeStreamStateCollectionName);

    /// <summary>
    /// Logs why a recovery starts. Losing the cluster time of a just-completed scan means the oplog window is
    /// shorter than a full scan, so the recovery repeats until the oplog grows.
    /// </summary>
    private void LogHistoryLost(BsonDocument state)
    {
        if (!state.Contains(ResumeTokenElement) &&
            state.TryGetValue(ScanStartedElement, out var started) &&
            state.TryGetValue(ScanCompletedElement, out var completed))
        {
            var completedUtc = completed.ToUniversalTime();
            _logger.LogError("Anchor candidate recovery for database {DatabaseName} must start over: its scan took {ScanDuration} " +
                             "and completed {SinceCompletion} ago, together longer than the oplog window. Increase the oplog size " +
                             "so that it covers a full recovery scan and the time until candidates are read again",
                             _mongoHelper.Database.DatabaseNamespace.DatabaseName,
                             completedUtc - started.ToUniversalTime(),
                             _timeProvider.GetUtcNow().UtcDateTime - completedUtc);
            return;
        }

        _logger.LogWarning("Anchor candidate recovery for database {DatabaseName} started: the change-stream position is no longer in the oplog",
                           _mongoHelper.Database.DatabaseNamespace.DatabaseName);
    }

    private async Task<IChangeStreamCursor<BsonDocument>> OpenCursorAsync(BsonDocument state, Int32 maxCandidates, CancellationToken cancellationToken)
    {
        var options = new ChangeStreamOptions
        {
            FullDocument = ChangeStreamFullDocumentOption.UpdateLookup,
            BatchSize = maxCandidates,
            MaxAwaitTime = TimeSpan.FromMilliseconds(1)
        };

        // StartAfter, unlike ResumeAfter, also accepts the token of an invalidate event.
        if (state.TryGetValue(ResumeTokenElement, out var resumeToken))
        {
            options.StartAfter = resumeToken.AsBsonDocument;
        }
        else
        {
            options.StartAtOperationTime = state[OperationTimeElement].AsBsonTimestamp;
        }

        var cursor = await _mongoHelper.Database.WatchAsync(CreatePipeline(), options, cancellationToken);
        _cursorBatchSize = maxCandidates;
        return cursor;
    }

    private async Task<AnchorCandidateBatch> ReadStreamBatchAsync(BsonDocument state, Int32 maxCandidates, CancellationToken cancellationToken)
    {
        _cursor ??= await OpenCursorAsync(state, maxCandidates, cancellationToken);

        var candidates = new List<AnchorCandidate>();
        var changes = 0;
        if (await _cursor.MoveNextAsync(cancellationToken))
        {
            var types = _registry.Types.ToDictionary(type => type.EventsCollectionName);
            foreach (var change in _cursor.Current)
            {
                changes++;
                if (TryCreateCandidate(change, types) is { } candidate)
                {
                    candidates.Add(candidate);
                }
            }
        }

        // The server may return fewer changes than the batch size while more are waiting, so only an empty batch,
        // counting changes that yield no candidate, shows that the stream is caught up.
        var hasMore = changes > 0;
        var resumeToken = _cursor.GetResumeToken();
        var position = resumeToken is null
            ? new StreamPosition(state.GetValue(ResumeTokenElement, BsonNull.Value) as BsonDocument,
                                 state.GetValue(OperationTimeElement, BsonNull.Value) as BsonTimestamp,
                                 state.GetValue(ScanStartedElement, BsonNull.Value).ToNullableUniversalTime(),
                                 state.GetValue(ScanCompletedElement, BsonNull.Value).ToNullableUniversalTime())
            : new StreamPosition(resumeToken, null, null, null);
        return new AnchorCandidateBatch(candidates, position, hasMore);
    }

    /// <summary>
    /// Scans stream heads from the scan position, two index point reads per stream, until it has examined
    /// <paramref name="maxCandidates"/> streams or every protected type is scanned.
    /// </summary>
    private async Task<AnchorCandidateBatch> ScanAsync(ScanPosition position, Int32 maxCandidates, CancellationToken cancellationToken)
    {
        var candidates = new List<AnchorCandidate>();
        var typeIndex = position.TypeIndex;
        var cursor = position.Cursor;
        var examined = 0;

        while (typeIndex < _registry.Types.Count && examined < maxCandidates)
        {
            var type = _registry.Types[typeIndex];
            var events = _mongoHelper.Database.GetCollection<BsonDocument>(type.EventsCollectionName);
            var nextQuery = type.NextStream(cursor);
            var next = await events.Find(nextQuery.Filter).Sort(nextQuery.Sort).Project(nextQuery.Projection).Limit(1)
                                   .FirstOrDefaultAsync(cancellationToken);
            if (next is null)
            {
                typeIndex++;
                cursor = null;
                continue;
            }

            examined++;
            var aggregateId = next[type.AggregateIdElement].AsGuid;
            var headQuery = type.StreamHead(aggregateId);
            var head = await events.Find(headQuery.Filter).Sort(headQuery.Sort).Project(headQuery.Projection).Limit(1)
                                   .FirstOrDefaultAsync(cancellationToken);
            if (head is not null &&
                head.TryGetValue(EventIntegrityChain.ElementName, out var integrityValue) &&
                EventIntegrityChain.TryRead(integrityValue, out var integrity))
            {
                candidates.Add(new AnchorCandidate(type.AggregateTypeName, aggregateId, head[type.VersionElement].ToInt64(), integrity.Hash));
            }

            cursor = aggregateId;
        }

        var isComplete = typeIndex >= _registry.Types.Count;
        DateTime? completedUtc = null;
        if (isComplete)
        {
            completedUtc = _timeProvider.GetUtcNow().UtcDateTime;
            _logger.LogInformation("Anchor candidate recovery scan for database {DatabaseName} finished after {ScanDuration}",
                                   _mongoHelper.Database.DatabaseNamespace.DatabaseName,
                                   completedUtc.Value - position.StartedUtc);
        }

        // Even a completed scan reports more, because the stream from the scan's cluster time may already hold
        // changes made during the scan; only an empty stream batch shows that the source is caught up.
        return new AnchorCandidateBatch(candidates,
                                        new ScanPosition(position.OperationTime, typeIndex, cursor, position.StartedUtc, completedUtc),
                                        true);
    }

    /// <inheritdoc/>
    public async Task AcknowledgeAsync(IClientSessionHandle session, AnchorCandidateBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(batch);

        var state = batch.Position switch
        {
            StreamPosition position => CreateStreamState(position),
            ScanPosition position => CreateScanState(position),
            _ => throw new ArgumentException("The batch was not read from the change-stream source.", nameof(batch))
        };

        // Only the state version that was read may be replaced, so a second consumer fails instead of rewinding progress.
        state[VersionElement] = (_readVersion ?? 0) + 1;
        var filter = _readVersion is { } readVersion
            ? new BsonDocument
            {
                { "_id", StateDocumentId },
                { VersionElement, readVersion }
            }
            : new BsonDocument
            {
                { "_id", StateDocumentId },
                { VersionElement, new BsonDocument("$exists", false) }
            };
        try
        {
            var result = await GetStateCollection().ReplaceOneAsync(session,
                                                                    filter,
                                                                    state,
                                                                    new ReplaceOptions
                                                                    {
                                                                        IsUpsert = _readVersion is null
                                                                    },
                                                                    cancellationToken);
            if (_readVersion is not null && result.MatchedCount == 0)
            {
                throw CreateConflictException();
            }
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            throw CreateConflictException(ex);
        }

        // Committed or not, a later read compares this with the persisted state and reopens on a mismatch.
        _expectedState = state;
        _hasOutstandingBatch = false;
    }

    /// <inheritdoc/>
    public async Task<AnchorCandidateBatch> ReadBatchAsync(Int32 maxCandidates, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCandidates);

        var state = await GetStateCollection().Find(new BsonDocument("_id", StateDocumentId)).FirstOrDefaultAsync(cancellationToken);
        if (_hasOutstandingBatch || _cursor is null || _cursorBatchSize != maxCandidates || !Equals(state, _expectedState))
        {
            CloseCursor();
            _expectedState = state;
        }

        // Stays set when the read fails, so the next read reopens the stream at the persisted position.
        _hasOutstandingBatch = true;
        _readVersion = state?.GetValue(VersionElement, BsonNull.Value) is { IsBsonNull: false } version ? version.ToInt64() : null;

        if (state is null || (!state.Contains(ResumeTokenElement) && !state.Contains(OperationTimeElement) && !state.Contains(RecoveryElement)))
        {
            _logger.LogWarning("Anchor candidate recovery for database {DatabaseName} started: no change-stream position is persisted",
                               _mongoHelper.Database.DatabaseNamespace.DatabaseName);
            return await ScanAsync(await BeginRecoveryAsync(cancellationToken), maxCandidates, cancellationToken);
        }

        // A position covers the collections that were protected when it was written; sealed heads of a newly
        // protected collection would otherwise never be returned, and a scan's type index would point elsewhere.
        if (!Equals(state.GetValue(CollectionsElement, BsonNull.Value), GetCoveredCollections()))
        {
            CloseCursor();
            _logger.LogWarning("Anchor candidate recovery for database {DatabaseName} started: the protected events collections changed",
                               _mongoHelper.Database.DatabaseNamespace.DatabaseName);
            return await ScanAsync(await BeginRecoveryAsync(cancellationToken), maxCandidates, cancellationToken);
        }

        if (state.GetValue(RecoveryElement, BsonNull.Value) is BsonDocument recovery)
        {
            return await ScanAsync(ReadScanPosition(recovery), maxCandidates, cancellationToken);
        }

        try
        {
            return await ReadStreamBatchAsync(state, maxCandidates, cancellationToken);
        }
        catch (MongoCommandException ex) when (ex.Code == ChangeStreamHistoryLostCode)
        {
            CloseCursor();
            LogHistoryLost(state);
            return await ScanAsync(await BeginRecoveryAsync(cancellationToken), maxCandidates, cancellationToken);
        }
    }

    /// <inheritdoc/>
    public void Dispose() => CloseCursor();

    private sealed record ScanPosition(BsonTimestamp OperationTime, Int32 TypeIndex, Guid? Cursor, DateTime StartedUtc, DateTime? CompletedUtc);

    private sealed record StreamPosition(BsonDocument? ResumeToken, BsonTimestamp? OperationTime, DateTime? ScanStartedUtc, DateTime? ScanCompletedUtc);
}
