// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using System.Text;

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
    /// time is not derivable from the events, because it records when the first event was appended, so the replay
    /// starts with the stored read model's creation time, as the rebuild after an erasure does, or with the earliest
    /// checkpoint's when the read model is missing; the states are compared without it, and
    /// <see cref="DerivedDataVerificationResult.CreationTimeConsistent"/> reports whether the
    /// read model and the checkpoints agree on it. The comparison uses the serialized documents, so it does not depend
    /// on equality members of the aggregate. A stored read model that cannot be read, for example because it repeats an
    /// element name, is reported as <see cref="ReadModelStatus.Differs"/>, and such a checkpoint, or one whose version
    /// is not an integer, as a mismatch of its hash and state. The chain does not show whether an event still
    /// deserializes, for example after a field's type was changed and the chain recomputed, or after an event type was
    /// removed: replay then stops at that event, the read model is reported as
    /// <see cref="ReadModelStatus.Differs"/>, or as <see cref="ReadModelStatus.Missing"/> when none is stored, and
    /// checkpoints the replay did not reach as state mismatches. An exception thrown by an event's <c>Execute</c>, such
    /// as a failed domain check in consumer code, propagates, as it does when the aggregate is loaded.
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
        // Read raw, so a document the driver cannot read, for example one that repeats an element name, is reported.
        var storedReadModel = await _mongoHelper.Database.GetCollection<BsonDocument>(_options.ReadModelCollectionName)
                                                .Find(session, idFilter)
                                                .As<RawBsonDocument>()
                                                .FirstOrDefaultAsync(cancellationToken);
        var readModel = storedReadModel is null ? null : TryPrepare(storedReadModel);
        var checkpointVersion = $"_id.{CheckpointIdElement(nameof(CheckpointId.Version))}";
        var storedCheckpoints = await _mongoHelper.Database.GetCollection<BsonDocument>(_options.CheckpointCollectionName)
                                                  .Find(session,
                                                        new BsonDocument($"_id.{CheckpointIdElement(nameof(CheckpointId.AggregateId))}",
                                                                         new BsonBinaryData(aggregateId, GuidRepresentation.Standard)))
                                                  .Sort(new BsonDocument(checkpointVersion, 1))
                                                  .As<RawBsonDocument>()
                                                  .ToListAsync(cancellationToken);
        var checkpoints = storedCheckpoints.Select(stored => TryPrepare(stored) is { } document
                                                       ? (Version: ReadCheckpointVersion(document), Document: document)
                                                       : (Version: TryReadCheckpointVersion(stored), Document: null))
                                           .ToList();
        var checkpointDocuments = checkpoints.Select(c => c.Document).OfType<BsonDocument>().ToList();

        // Every stored document is compared: a second document at a version, for example with its identifier's fields
        // in another order, must not share the first one's result.
        var checkpointsByVersion = checkpoints.Where(c => c.Version is not null)
                                              .GroupBy(c => c.Version.GetValueOrDefault())
                                              .ToDictionary(group => group.Key, group => group.Select(c => c.Document).OfType<BsonDocument>().ToList());
        var replay = await ReplayAsync(aggregateId, StoredCreationTime(readModel, checkpointDocuments), session, checkpointsByVersion, cancellationToken);

        var readModelStatus = (readModel, replay.HasState) switch
        {
            _ when replay.Stopped => storedReadModel is null ? ReadModelStatus.Missing : ReadModelStatus.Differs,
            (null, _) when storedReadModel is not null => ReadModelStatus.Differs,
            (null, false) => ReadModelStatus.Matches,
            (null, true) => ReadModelStatus.Missing,
            _ => DocumentsMatch(WithIdFirst(replay.State), readModel, replay.Version, replay.Revision) ? ReadModelStatus.Matches : ReadModelStatus.Differs
        };

        var hashes = new Dictionary<Int64, Byte[]>();
        foreach (var verified in stream.Events)
        {
            hashes.TryAdd(verified.Version, verified.Hash);
        }

        // A checkpoint document that cannot be read, or whose version cannot be read, counts as a mismatch of its hash
        // and state.
        var results = checkpoints.Select(checkpoint => (checkpoint.Document, checkpoint.Version) is ({ } document, { } version)
                                             ? new CheckpointVerification(version, VerifyHash(document, version, hashes), replay.CheckpointStates.GetValueOrDefault(document))
                                             : new CheckpointVerification(checkpoint.Version.GetValueOrDefault(), CheckpointHashStatus.Mismatch, false))
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
               ? StoredVersion.TryRead(storedRevision, out var storedRevisionValue) && storedRevisionValue == revision
               : LegacyRevision.Of(0, version) == revision);

    /// <summary>
    /// Reads a checkpoint's version with the strict rules for stored versions, or <c>null</c>: a value such as a
    /// fraction or a Decimal128 is no version, rather than one truncated or rejected by a lenient conversion.
    /// </summary>
    private static Int64? ReadCheckpointVersion(BsonDocument checkpoint)
        => checkpoint.TryGetValue("_id", out var id) &&
           id is BsonDocument idDocument &&
           idDocument.TryGetValue(CheckpointIdElement(nameof(CheckpointId.Version)), out var value) &&
           StoredVersion.TryRead(value, out var version)
            ? version
            : null;

    /// <summary>
    /// Gets the creation time replay starts with: the stored read model's, or the earliest checkpoint state's when the
    /// read model is missing, or the default when neither records one.
    /// </summary>
    private static DateTime StoredCreationTime(BsonDocument? readModel, IEnumerable<BsonDocument> checkpoints)
    {
        var createdUtcElement = ElementNameOf(nameof(IAggregate.CreatedUtc));
        var stateElement = CheckpointElement(nameof(CheckpointDocument<TAggregate>.State));
        var source = readModel ?? checkpoints.Select(checkpoint => checkpoint.GetValue(stateElement, BsonNull.Value)).OfType<BsonDocument>().FirstOrDefault();
        return source?.GetValue(createdUtcElement, BsonNull.Value) is BsonDateTime createdUtc ? createdUtc.ToUniversalTime() : default;
    }

    /// <summary>
    /// Prepares a stored document for comparison, or returns <c>null</c> when it cannot be read: it repeats an element
    /// name, holds a value the driver rejects, or is not valid UTF-8.
    /// </summary>
    private static BsonDocument? TryPrepare(BsonDocument document)
    {
        try
        {
            return StoredDocuments.Prepare(document);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the version of a checkpoint document that cannot be read as a whole, or <c>null</c>.
    /// </summary>
    private static Int64? TryReadCheckpointVersion(BsonDocument checkpoint)
    {
        try
        {
            return ReadCheckpointVersion(checkpoint);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or DecoderFallbackException)
        {
            return null;
        }
    }

    private static CheckpointHashStatus VerifyHash(BsonDocument checkpoint, Int64 version, Dictionary<Int64, Byte[]> hashes)
    {
        if (!checkpoint.TryGetValue(CheckpointDocument<TAggregate>.ChainHashElementName, out var recorded))
        {
            return CheckpointHashStatus.Unverifiable;
        }

        return recorded is BsonBinaryData { Bytes: var hash } && hashes.TryGetValue(version, out var verified) && verified.AsSpan().SequenceEqual(hash)
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
    /// Replays the state-changing events from the stream start, beginning with the given creation time, and compares
    /// the state with each checkpoint document when the replay reaches its version.
    /// </summary>
    private async Task<Replay> ReplayAsync(Guid aggregateId,
                                           DateTime createdUtc,
                                           IClientSessionHandle session,
                                           Dictionary<Int64, List<BsonDocument>> checkpoints,
                                           CancellationToken cancellationToken)
    {
        var aggregate = new TAggregate
        {
            Id = aggregateId,
            CreatedUtc = createdUtc
        };

        var eventFilter = Builders<Event<TAggregate>>.Filter;
        // Read raw through a cursor and deserialize one event at a time, so memory does not grow with the stream and an
        // event that no longer deserializes stops the replay instead of failing the check.
        using var cursor = await _mongoHelper.Database.GetCollection<Event<TAggregate>>(_options.EventsCollectionName)
                                             .Find(session,
                                                   eventFilter.Eq(e => e.AggregateId, aggregateId) &
                                                   EventDocumentFields<TAggregate>.ExcludeObservationalEvents(_options.ObservationalEventDiscriminators))
                                             .SortBy(e => e.Version)
                                             .As<RawBsonDocument>()
                                             .ToCursorAsync(cancellationToken);

        var checkpointStates = new Dictionary<BsonDocument, Boolean>(ReferenceEqualityComparer.Instance);
        var hasState = false;
        while (await cursor.MoveNextAsync(cancellationToken))
        {
            foreach (var storedEvent in cursor.Current)
            {
                Event<TAggregate> @event;
                try
                {
                    @event = BsonSerializer.Deserialize<Event<TAggregate>>(storedEvent);
                }
                catch (Exception ex) when (ex is FormatException or BsonException or InvalidOperationException or DecoderFallbackException)
                {
                    return new Replay(aggregate.ToBsonDocument(), hasState, aggregate.Version, aggregate.Revision, checkpointStates, true);
                }

                if (@event is ObservationalEvent<TAggregate>)
                {
                    continue;
                }

                LegacyRevision.Normalize(@event);
                @event.Execute(aggregate);
                aggregate.Version = @event.Version;
                aggregate.Revision = @event.Revision;
                hasState = true;

                if (checkpoints.TryGetValue(@event.Version, out var documents))
                {
                    var state = aggregate.ToBsonDocument();
                    foreach (var checkpoint in documents)
                    {
                        checkpointStates[checkpoint] = MatchesCheckpoint(state, checkpoint, aggregate.Version, aggregate.Revision);
                    }
                }
            }
        }

        return new Replay(aggregate.ToBsonDocument(), hasState, aggregate.Version, aggregate.Revision, checkpointStates, false);
    }

    /// <summary>
    /// The state replayed from the stream start with its version and revision, whether any state-changing event was
    /// replayed, whether each checkpoint document's state matched when the replay reached its version, and whether
    /// the replay stopped at an event that could not be deserialized.
    /// </summary>
    private sealed record Replay(
        BsonDocument State,
        Boolean HasState,
        Int64 Version,
        Int64 Revision,
        Dictionary<BsonDocument, Boolean> CheckpointStates,
        Boolean Stopped);
}
