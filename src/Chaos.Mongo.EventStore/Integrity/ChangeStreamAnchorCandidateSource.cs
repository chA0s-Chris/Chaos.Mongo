// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using MongoDB.Bson;
using MongoDB.Driver;

/// <summary>
/// Reads anchor candidates from one database-wide change stream over the protected events collections
/// (option D of #154).
/// </summary>
/// <remarks>
///     <para>
///     The persisted position is the post-batch resume token of the last acknowledged batch, so even an idle
///     stream keeps a recent token. A read without a preceding acknowledgement, or after an acknowledgement
///     whose transaction did not commit, reopens the stream at the persisted position.
///     </para>
///     <para>
///     Without a position, or when the position is no longer in the oplog, the source recovers: it takes the
///     cluster time, scans every stream head and then starts the stream at that cluster time. Acknowledging
///     during recovery persists the scan position, so a restart continues the scan instead of repeating it.
///     </para>
/// </remarks>
internal sealed class ChangeStreamAnchorCandidateSource : IAnchorCandidateSource
{
    /// <summary>
    /// The server error code for a resume point that is no longer in the oplog.
    /// </summary>
    internal const Int32 ChangeStreamHistoryLostCode = 286;

    private const String OperationTimeElement = "OperationTime";
    private const String RecoveryElement = "Recovery";
    private const String ResumeTokenElement = "ResumeToken";
    private const String StateDocumentId = "changeStream";

    private readonly IMongoHelper _mongoHelper;
    private readonly IntegrityAnchoringOptions _options;
    private readonly ProtectedStreamRegistry _registry;
    private IChangeStreamCursor<BsonDocument>? _cursor;
    private BsonDocument? _expectedState;
    private Boolean _hasOutstandingBatch;

    public ChangeStreamAnchorCandidateSource(IMongoHelper mongoHelper,
                                             IntegrityAnchoringOptions options,
                                             ProtectedStreamRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(mongoHelper);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(registry);
        _mongoHelper = mongoHelper;
        _options = options;
        _registry = registry;
    }

    private static BsonDocument CreateStreamState(StreamPosition position)
    {
        var state = new BsonDocument("_id", StateDocumentId);
        if (position.ResumeToken is not null)
        {
            state.Add(ResumeTokenElement, position.ResumeToken);
        }
        else if (position.OperationTime is not null)
        {
            state.Add(OperationTimeElement, position.OperationTime);
        }

        return state;
    }

