// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using MongoDB.Bson;
using MongoDB.Driver;
using System.Globalization;

/// <summary>
/// Verifies the hash chain of an event stream by recomputing it from the raw stored documents, compares it with the
/// stream's leaves in completed anchors, and verifies that single events are anchored.
/// </summary>
/// <typeparam name="TAggregate">The aggregate type.</typeparam>
internal sealed class EventStreamVerifier<TAggregate> where TAggregate : class, IAggregate, new()
{
    private readonly String _aggregateTypeName = typeof(TAggregate).Name;
    private readonly AnchorVerifier? _anchors;
    private readonly IMongoHelper _mongoHelper;
    private readonly MongoEventStoreOptions<TAggregate> _options;

    public EventStreamVerifier(IMongoHelper mongoHelper, MongoEventStoreOptions<TAggregate> options, AnchorVerifier? anchors = null)
    {
        ArgumentNullException.ThrowIfNull(mongoHelper);
        ArgumentNullException.ThrowIfNull(options);
        _mongoHelper = mongoHelper;
        _options = options;
        _anchors = anchors;
    }

    /// <summary>
    /// Verifies that an event is anchored: the chain from the event to the version of the first completed anchor that
    /// covers it, the anchor's leaf and inclusion proof, the anchor's digest and its stored timestamp tokens.
    /// </summary>
    /// <param name="aggregateId">The aggregate identifier.</param>
    /// <param name="version">The event version.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The result.</returns>
    public async Task<EventAnchorVerificationResult> VerifyEventAnchorAsync(Guid aggregateId, Int64 version, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        if (_anchors is null)
        {
            return EventAnchorVerificationResult.NotChecked;
        }

        var leaves = await _anchors.Store.FindCompletedStreamLeavesAsync(_aggregateTypeName, aggregateId, version, cancellationToken);
        if (leaves.Count == 0)
        {
            return EventAnchorVerificationResult.NotAnchored;
        }

        var (leaf, anchor) = leaves[0];
        var reference = new AnchorReference(anchor.Id, anchor.Sequence);
        var anchoredHash = await VerifyChainToAsync(aggregateId, version, leaf.Version, cancellationToken);
        if (anchoredHash is null)
        {
            return Failed(EventAnchorFailure.ChainBroken, []);
        }

        if (!anchoredHash.AsSpan().SequenceEqual(leaf.Hash))
        {
            return Failed(EventAnchorFailure.LeafHashMismatch, []);
        }

        var leafHash = MerkleTree.HashLeaf(AnchorEncoding.EncodeLeaf(new AnchorCandidate(_aggregateTypeName, aggregateId, leaf.Version, anchoredHash, default)));
        if (!MerkleTree.VerifyInclusion(leafHash, leaf.Index, anchor.LeafCount, leaf.Proof, anchor.MerkleRoot))
        {
            return Failed(EventAnchorFailure.InclusionProofInvalid, []);
        }

        var anchorResult = await _anchors.VerifyAnchorAsync(anchor, cancellationToken);
        if (!anchorResult.DigestIsValid)
        {
            return Failed(EventAnchorFailure.AnchorDigestMismatch, anchorResult.Timestamps);
        }

        return anchorResult.IsVerified
            ? new EventAnchorVerificationResult(EventAnchorStatus.Anchored, null, reference, leaf.Version, anchorResult.Timestamps)
            : Failed(EventAnchorFailure.NoValidTimestamp, anchorResult.Timestamps);

        EventAnchorVerificationResult Failed(EventAnchorFailure failure, IReadOnlyList<TimestampVerificationResult> timestamps)
            => new(EventAnchorStatus.Failed, failure, reference, leaf.Version, timestamps);
    }

