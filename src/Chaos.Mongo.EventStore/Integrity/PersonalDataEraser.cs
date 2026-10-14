// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using Chaos.Mongo.EventStore.Errors;
using MongoDB.Bson;
using MongoDB.Driver;

/// <summary>
/// Erases personal data values from the sealed events of a stream without breaking its hash chain.
/// </summary>
/// <remarks>
///     <para>
///     An erasure appends a <see cref="PersonalDataErased{TAggregate}"/> event through the ordinary append path, so
///     sealing, concurrency retries and both write paths apply unchanged. Inside the append transaction it sets the
///     selected values to <c>null</c> in place, removes their salts while their commitments stay, and rebuilds the read
///     model and the checkpoints at or above the first erased version. On failure, nothing is persisted.
///     </para>
///     <para>
///     Only the <c>PersonalData</c> subfield of the integrity data is rewritten, so the change-stream candidate source,
///     which matches updates of the <c>_integrity</c> element as a whole, ignores the update. The server could report
///     the update as a replacement only if the delta exceeded the document, which a sealed document's hashes and
///     commitments rule out. A long stream without a checkpoint below the first erased version is replayed within one
///     transaction, which is bounded by the server's transaction lifetime limit.
///     </para>
///     <para>
///     The rebuilt read model and checkpoints hold only what replaying the events produces; read-model fields written
///     outside <c>Execute</c> are reset. A request without explicit versions fails when events were appended after its
///     selection, so no later event escapes it.
///     </para>
/// </remarks>
/// <typeparam name="TAggregate">The aggregate type.</typeparam>
internal sealed class PersonalDataEraser<TAggregate> where TAggregate : class, IAggregate, new()
{
    private const String PersonalDataField = EventIntegrityChain.ElementName + "." + nameof(EventIntegrity.PersonalData);
    private readonly IMongoHelper _mongoHelper;
    private readonly MongoEventStoreOptions<TAggregate> _options;
    private readonly IEventStore<TAggregate> _store;

    public PersonalDataEraser(IMongoHelper mongoHelper, MongoEventStoreOptions<TAggregate> options, IEventStore<TAggregate> store)
    {
        ArgumentNullException.ThrowIfNull(mongoHelper);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(store);
        _mongoHelper = mongoHelper;
        _options = options;
        _store = store;
    }

    /// <summary>
    /// Erases the selected personal data values of a stream in one transaction. Values already erased are skipped;
    /// when nothing is left to erase, no event is appended.
    /// </summary>
    /// <param name="aggregateId">The aggregate identifier.</param>
    /// <param name="request">The selection and the reference to record.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The erased paths per event version, empty when nothing was left to erase.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="aggregateId"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown when integrity protection is disabled.</exception>
    /// <exception cref="MongoEventStoreException">
    /// Thrown when the stream has no events, when a requested version does not exist or has no recorded personal data,
    /// when a requested path is recorded in none of the selected events, when a selected event is not sealed or its
    /// integrity data is malformed, or when the read model cannot be rebuilt consistently.
    /// </exception>
    /// <exception cref="MongoConcurrencyException">
    /// Thrown when the stream or a selected salt changed concurrently, or when a request without explicit versions
    /// finds events appended after its selection.
    /// </exception>
    public async Task<IReadOnlyList<PersonalDataErasureEntry>> ErasePersonalDataAsync(Guid aggregateId,
                                                                                      ErasureRequest request,
                                                                                      CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (aggregateId == Guid.Empty)
        {
            throw new ArgumentException("The aggregate identifier must not be empty.", nameof(aggregateId));
        }

        if (!_options.IntegrityProtectionEnabled)
        {
            throw new InvalidOperationException(
                $"Personal data of aggregate type {typeof(TAggregate).Name} cannot be erased, because integrity protection is disabled.");
        }

        var (entries, lastSelectedVersion) = await SelectAsync(aggregateId, request, cancellationToken);
        if (entries.Count == 0)
        {
            return entries;
        }

        // A whole-stream request covers the stream as selected; an event appended since would escape it.
        var expectedErasureVersion = request.Versions is null ? lastSelectedVersion + 1 : (Int64?)null;

        var erasure = new PersonalDataErased<TAggregate>
        {
            AggregateId = aggregateId,
            Reference = request.Reference,
            Entries = entries
        };

        await _store.AppendEventsAsync(
            [erasure],
            onBeforeCommit: (session, aggregate, _, ct) =>
            {
                if (expectedErasureVersion is { } expectedVersion && erasure.Version != expectedVersion)
                {
                    throw new MongoConcurrencyException(
                        $"Aggregate '{aggregateId}' received events after the personal data to erase was selected.");
                }

                return EraseAsync(session, aggregate, entries, ct);
            },
            cancellationToken: cancellationToken);

        return entries;
    }

