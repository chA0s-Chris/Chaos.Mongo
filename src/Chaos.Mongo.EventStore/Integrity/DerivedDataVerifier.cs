// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

/// <summary>
/// Compares a stream's derived data, its read model and checkpoints, with the stream. The chain does not cover derived
/// data, so it is checked by replay and compare, and checkpoints are also validated against their recorded chain hash.
/// </summary>
/// <typeparam name="TAggregate">The aggregate type.</typeparam>
internal sealed class DerivedDataVerifier<TAggregate> where TAggregate : class, IAggregate, new()
{
    private readonly IMongoHelper _mongoHelper;
    private readonly MongoEventStoreOptions<TAggregate> _options;
    private readonly EventStreamVerifier<TAggregate> _streams;

    public DerivedDataVerifier(IMongoHelper mongoHelper, MongoEventStoreOptions<TAggregate> options, EventStreamVerifier<TAggregate> streams)
    {
        ArgumentNullException.ThrowIfNull(mongoHelper);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(streams);
        _mongoHelper = mongoHelper;
        _options = options;
        _streams = streams;
    }

    /// <summary>
    /// Gets or sets a callback that runs after the stream was verified and before its derived data is read, so tests
    /// can write concurrently.
    /// </summary>
    internal Func<Task>? StreamVerified { get; set; }

    /// <summary>
    /// Verifies the stream's chain and, when it is intact, replays the stream from its start and compares the stored
    /// read model and the state of every checkpoint with the replayed state, and every checkpoint's recorded chain
    /// hash with the event at its version.
    /// </summary>
    /// <remarks>
    /// Every read happens in one snapshot session, so appends and erasures that commit during the check do not show
    /// as differences; snapshot reads need MongoDB 5.0 or later and must finish within the server's snapshot history
    /// window. The replay never starts from a checkpoint, because checkpoints are the data under test. The creation
    /// time is not derivable from the events, because it records when the first event was appended, so the states are
    /// compared without it, and <see cref="DerivedDataVerificationResult.CreationTimeConsistent"/> reports whether the
    /// read model and the checkpoints agree on it. The comparison uses the serialized documents, so it does not depend
    /// on equality members of the aggregate.
    /// </remarks>
    /// <param name="aggregateId">The aggregate identifier.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The result.</returns>
    public async Task<DerivedDataVerificationResult> VerifyAsync(Guid aggregateId, CancellationToken cancellationToken = default)
    {
        using var session = await _mongoHelper.Client.StartSessionAsync(new ClientSessionOptions
                                                                        {
                                                                            Snapshot = true
                                                                        },
                                                                        cancellationToken);
        var stream = await _streams.VerifyStreamAsync(aggregateId, session, cancellationToken);
        if (StreamVerified is { } streamVerified)
        {
            await streamVerified();
        }

        if (!stream.IsIntact)
        {
            return new DerivedDataVerificationResult(stream, ReadModelStatus.NotChecked, [], true);
        }

        var idFilter = new BsonDocument("_id", new BsonBinaryData(aggregateId, GuidRepresentation.Standard));
        var readModel = await _mongoHelper.Database.GetCollection<BsonDocument>(_options.ReadModelCollectionName)
                                          .Find(session, idFilter)
                                          .FirstOrDefaultAsync(cancellationToken);
        var checkpointVersion = $"_id.{CheckpointIdElement(nameof(CheckpointId.Version))}";
        var checkpointDocuments = await _mongoHelper.Database.GetCollection<BsonDocument>(_options.CheckpointCollectionName)
                                                    .Find(session,
                                                          new BsonDocument($"_id.{CheckpointIdElement(nameof(CheckpointId.AggregateId))}",
                                                                           new BsonBinaryData(aggregateId, GuidRepresentation.Standard)))
                                                    .Sort(new BsonDocument(checkpointVersion, 1))
                                                    .ToListAsync(cancellationToken);
        var checkpoints = checkpointDocuments.Select(document => (Version: ReadCheckpointVersion(document), Document: document)).ToList();

        var checkpointsByVersion = checkpoints.Where(c => c.Version is not null)
                                              .GroupBy(c => c.Version.GetValueOrDefault())
                                              .ToDictionary(group => group.Key, group => group.First().Document);
        var replay = await ReplayAsync(aggregateId, session, checkpointsByVersion, cancellationToken);

        var readModelStatus = (readModel, replay.HasState) switch
        {
            (null, false) => ReadModelStatus.Matches,
            (null, true) => ReadModelStatus.Missing,
            _ => DocumentsMatch(WithIdFirst(replay.State), readModel, replay.Version, replay.Revision) ? ReadModelStatus.Matches : ReadModelStatus.Differs
        };

        var results = checkpoints.Select(checkpoint => new CheckpointVerification(checkpoint.Version.GetValueOrDefault(),
                                                                                  VerifyHash(checkpoint.Document, checkpoint.Version.GetValueOrDefault(), stream),
                                                                                  checkpoint.Version is { } version &&
                                                                                  replay.CheckpointStates.GetValueOrDefault(version)))
                                 .ToList();
        return new DerivedDataVerificationResult(stream, readModelStatus, results, CreationTimesAgree(readModel, checkpointDocuments));
    }

    private static String CheckpointElement(String memberName) => ElementNameOf(typeof(CheckpointDocument<TAggregate>), memberName);

    private static String CheckpointIdElement(String memberName) => ElementNameOf(typeof(CheckpointId), memberName);