    /// <summary>
    /// Recomputes the hash chain of a stream and reports the first broken version. For every verified version it
    /// returns the integrity data and the sealing anchor. For an intact stream it also compares the stream with its
    /// leaves in completed anchors and reports erased values that no later erasure event of the stream records.
    /// </summary>
    /// <remarks>
    /// A consistent chain does not reveal deleted newest events or a deleted stream, because the remaining events still
    /// form a valid chain. Only the comparison with anchored leaves detects that, for anchored versions.
    /// </remarks>
    /// <param name="aggregateId">The aggregate identifier.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The verification result.</returns>
    public async Task<StreamVerificationResult> VerifyStreamAsync(Guid aggregateId, CancellationToken cancellationToken = default)
    {
        var scan = await ScanChainAsync(aggregateId, cancellationToken);
        var leaves = _anchors is null
            ? []
            : await _anchors.Store.FindCompletedStreamLeavesAsync(_aggregateTypeName, aggregateId, 1, cancellationToken);
        var events = BuildVerifiedEvents(scan, leaves);
        var transitions = FindSealModeTransitions(scan.Events);

        StreamVerificationResult result;
        if (scan is { BrokenVersion: { } brokenVersion, Failure: { } failure })
        {
            result = StreamVerificationResult.Broken(brokenVersion, failure);
        }
        else if (CompareWithLeaves(scan.Events, leaves) is { } anchoredBreak)
        {
            result = StreamVerificationResult.Broken(anchoredBreak.Version, anchoredBreak.Failure);
        }
        else
        {
            var unrecorded = scan.Events.SelectMany(e => e.PersonalData.Where(p => p.Erased).Select(p => new UnrecordedErasure(e.Version, p.Path)))
                                 .Where(erasure => !scan.RecordedErasures.Contains(erasure))
                                 .ToList();
            result = StreamVerificationResult.IntactWith(unrecorded);
        }

        return result with
        {
            Events = events,
            SealModeTransitions = transitions,
            AnchorCoverageChecked = _anchors is not null
        };
    }

    /// <summary>
    /// Assigns each verified event its sealing anchor: the completed anchor with the lowest sequence number whose leaf
    /// for the stream has a version at or above the event's.
    /// </summary>
    private static List<VerifiedEvent> BuildVerifiedEvents(ChainScan scan, IReadOnlyList<CompletedStreamLeaf> leaves)
    {
        var byVersionDescending = leaves.OrderByDescending(leaf => leaf.Leaf.Version).ToList();
        var anchors = new AnchorReference?[scan.Events.Count];
        CompletedStreamLeaf? best = null;
        var next = 0;
        for (var index = scan.Events.Count - 1; index >= 0; index--)
        {
            var version = scan.Events[index].Version;
            while (next < byVersionDescending.Count && byVersionDescending[next].Leaf.Version >= version)
            {
                var candidate = byVersionDescending[next++];
                best = best is null || candidate.Anchor.Sequence < best.Anchor.Sequence ? candidate : best;
            }

            anchors[index] = best is null ? null : new AnchorReference(best.Anchor.Id, best.Anchor.Sequence);
        }

        return scan.Events.Select((e, index) => new VerifiedEvent(
                                      e.Version,
                                      e.Hash,
                                      e.PreviousHash,
                                      e.SealMode,
                                      anchors[index],
                                      e.PersonalData.Select(p => new PersonalDataPathStatus(
                                                                p.Path,
                                                                !p.Erased
                                                                    ? PersonalDataStatus.Present
                                                                    : scan.RecordedErasures.Contains(new UnrecordedErasure(e.Version, p.Path))
                                                                        ? PersonalDataStatus.ErasedWithRecord
                                                                        : PersonalDataStatus.ErasedWithoutRecord))
                                       .ToList()))
                   .ToList();
    }

    /// <summary>
    /// Reads the <c>(Version, Path)</c> records of an erasure event from its raw document. Only records of earlier
    /// versions document an erasure. Entries that do not have the pinned shape are ignored.
    /// </summary>
    private static void CollectErasureRecords(RawBsonDocument erasureEvent, Int64 erasureVersion, HashSet<UnrecordedErasure> recordedErasures)
    {
        if (!erasureEvent.TryGetValue(PersonalDataErased<TAggregate>.EntriesElement, out var entriesValue) ||
            entriesValue is not BsonArray entries)
        {
            return;
        }

        foreach (var entryValue in entries)
        {
            if (entryValue is not BsonDocument entry ||
                !entry.TryGetValue(PersonalDataErasureEntry.VersionElement, out var versionValue) ||
                !TryReadVersion(versionValue, out var version) ||
                version >= erasureVersion ||
                !entry.TryGetValue(PersonalDataErasureEntry.PathsElement, out var pathsValue) ||
                pathsValue is not BsonArray paths)
            {
                continue;
            }

            foreach (var path in paths)
            {
                if (path is BsonString { Value: var erasedPath })
                {
                    recordedErasures.Add(new UnrecordedErasure(version, erasedPath));
                }
            }
        }
    }