    private static MongoEventStoreException CreateCheckpointException(Guid aggregateId, Int64 checkpointVersion)
        => new($"The checkpoint at version {checkpointVersion} of aggregate '{aggregateId}' has no state-changing event at that " +
               "version, so it cannot be rebuilt.");

    /// <summary>
    /// Creates the update that nulls the selected values of one event and removes their salts. The filter requires
    /// every salt to be present, so a concurrent erasure leaves the update unmatched.
    /// </summary>
    private static UpdateOneModel<BsonDocument> CreateErasureUpdate(Guid aggregateId, PersonalDataErasureEntry entry)
    {
        var filters = new List<FilterDefinition<BsonDocument>>(entry.Paths.Count + 1)
        {
            EventDocumentFields<TAggregate>.ForVersion<BsonDocument>(aggregateId, entry.Version)
        };
        var updates = new List<UpdateDefinition<BsonDocument>>(entry.Paths.Count * 2);
        var arrayFilters = new List<ArrayFilterDefinition>(entry.Paths.Count);

        for (var index = 0; index < entry.Paths.Count; index++)
        {
            var path = entry.Paths[index];
            var identifier = $"commitment{index}";
            filters.Add(new BsonDocumentFilterDefinition<BsonDocument>(new BsonDocument(
                                                                           PersonalDataField,
                                                                           new BsonDocument("$elemMatch", new BsonDocument
                                                                           {
                                                                               { nameof(PersonalDataCommitment.Path), path },
                                                                               { nameof(PersonalDataCommitment.Salt), new BsonDocument("$exists", true) }
                                                                           }))));
            updates.Add(Builders<BsonDocument>.Update.Set(path, BsonNull.Value));
            updates.Add(Builders<BsonDocument>.Update.Unset($"{PersonalDataField}.$[{identifier}].{nameof(PersonalDataCommitment.Salt)}"));
            arrayFilters.Add(new BsonDocumentArrayFilterDefinition<BsonDocument>(
                                 new BsonDocument($"{identifier}.{nameof(PersonalDataCommitment.Path)}", path)));
        }

        return new UpdateOneModel<BsonDocument>(Builders<BsonDocument>.Filter.And(filters), Builders<BsonDocument>.Update.Combine(updates))
        {
            ArrayFilters = arrayFilters
        };
    }

    /// <summary>
    /// Checks that every requested version exists and that every requested path is recorded, with or without its
    /// salt, in at least one selected event.
    /// </summary>
    private static void EnsureRequestSatisfied(Guid aggregateId, ErasureRequest request, HashSet<Int64> foundVersions, HashSet<String> recordedPaths)
    {
        foreach (var version in request.Versions ?? [])
        {
            if (!foundVersions.Contains(version))
            {
                throw new MongoEventStoreException($"Version {version} of aggregate '{aggregateId}' does not exist.");
            }
        }

        foreach (var path in request.Paths ?? [])
        {
            if (!recordedPaths.Contains(path))
            {
                throw new MongoEventStoreException(
                    $"The path '{path}' is not recorded as personal data in any selected event of aggregate '{aggregateId}'.");
            }
        }
    }

    /// <summary>
    /// Reads the recorded commitments of a stored event. Every selected event must be sealed with well-formed
    /// integrity data.
    /// </summary>
    private static IReadOnlyList<PersonalDataCommitment> ReadCommitments(BsonDocument document, Guid aggregateId, Int64 version)
    {
        if (!document.TryGetValue(EventIntegrityChain.ElementName, out var integrityValue))
        {
            throw new MongoEventStoreException(
                $"Version {version} of aggregate '{aggregateId}' is not sealed, so its personal data cannot be erased.");
        }

        if (!EventIntegrityChain.TryRead(integrityValue, out var integrity) ||
            (integrity.PersonalData is { } commitments && !PersonalDataCommitments.IsWellFormed(commitments)))
        {
            throw new MongoEventStoreException(
                $"The integrity data of version {version} of aggregate '{aggregateId}' is malformed, so its personal data cannot be erased.");
        }

        return integrity.PersonalData ?? [];
    }

    /// <summary>
    /// Selects the paths of one event that match the request and still have their salt, recording every matching
    /// path whether or not it has one.
    /// </summary>
    private static PersonalDataErasureEntry? SelectEntry(Int64 version,
                                                         IReadOnlyList<PersonalDataCommitment> commitments,
                                                         HashSet<String>? requestedPaths,
                                                         HashSet<String> recordedPaths)
    {
        var paths = new List<String>();
        foreach (var commitment in commitments)
        {
            if (requestedPaths is not null && !requestedPaths.Contains(commitment.Path))
            {
                continue;
            }

            recordedPaths.Add(commitment.Path);
            if (commitment.Salt is not null)
            {
                paths.Add(commitment.Path);
            }
        }

        return paths.Count == 0
            ? null
            : new PersonalDataErasureEntry
            {
                Version = version,
                Paths = paths
            };
    }

