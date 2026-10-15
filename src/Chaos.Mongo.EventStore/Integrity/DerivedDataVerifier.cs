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
    /// Verifies the stream's chain and, when it is intact, replays the stream from its start and compares the stored
    /// read model and the state of every checkpoint with the replayed state, and every checkpoint's recorded chain
    /// hash with the event at its version.
    /// </summary>
    /// <remarks>
    /// The replay never starts from a checkpoint, because checkpoints are the data under test. Fields that replay does
    /// not produce, such as the read model's creation time, are taken from the stored read model, as an erasure
    /// rebuild does. The comparison uses the serialized documents, so it does not depend on equality members of the
    /// aggregate.
    /// </remarks>
    /// <param name="aggregateId">The aggregate identifier.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The result.</returns>
    public async Task<DerivedDataVerificationResult> VerifyAsync(Guid aggregateId, CancellationToken cancellationToken = default)
    {
        var stream = await _streams.VerifyStreamAsync(aggregateId, cancellationToken);
        if (!stream.IsIntact)
        {
            return new DerivedDataVerificationResult(stream, ReadModelStatus.NotChecked, []);
        }

        var idFilter = new BsonDocument("_id", new BsonBinaryData(aggregateId, GuidRepresentation.Standard));
        var readModel = await _mongoHelper.Database.GetCollection<BsonDocument>(_options.ReadModelCollectionName)
                                          .Find(idFilter)
                                          .FirstOrDefaultAsync(cancellationToken);
        var checkpointVersion = $"_id.{CheckpointIdElement(nameof(CheckpointId.Version))}";
        var checkpoints = await _mongoHelper.Database.GetCollection<BsonDocument>(_options.CheckpointCollectionName)
                                            .Find(new BsonDocument($"_id.{CheckpointIdElement(nameof(CheckpointId.AggregateId))}",
                                                                   new BsonBinaryData(aggregateId, GuidRepresentation.Standard)))
                                            .Sort(new BsonDocument(checkpointVersion, 1))
                                            .ToListAsync(cancellationToken);

        var checkpointsByVersion = checkpoints.Where(c => TryReadCheckpointVersion(c, out _))
                                              .GroupBy(c => ReadCheckpointVersion(c))
                                              .ToDictionary(group => group.Key, group => group.First());
        var replay = await ReplayAsync(aggregateId, readModel, checkpointsByVersion, cancellationToken);

        var readModelStatus = (readModel, replay.HasState) switch
        {
            (null, false) => ReadModelStatus.Matches,
            (null, true) => ReadModelStatus.Missing,
            _ => DocumentsMatch(WithIdFirst(replay.State), readModel) ? ReadModelStatus.Matches : ReadModelStatus.Differs
        };

        var results = checkpoints.Select(checkpoint =>
                                 {
                                     var hasVersion = TryReadCheckpointVersion(checkpoint, out var version);
                                     return new CheckpointVerification(version,
                                                                       VerifyHash(checkpoint, hasVersion ? version : 0, stream),
                                                                       hasVersion && replay.CheckpointStates.GetValueOrDefault(version));
                                 })
                                 .ToList();
        return new DerivedDataVerificationResult(stream, readModelStatus, results);
    }

    private static String CheckpointElement(String memberName) => ElementNameOf(typeof(CheckpointDocument<TAggregate>), memberName);

    private static String CheckpointIdElement(String memberName) => ElementNameOf(typeof(CheckpointId), memberName);

    /// <summary>
    /// Compares a replayed state with a stored one. A stored document written before revisions existed lacks the
    /// revision, which is then left out of the comparison.
    /// </summary>
    private static Boolean DocumentsMatch(BsonDocument replayed, BsonDocument stored)
    {
        var expected = replayed.DeepClone().AsBsonDocument;
        var revisionElement = ElementNameOf(nameof(IAggregate.Revision));
        if (!stored.Contains(revisionElement))
        {
            expected.Remove(revisionElement);
        }

        return expected.Equals(stored);
    }

    private static String ElementNameOf(String memberName) => ElementNameOf(typeof(TAggregate), memberName);

    private static String ElementNameOf(Type type, String memberName)
        => BsonClassMap.LookupClassMap(type).AllMemberMaps.FirstOrDefault(m => m.MemberName == memberName)?.ElementName ?? memberName;

    private static Boolean MatchesCheckpoint(BsonDocument state, BsonDocument checkpoint, Int64 revision)
        => checkpoint.TryGetValue(CheckpointElement(nameof(CheckpointDocument<TAggregate>.State)), out var stored) &&
           stored is BsonDocument storedState &&
           DocumentsMatch(state, storedState) &&
           (!checkpoint.TryGetValue(CheckpointElement(nameof(CheckpointDocument<TAggregate>.Revision)), out var storedRevision) ||
            (storedRevision.IsNumeric && storedRevision.ToInt64() == revision));

    private static Int64 ReadCheckpointVersion(BsonDocument checkpoint)
        => TryReadCheckpointVersion(checkpoint, out var version) ? version : 0;

    /// <summary>
    /// Serializes the replayed state with the creation time of the stored read model, or, without one, with the
    /// creation time of the checkpoint it is compared with.
    /// </summary>
    private static BsonDocument Serialize(TAggregate aggregate, BsonValue? createdUtc, String createdUtcElement, BsonDocument? checkpoint)
    {
        var document = aggregate.ToBsonDocument();
        var creationTime = createdUtc ??
                           (checkpoint is not null &&
                            checkpoint.TryGetValue(CheckpointElement(nameof(CheckpointDocument<TAggregate>.State)), out var state) &&
                            state is BsonDocument stateDocument &&
                            stateDocument.TryGetValue(createdUtcElement, out var checkpointCreatedUtc)
                               ? checkpointCreatedUtc
                               : null);
        if (creationTime is not null && document.Contains(createdUtcElement))
        {
            document[createdUtcElement] = creationTime;
        }

        return document;
    }

    private static Boolean TryReadCheckpointVersion(BsonDocument checkpoint, out Int64 version)
    {
        if (checkpoint.TryGetValue("_id", out var id) &&
            id is BsonDocument idDocument &&
            idDocument.TryGetValue(CheckpointIdElement(nameof(CheckpointId.Version)), out var value) &&
            value.IsNumeric)
        {
            version = value.ToInt64();
            return true;
        }

        version = 0;
        return false;
    }

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
                                           BsonDocument? readModel,
                                           Dictionary<Int64, BsonDocument> checkpoints,
                                           CancellationToken cancellationToken)
    {
        var createdUtcElement = ElementNameOf(nameof(IAggregate.CreatedUtc));
        var createdUtc = readModel is not null && readModel.TryGetValue(createdUtcElement, out var stored) ? stored : null;
        var aggregate = new TAggregate
        {
            Id = aggregateId
        };

        var eventFilter = Builders<Event<TAggregate>>.Filter;
        var events = await _mongoHelper.Database.GetCollection<Event<TAggregate>>(_options.EventsCollectionName)
                                       .Find(eventFilter.Eq(e => e.AggregateId, aggregateId) &
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
                checkpointStates[@event.Version] = MatchesCheckpoint(Serialize(aggregate, createdUtc, createdUtcElement, checkpoint), checkpoint, aggregate.Revision);
            }
        }

        return new Replay(Serialize(aggregate, createdUtc, createdUtcElement, null), hasState, checkpointStates);
    }

    /// <summary>
    /// The state replayed from the stream start, whether any state-changing event was replayed, and whether each
    /// checkpoint's state matched when the replay reached its version.
    /// </summary>
    private sealed record Replay(BsonDocument State, Boolean HasState, Dictionary<Int64, Boolean> CheckpointStates);
}