    /// <summary>
    /// Compares an intact chain with the stream's leaves in completed anchors: an anchored version that no longer
    /// exists reveals truncation, and an anchored version whose stored hash differs from its leaf reveals a rewritten or
    /// rolled-back stream.
    /// </summary>
    /// <returns>The lowest broken version and why, or <c>null</c> when every anchored version matches.</returns>
    private static (Int64 Version, StreamVerificationFailure Failure)? CompareWithLeaves(IReadOnlyList<ScannedEvent> events,
                                                                                         IReadOnlyList<CompletedStreamLeaf> leaves)
    {
        (Int64 Version, StreamVerificationFailure Failure)? first = null;
        foreach (var (leaf, _) in leaves)
        {
            (Int64 Version, StreamVerificationFailure Failure)? finding = leaf.Version > events.Count
                ? (events.Count + 1, StreamVerificationFailure.AnchoredVersionMissing)
                : !events[(Int32)(leaf.Version - 1)].Hash.AsSpan().SequenceEqual(leaf.Hash)
                    ? (leaf.Version, StreamVerificationFailure.AnchoredHashMismatch)
                    : null;
            if (finding is { } found && (first is null || found.Version < first.Value.Version))
            {
                first = found;
            }
        }

        return first;
    }

    /// <summary>
    /// Finds the versions at which the seal mode changes from <see cref="IntegritySealMode.Append"/> to
    /// <see cref="IntegritySealMode.Retroactive"/>. Such a transition is either a legitimate gap in protection or a
    /// suffix re-sealed after tampering; only anchors tell them apart.
    /// </summary>
    private static List<Int64> FindSealModeTransitions(IReadOnlyList<ScannedEvent> events)
        => events.Skip(1)
                 .Where((e, index) => e.SealMode == IntegritySealMode.Retroactive && events[index].SealMode == IntegritySealMode.Append)
                 .Select(e => e.Version)
                 .ToList();

    /// <summary>
    /// Reads a stored version without throwing on tampered values. Only whole numbers within the
    /// <see cref="Int64"/> range are versions.
    /// </summary>
    private static Boolean TryReadVersion(BsonValue value, out Int64 version)
    {
        // 2^63, the first double above Int64.MaxValue.
        const Double int64Limit = 9223372036854775808d;

        switch (value)
        {
            case BsonInt32 int32:
                version = int32.Value;
                return true;
            case BsonInt64 int64:
                version = int64.Value;
                return true;
            case BsonDouble { Value: var number } when Double.IsInteger(number) && number >= -int64Limit && number < int64Limit:
                version = (Int64)number;
                return true;
            case BsonDecimal128 decimal128:
                return Int64.TryParse(decimal128.Value.ToString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out version);
            default:
                version = 0;
                return false;
        }
    }