    /// <summary>
    /// Runs inside the append transaction of the erasure event, after the event was inserted.
    /// </summary>
    private async Task EraseAsync(IClientSessionHandle session,
                                  TAggregate readModel,
                                  List<PersonalDataErasureEntry> entries,
                                  CancellationToken cancellationToken)
    {
        var aggregateId = readModel.Id;
        var updates = entries.Select(entry => CreateErasureUpdate(aggregateId, entry)).ToList();
        var result = await GetEventDocumentsCollection().BulkWriteAsync(session, updates, cancellationToken: cancellationToken);

        if (result.MatchedCount != updates.Count)
        {
            throw new MongoConcurrencyException(
                $"Personal data of aggregate '{aggregateId}' was erased concurrently.");
        }

        await RebuildAsync(session, readModel, entries[0].Version, cancellationToken);
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
    /// Loads the state the replay starts from: the newest checkpoint below the first erased version when
    /// checkpoints are enabled, or a new aggregate for a replay from the stream start. Checkpoints below the first
    /// erased version cannot contain erased values.
    /// </summary>
    private async Task<(TAggregate Aggregate, Int64 FromVersion)> LoadStartStateAsync(IClientSessionHandle session,
                                                                                      TAggregate readModel,
                                                                                      Int64 firstErasedVersion,
                                                                                      CancellationToken cancellationToken)
    {
        var checkpointFilter = Builders<CheckpointDocument<TAggregate>>.Filter;
        var start = _options.CheckpointsEnabled
            ? await GetCheckpointCollection().Find(session,
                                                   checkpointFilter.Eq(c => c.Id.AggregateId, readModel.Id) &
                                                   checkpointFilter.Lt(c => c.Id.Version, firstErasedVersion))
                                             .SortByDescending(c => c.Id.Version)
                                             .FirstOrDefaultAsync(cancellationToken)
            : null;

        if (start is null)
        {
            return (new TAggregate
            {
                Id = readModel.Id,
                CreatedUtc = readModel.CreatedUtc
            }, 1);
        }

        LegacyRevision.Normalize(start);
        return (start.State, start.Id.Version + 1);
    }

    /// <summary>
    /// Reads the selected events: the requested versions, or every event of the stream.
    /// </summary>
    private async Task<List<BsonDocument>> ReadSelectedDocumentsAsync(Guid aggregateId, ErasureRequest request, CancellationToken cancellationToken)
    {
        var versionElement = EventDocumentFields<TAggregate>.Version;
        var filter = EventDocumentFields<TAggregate>.ForAggregate<BsonDocument>(aggregateId);
        if (request.Versions is { } versions)
        {
            filter &= new BsonDocumentFilterDefinition<BsonDocument>(
                new BsonDocument(versionElement, new BsonDocument("$in", new BsonArray(versions.Distinct()))));
        }

        return await GetEventDocumentsCollection()
                     .Find(filter)
                     .Sort(Builders<BsonDocument>.Sort.Ascending(versionElement))
                     .Project(Builders<BsonDocument>.Projection.Include(versionElement).Include(EventIntegrityChain.ElementName))
                     .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Replays the state-changing events from the start state, rewriting the state of every checkpoint at or above
    /// the first erased version in the same pass, and then replaces the read model.
    /// </summary>
    private async Task RebuildAsync(IClientSessionHandle session,
                                    TAggregate readModel,
                                    Int64 firstErasedVersion,
                                    CancellationToken cancellationToken)
    {
        var aggregateId = readModel.Id;
        var (aggregate, fromVersion) = await LoadStartStateAsync(session, readModel, firstErasedVersion, cancellationToken);

        // Existing checkpoints are rewritten even when checkpoints are disabled now, so none keeps an erased value.
        var checkpointFilter = Builders<CheckpointDocument<TAggregate>>.Filter;
        var affectedCheckpoints = await GetCheckpointCollection().Find(session,
                                                                       checkpointFilter.Eq(c => c.Id.AggregateId, aggregateId) &
                                                                       checkpointFilter.Gte(c => c.Id.Version, firstErasedVersion))
                                                                 .SortBy(c => c.Id.Version)
                                                                 .Project(c => c.Id.Version)
                                                                 .ToListAsync(cancellationToken);

        await ReplayAsync(session, aggregate, fromVersion, readModel.Version, affectedCheckpoints, cancellationToken);

        if (aggregate.Version != readModel.Version || aggregate.Revision != readModel.Revision)
        {
            throw new MongoEventStoreException(
                $"Rebuilding the read model of aggregate '{aggregateId}' reached version {aggregate.Version} at revision " +
                $"{aggregate.Revision}, but the stored read model is at version {readModel.Version} and revision {readModel.Revision}.");
        }

        aggregate.CreatedUtc = readModel.CreatedUtc;
        await GetReadModelCollection().ReplaceOneAsync(
            session,
            Builders<TAggregate>.Filter.Eq(a => a.Id, aggregateId),
            aggregate,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Applies the state-changing events in the version range to the aggregate and rewrites the state of each
    /// affected checkpoint when the replay reaches its version.
    /// </summary>
    private async Task ReplayAsync(IClientSessionHandle session,
                                   TAggregate aggregate,
                                   Int64 fromVersion,
                                   Int64 toVersion,
                                   List<Int64> affectedCheckpoints,
                                   CancellationToken cancellationToken)
    {
        var eventFilter = Builders<Event<TAggregate>>.Filter;
        var events = await GetEventsCollection()
                           .Find(session,
                                 eventFilter.Eq(e => e.AggregateId, aggregate.Id) &
                                 eventFilter.Gte(e => e.Version, fromVersion) &
                                 eventFilter.Lte(e => e.Version, toVersion) &
                                 EventDocumentFields<TAggregate>.ExcludeObservationalEvents(_options.ObservationalEventDiscriminators))
                           .SortBy(e => e.Version)
                           .ToListAsync(cancellationToken);

        var nextCheckpoint = 0;
        foreach (var @event in events)
        {
            if (@event is ObservationalEvent<TAggregate>)
            {
                continue;
            }

            LegacyRevision.Normalize(@event);
            @event.Execute(aggregate);
            aggregate.Version = @event.Version;
            aggregate.Revision = @event.Revision;

            if (nextCheckpoint < affectedCheckpoints.Count && affectedCheckpoints[nextCheckpoint] < @event.Version)
            {
                throw CreateCheckpointException(aggregate.Id, affectedCheckpoints[nextCheckpoint]);
            }

            if (nextCheckpoint < affectedCheckpoints.Count && affectedCheckpoints[nextCheckpoint] == @event.Version)
            {
                await GetCheckpointCollection().UpdateOneAsync(
                    session,
                    Builders<CheckpointDocument<TAggregate>>.Filter.Eq(c => c.Id, new CheckpointId(aggregate.Id, @event.Version)),
                    Builders<CheckpointDocument<TAggregate>>.Update.Set(c => c.State, aggregate),
                    cancellationToken: cancellationToken);
                nextCheckpoint++;
            }
        }

        if (nextCheckpoint < affectedCheckpoints.Count)
        {
            throw CreateCheckpointException(aggregate.Id, affectedCheckpoints[nextCheckpoint]);
        }
    }

    /// <summary>
    /// Computes the selection from the stored integrity data, before the append.
    /// </summary>
    private async Task<(List<PersonalDataErasureEntry> Entries, Int64 LastSelectedVersion)> SelectAsync(Guid aggregateId,
                                                                                                        ErasureRequest request,
                                                                                                        CancellationToken cancellationToken)
    {
        var documents = await ReadSelectedDocumentsAsync(aggregateId, request, cancellationToken);
        if (documents.Count == 0 && request.Versions is null)
        {
            throw new MongoEventStoreException($"Aggregate '{aggregateId}' has no events, so there is no personal data to erase.");
        }

        var versionElement = EventDocumentFields<TAggregate>.Version;
        var requestedPaths = request.Paths is null ? null : new HashSet<String>(request.Paths, StringComparer.Ordinal);
        var entries = new List<PersonalDataErasureEntry>();
        var foundVersions = new HashSet<Int64>();
        var recordedPaths = new HashSet<String>(StringComparer.Ordinal);
        var lastSelectedVersion = 0L;

        foreach (var document in documents)
        {
            var version = document[versionElement].ToInt64();
            foundVersions.Add(version);
            lastSelectedVersion = version;

            var commitments = ReadCommitments(document, aggregateId, version);
            if (request.Versions is not null && commitments.Count == 0)
            {
                throw new MongoEventStoreException($"Version {version} of aggregate '{aggregateId}' has no recorded personal data.");
            }

            if (SelectEntry(version, commitments, requestedPaths, recordedPaths) is { } entry)
            {
                entries.Add(entry);
            }
        }

        EnsureRequestSatisfied(aggregateId, request, foundVersions, recordedPaths);
        return (entries, lastSelectedVersion);
    }
}