    private static ScanPosition ReadScanPosition(BsonDocument recovery)
        => new(recovery[OperationTimeElement].AsBsonTimestamp,
               recovery["TypeIndex"].ToInt32(),
               recovery["Cursor"].IsBsonNull ? null : recovery["Cursor"].AsGuid,
               false);

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
    /// Takes the cluster time before scanning, so the stream started at it afterwards misses no commit.
    /// </summary>
    private async Task<ScanPosition> BeginRecoveryAsync(CancellationToken cancellationToken)
    {
        using var session = await _mongoHelper.Client.StartSessionAsync(cancellationToken: cancellationToken);
        await GetStateCollection().Find(session, new BsonDocument("_id", StateDocumentId)).FirstOrDefaultAsync(cancellationToken);
        var operationTime = session.OperationTime ??
                            throw new InvalidOperationException("The server did not report an operation time; change streams need a replica set.");
        return new ScanPosition(operationTime, 0, null, false);
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

    private IMongoCollection<BsonDocument> GetStateCollection()
        => _mongoHelper.Database.GetCollection<BsonDocument>(_options.ChangeStreamStateCollectionName);

    private async Task<IChangeStreamCursor<BsonDocument>> OpenCursorAsync(BsonDocument state, Int32 maxCandidates, CancellationToken cancellationToken)
    {
        var options = new ChangeStreamOptions
        {
            FullDocument = ChangeStreamFullDocumentOption.UpdateLookup,
            BatchSize = maxCandidates,
            MaxAwaitTime = TimeSpan.FromMilliseconds(1)
        };

        if (state.TryGetValue(ResumeTokenElement, out var resumeToken))
        {
            options.ResumeAfter = resumeToken.AsBsonDocument;
        }
        else
        {
            options.StartAtOperationTime = state[OperationTimeElement].AsBsonTimestamp;
        }

        return await _mongoHelper.Database.WatchAsync(CreatePipeline(), options, cancellationToken);
    }

    private async Task<AnchorCandidateBatch> ReadStreamBatchAsync(BsonDocument state, Int32 maxCandidates, CancellationToken cancellationToken)
    {
        _cursor ??= await OpenCursorAsync(state, maxCandidates, cancellationToken);

        var candidates = new List<AnchorCandidate>();
        if (await _cursor.MoveNextAsync(cancellationToken))
        {
            var types = _registry.Types.ToDictionary(type => type.EventsCollectionName);
            foreach (var change in _cursor.Current)
            {
                if (TryCreateCandidate(change, types) is { } candidate)
                {
                    candidates.Add(candidate);
                }
            }
        }

        var resumeToken = _cursor.GetResumeToken();
        return new AnchorCandidateBatch(candidates, resumeToken is null
                                            ? new StreamPosition(state.GetValue(ResumeTokenElement, BsonNull.Value) as BsonDocument,
                                                                 state.GetValue(OperationTimeElement, BsonNull.Value) as BsonTimestamp)
                                            : new StreamPosition(resumeToken, null));
    }

    /// <summary>
    /// Scans stream heads from the scan position, two index point reads per stream, until the batch is full or
    /// every protected type is scanned.
    /// </summary>
    private async Task<AnchorCandidateBatch> ScanAsync(ScanPosition position, Int32 maxCandidates, CancellationToken cancellationToken)
    {
        var candidates = new List<AnchorCandidate>();
        var typeIndex = position.TypeIndex;
        var cursor = position.Cursor;

        while (typeIndex < _registry.Types.Count && candidates.Count < maxCandidates)
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

        return new AnchorCandidateBatch(candidates, new ScanPosition(position.OperationTime, typeIndex, cursor, typeIndex >= _registry.Types.Count));
    }

    /// <inheritdoc/>
    public async Task AcknowledgeAsync(IClientSessionHandle session, AnchorCandidateBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(batch);

        var state = batch.Position switch
        {
            StreamPosition position => CreateStreamState(position),
            ScanPosition { IsComplete: true } position => CreateStreamState(new StreamPosition(null, position.OperationTime)),
            ScanPosition position => new BsonDocument
            {
                { "_id", StateDocumentId },
                {
                    RecoveryElement, new BsonDocument
                    {
                        { OperationTimeElement, position.OperationTime },
                        { "TypeIndex", position.TypeIndex },
                        { "Cursor", position.Cursor is { } cursor ? new BsonBinaryData(cursor, GuidRepresentation.Standard) : BsonNull.Value }
                    }
                }
            },
            _ => throw new ArgumentException("The batch was not read from the change-stream source.", nameof(batch))
        };

        await GetStateCollection().ReplaceOneAsync(session,
                                                   new BsonDocument("_id", StateDocumentId),
                                                   state,
                                                   new ReplaceOptions
                                                   {
                                                       IsUpsert = true
                                                   },
                                                   cancellationToken);

        // Committed or not, a later read compares this with the persisted state and reopens on a mismatch.
        _expectedState = state;
        _hasOutstandingBatch = false;
    }

    /// <inheritdoc/>
    public async Task<AnchorCandidateBatch> ReadBatchAsync(Int32 maxCandidates, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCandidates);

        var state = await GetStateCollection().Find(new BsonDocument("_id", StateDocumentId)).FirstOrDefaultAsync(cancellationToken);
        if (_hasOutstandingBatch || _cursor is null || !Equals(state, _expectedState))
        {
            CloseCursor();
            _expectedState = state;
        }

        _hasOutstandingBatch = true;

        if (state?.GetValue(RecoveryElement, BsonNull.Value) is BsonDocument recovery)
        {
            return await ScanAsync(ReadScanPosition(recovery), maxCandidates, cancellationToken);
        }

        if (state is null || (!state.Contains(ResumeTokenElement) && !state.Contains(OperationTimeElement)))
        {
            return await ScanAsync(await BeginRecoveryAsync(cancellationToken), maxCandidates, cancellationToken);
        }

        try
        {
            return await ReadStreamBatchAsync(state, maxCandidates, cancellationToken);
        }
        catch (MongoCommandException ex) when (ex.Code == ChangeStreamHistoryLostCode)
        {
            CloseCursor();
            return await ScanAsync(await BeginRecoveryAsync(cancellationToken), maxCandidates, cancellationToken);
        }
    }

    /// <inheritdoc/>
    public void Dispose() => CloseCursor();

    private sealed record ScanPosition(BsonTimestamp OperationTime, Int32 TypeIndex, Guid? Cursor, Boolean IsComplete);

    private sealed record StreamPosition(BsonDocument? ResumeToken, BsonTimestamp? OperationTime);
}