    /// <summary>
    /// Verifies one event chained from an expected previous hash.
    /// </summary>
    private static EventCheck VerifyEvent(Byte[] document, BsonValue? integrityValue, Byte[] expectedPreviousHash)
    {
        if (integrityValue is null)
        {
            return EventCheck.Failed(StreamVerificationFailure.NotSealed);
        }

        if (!EventIntegrityChain.TryRead(integrityValue, out var integrity) || !Enum.IsDefined(integrity.SealMode))
        {
            return EventCheck.Failed(StreamVerificationFailure.MalformedIntegrity);
        }

        if (integrity.FormatVersion != EventIntegrityChain.FormatVersion ||
            !String.Equals(integrity.Algorithm, EventIntegrityChain.Algorithm, StringComparison.Ordinal))
        {
            return EventCheck.Failed(StreamVerificationFailure.UnsupportedFormat);
        }

        if (!integrity.PreviousHash.AsSpan().SequenceEqual(expectedPreviousHash))
        {
            return EventCheck.Failed(StreamVerificationFailure.PreviousHashMismatch);
        }

        var storedDocument = RawBsonElements.RemoveTopLevelElement(document, EventIntegrityChain.ElementName);
        var canonicalDocument = storedDocument;
        var personalData = new List<(String Path, Boolean Erased)>();
        StreamVerificationFailure? personalDataFailure = null;

        // Only the recorded paths take part, so changing or removing marks later does not affect sealed events.
        if (integrity.PersonalData is { } commitments)
        {
            if (!PersonalDataCommitments.IsWellFormed(commitments))
            {
                return EventCheck.Failed(StreamVerificationFailure.MalformedIntegrity);
            }

            foreach (var commitment in commitments)
            {
                switch (PersonalDataCommitments.Verify(storedDocument, commitment))
                {
                    case PersonalDataVerification.Unresolved:
                        return EventCheck.Failed(StreamVerificationFailure.MalformedIntegrity);
                    case PersonalDataVerification.Mismatch:
                        personalDataFailure ??= StreamVerificationFailure.PersonalDataMismatch;
                        break;
                    case PersonalDataVerification.NotErased:
                        personalDataFailure ??= StreamVerificationFailure.ErasedPersonalDataPresent;
                        break;
                    case PersonalDataVerification.Erased:
                        personalData.Add((commitment.Path, true));
                        break;
                    case PersonalDataVerification.Committed:
                        personalData.Add((commitment.Path, false));
                        break;
                }
            }

            canonicalDocument = PersonalDataCommitments.Canonicalize(storedDocument, commitments);
        }

        var recomputedHash = EventIntegrityChain.ComputeHash(canonicalDocument, integrity.PersonalData, expectedPreviousHash, integrity.SealMode);
        if (!recomputedHash.AsSpan().SequenceEqual(integrity.Hash))
        {
            return EventCheck.Failed(StreamVerificationFailure.HashMismatch);
        }

        return personalDataFailure is { } failure
            ? EventCheck.Failed(failure)
            : new EventCheck(null, integrity, personalData);
    }

    private IMongoCollection<RawBsonDocument> GetEventsCollection()
        => _mongoHelper.Database.GetCollection<RawBsonDocument>(_options.EventsCollectionName);

    /// <summary>
    /// Recomputes the chain of a stream from version 1 until the first broken version or the end of the stream.
    /// </summary>
    private async Task<ChainScan> ScanChainAsync(Guid aggregateId, CancellationToken cancellationToken)
    {
        var versionElement = EventDocumentFields<TAggregate>.Version;
        var discriminatorElement = EventDocumentFields<TAggregate>.Discriminator;
        using var cursor = await GetEventsCollection().Find(EventDocumentFields<TAggregate>.ForAggregate<RawBsonDocument>(aggregateId))
                                                      .Sort(new BsonDocument(versionElement, 1))
                                                      .ToCursorAsync(cancellationToken);

        var scan = new ChainScan();
        Byte[]? previousHash = null;
        while (await cursor.MoveNextAsync(cancellationToken))
        {
            foreach (var rawDocument in cursor.Current)
            {
                using (rawDocument)
                {
                    var expectedVersion = scan.Events.Count + 1L;
                    if (!rawDocument.TryGetValue(versionElement, out var versionValue) ||
                        !TryReadVersion(versionValue, out var version) ||
                        version != expectedVersion)
                    {
                        return scan.Break(expectedVersion, StreamVerificationFailure.VersionGap);
                    }

                    // The genesis value comes from the aggregate type stored in version 1, not from the class name.
                    if (previousHash is null)
                    {
                        if (!EventDocumentFields<TAggregate>.TryReadAggregateType(rawDocument, out var aggregateType))
                        {
                            return scan.Break(expectedVersion, StreamVerificationFailure.MissingAggregateType);
                        }

                        previousHash = EventIntegrityChain.ComputeGenesis(aggregateType, aggregateId);
                    }

                    rawDocument.TryGetValue(EventIntegrityChain.ElementName, out var integrityValue);
                    var check = VerifyEvent(rawDocument.ToBson(), integrityValue, previousHash);
                    if (check.Integrity is not { } integrity)
                    {
                        return scan.Break(expectedVersion, check.Failure ?? StreamVerificationFailure.MalformedIntegrity);
                    }

                    scan.Events.Add(new ScannedEvent(expectedVersion, integrity.Hash, integrity.PreviousHash, integrity.SealMode, check.PersonalData));

                    // The erasure event is identified by its reserved discriminator; its records are trusted once its own hash verified.
                    if (rawDocument.TryGetValue(discriminatorElement, out var discriminator) &&
                        EventDocumentFields<TAggregate>.IsErasureEvent(discriminator))
                    {
                        CollectErasureRecords(rawDocument, expectedVersion, scan.RecordedErasures);
                    }

                    previousHash = integrity.Hash;
                }
            }
        }

        return scan;
    }