    /// <summary>
    /// Gets a value indicating whether the stored read model and the states of the checkpoints record the same
    /// creation time.
    /// </summary>
    private static Boolean CreationTimesAgree(BsonDocument? readModel, IEnumerable<BsonDocument> checkpoints)
    {
        var createdUtcElement = ElementNameOf(nameof(IAggregate.CreatedUtc));
        var stateElement = CheckpointElement(nameof(CheckpointDocument<TAggregate>.State));
        var states = checkpoints.Select(checkpoint => checkpoint.GetValue(stateElement, BsonNull.Value)).OfType<BsonDocument>();
        return (readModel is null ? states : states.Prepend(readModel)).Select(state => state.GetValue(createdUtcElement, BsonNull.Value))
                                                                       .Distinct()
                                                                       .Count() <= 1;
    }

    /// <summary>
    /// Compares a replayed state with a stored one, both without the creation time, which replay does not produce. A
    /// stored document without a revision is accepted only when it could have been written before revisions existed,
    /// that is when its version equals the replayed revision, so removing the revision does not hide a difference.
    /// </summary>
    private static Boolean DocumentsMatch(BsonDocument replayed, BsonDocument stored, Int64 version, Int64 revision)
    {
        var createdUtcElement = ElementNameOf(nameof(IAggregate.CreatedUtc));
        var expected = replayed.DeepClone().AsBsonDocument;
        var actual = stored.DeepClone().AsBsonDocument;
        expected.Remove(createdUtcElement);
        actual.Remove(createdUtcElement);
        var revisionElement = ElementNameOf(nameof(IAggregate.Revision));
        if (!actual.Contains(revisionElement))
        {
            if (LegacyRevision.Of(0, version) != revision)
            {
                return false;
            }

            expected.Remove(revisionElement);
        }

        return expected.Equals(actual);
    }

    private static String ElementNameOf(String memberName) => ElementNameOf(typeof(TAggregate), memberName);

    private static String ElementNameOf(Type type, String memberName)
        => BsonClassMap.LookupClassMap(type).AllMemberMaps.FirstOrDefault(m => m.MemberName == memberName)?.ElementName ?? memberName;

    private static Boolean MatchesCheckpoint(BsonDocument state, BsonDocument checkpoint, Int64 version, Int64 revision)
        => checkpoint.TryGetValue(CheckpointElement(nameof(CheckpointDocument<TAggregate>.State)), out var stored) &&
           stored is BsonDocument storedState &&
           DocumentsMatch(state, storedState, version, revision) &&
           (checkpoint.TryGetValue(CheckpointElement(nameof(CheckpointDocument<TAggregate>.Revision)), out var storedRevision)
               ? storedRevision.IsNumeric && storedRevision.ToInt64() == revision
               : LegacyRevision.Of(0, version) == revision);

    private static Int64? ReadCheckpointVersion(BsonDocument checkpoint)
        => checkpoint.TryGetValue("_id", out var id) &&
           id is BsonDocument idDocument &&
           idDocument.TryGetValue(CheckpointIdElement(nameof(CheckpointId.Version)), out var value) &&
           value.IsNumeric
            ? value.ToInt64()
            : null;

    private static CheckpointHashStatus VerifyHash(BsonDocument checkpoint, Int64 version, StreamVerificationResult stream)
    {
        if (!checkpoint.TryGetValue(CheckpointDocument<TAggregate>.ChainHashElementName, out var recorded))
        {
            return CheckpointHashStatus.Unverifiable;
        }

        var verified = stream.Events.FirstOrDefault(e => e.Version == version);
        return recorded is BsonBinaryData { Bytes: var hash } && verified is not null && verified.Hash.AsSpan().SequenceEqual(hash)
            ? CheckpointHashStatus.Matches
            : CheckpointHashStatus.Mismatch;
    }

    /// <summary>
    /// Moves <c>_id</c> to the front, as the server does when it stores a top-level document.
    /// </summary>
    private static BsonDocument WithIdFirst(BsonDocument document)
    {
        if (!document.TryGetElement("_id", out var id))
        {
            return document;
        }

        var reordered = new BsonDocument(id);
        reordered.AddRange(document.Elements.Where(element => element.Name != "_id"));
        return reordered;
    }

    /// <summary>
    /// Replays the state-changing events from the stream start and compares the state with each checkpoint when the
    /// replay reaches its version.
    /// </summary>
    private async Task<Replay> ReplayAsync(Guid aggregateId,
                                           IClientSessionHandle session,
                                           Dictionary<Int64, BsonDocument> checkpoints,
                                           CancellationToken cancellationToken)
    {
        var aggregate = new TAggregate
        {
            Id = aggregateId
        };

        var eventFilter = Builders<Event<TAggregate>>.Filter;
        var events = await _mongoHelper.Database.GetCollection<Event<TAggregate>>(_options.EventsCollectionName)
                                       .Find(session,
                                             eventFilter.Eq(e => e.AggregateId, aggregateId) &
                                             EventDocumentFields<TAggregate>.ExcludeObservationalEvents(_options.ObservationalEventDiscriminators))
                                       .SortBy(e => e.Version)
                                       .ToListAsync(cancellationToken);

        var checkpointStates = new Dictionary<Int64, Boolean>();
        var hasState = false;
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
            hasState = true;

            if (checkpoints.TryGetValue(@event.Version, out var checkpoint))
            {
                checkpointStates[@event.Version] = MatchesCheckpoint(aggregate.ToBsonDocument(), checkpoint, aggregate.Version, aggregate.Revision);
            }
        }

        return new Replay(aggregate.ToBsonDocument(), hasState, aggregate.Version, aggregate.Revision, checkpointStates);
    }

    /// <summary>
    /// The state replayed from the stream start with its version and revision, whether any state-changing event was
    /// replayed, and whether each checkpoint's state matched when the replay reached its version.
    /// </summary>
    private sealed record Replay(BsonDocument State, Boolean HasState, Int64 Version, Int64 Revision, Dictionary<Int64, Boolean> CheckpointStates);
}