    /// <summary>
    /// Verifies the chain from an event up to a later version, starting from the event's stored previous hash.
    /// </summary>
    /// <returns>The hash of <paramref name="toVersion"/>, or <c>null</c> when a version is missing or does not verify.</returns>
    private async Task<Byte[]?> VerifyChainToAsync(Guid aggregateId, Int64 fromVersion, Int64 toVersion, CancellationToken cancellationToken)
    {
        var versionElement = EventDocumentFields<TAggregate>.Version;
        var filter = new BsonDocument
        {
            { EventDocumentFields<TAggregate>.AggregateId, new BsonBinaryData(aggregateId, GuidRepresentation.Standard) },
            {
                versionElement, new BsonDocument
                {
                    { "$gte", fromVersion },
                    { "$lte", toVersion }
                }
            }
        };
        using var cursor = await GetEventsCollection().Find(filter).Sort(new BsonDocument(versionElement, 1)).ToCursorAsync(cancellationToken);

        var expectedVersion = fromVersion;
        Byte[]? previousHash = null;
        while (await cursor.MoveNextAsync(cancellationToken))
        {
            foreach (var rawDocument in cursor.Current)
            {
                using (rawDocument)
                {
                    if (!rawDocument.TryGetValue(versionElement, out var versionValue) ||
                        !TryReadVersion(versionValue, out var version) ||
                        version != expectedVersion ||
                        !rawDocument.TryGetValue(EventIntegrityChain.ElementName, out var integrityValue))
                    {
                        return null;
                    }

                    // The first event's stored previous hash is taken as given: the anchored hash commits to it.
                    if (previousHash is null)
                    {
                        if (!EventIntegrityChain.TryRead(integrityValue, out var first))
                        {
                            return null;
                        }

                        previousHash = first.PreviousHash;
                    }

                    if (VerifyEvent(rawDocument.ToBson(), integrityValue, previousHash).Integrity is not { } integrity)
                    {
                        return null;
                    }

                    previousHash = integrity.Hash;
                    expectedVersion++;
                }
            }
        }

        return expectedVersion == toVersion + 1 ? previousHash : null;
    }

    /// <summary>
    /// The events verified by a chain scan, the erasures recorded by its erasure events, and where it broke, if it did.
    /// </summary>
    private sealed class ChainScan
    {
        public Int64? BrokenVersion { get; private set; }

        public List<ScannedEvent> Events { get; } = [];

        public StreamVerificationFailure? Failure { get; private set; }

        public HashSet<UnrecordedErasure> RecordedErasures { get; } = [];

        public ChainScan Break(Int64 version, StreamVerificationFailure failure)
        {
            BrokenVersion = version;
            Failure = failure;
            return this;
        }
    }

    /// <summary>
    /// The outcome of verifying one event: its integrity data and recorded personal data, or why it failed.
    /// </summary>
    private sealed record EventCheck(StreamVerificationFailure? Failure, EventIntegrity? Integrity, IReadOnlyList<(String Path, Boolean Erased)> PersonalData)
    {
        public static EventCheck Failed(StreamVerificationFailure failure) => new(failure, null, []);
    }

    /// <summary>
    /// A verified event of a chain scan.
    /// </summary>
    private sealed record ScannedEvent(
        Int64 Version,
        Byte[] Hash,
        Byte[] PreviousHash,
        IntegritySealMode SealMode,
        IReadOnlyList<(String Path, Boolean Erased)> PersonalData);
}
