// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using MongoDB.Bson;
using MongoDB.Driver;
using System.Text;

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
    /// covers it, the anchor's leaf and inclusion proof, the anchor's digest and its stored timestamp tokens. When none
    /// of that anchor's tokens is valid, for example because one was revoked, the next completed anchor that covers the
    /// event is verified in the same way, so the evidence degrades to that anchor's time; the result names the anchor
    /// that verified, or the first one when none does. Any other failure ends the verification. A stream leaf that
    /// cannot be read, or whose committed anchor cannot be read, met before an anchor verified, fails it as
    /// <see cref="EventAnchorFailure.LeafHashMismatch"/>.
    /// </summary>
    /// <param name="aggregateId">The aggregate identifier.</param>
    /// <param name="version">The event version.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The result.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="version"/> is less than 1.</exception>
    public async Task<EventAnchorVerificationResult> VerifyEventAnchorAsync(Guid aggregateId, Int64 version, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        if (_anchors is null)
        {
            return EventAnchorVerificationResult.NotChecked;
        }

        EventAnchorVerificationResult? firstWithoutValidToken = null;
        await foreach (var (covering, unreadable) in _anchors.Store.FindCompletedStreamLeavesInOrderAsync(_aggregateTypeName, aggregateId, version, cancellationToken))
        {
            if (unreadable || covering is null)
            {
                return new EventAnchorVerificationResult(EventAnchorStatus.Failed, EventAnchorFailure.LeafHashMismatch, null, null, []);
            }

            var result = await VerifyEventAtAnchorAsync(_anchors, aggregateId, version, covering, cancellationToken);
            if (result.Failure != EventAnchorFailure.NoValidTimestamp)
            {
                return result;
            }

            firstWithoutValidToken ??= result;
        }

        return firstWithoutValidToken ?? EventAnchorVerificationResult.NotAnchored;
    }

    /// <summary>
    /// Recomputes the hash chain of a stream and reports the first broken version. For every verified version it
    /// returns the integrity data and the sealing anchor. It also checks the inclusion proofs of the stream's leaves in
    /// completed anchors and compares the verified versions with them, reporting the lowest broken version of the chain
    /// and of the leaves, and for an intact stream the erased values that no later erasure event records.
    /// </summary>
    /// <remarks>
    /// A consistent chain does not reveal deleted newest events or a deleted stream, because the remaining events still
    /// form a valid chain. Only the comparison with anchored leaves detects that, for anchored versions. A stream leaf
    /// that cannot be read, or whose committed anchor cannot be read, breaks the stream as
    /// <see cref="StreamVerificationFailure.AnchoredLeafInvalid"/> at its version, or at version 1 when its version is a
    /// number but not an integer. A leaf whose version is not a number is not selected by the query for the stream's
    /// leaves at all; the anchor-chain verification, which binds the leaves to the anchor digests and should run too,
    /// reports it.
    /// </remarks>
    /// <param name="aggregateId">The aggregate identifier.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The verification result.</returns>
    public async Task<StreamVerificationResult> VerifyStreamAsync(Guid aggregateId, CancellationToken cancellationToken = default)
    {
        var scan = await ScanChainAsync(aggregateId, cancellationToken);
        var leaves = _anchors is null
            ? new StreamLeaves([], null)
            : await _anchors.Store.ReadStreamLeavesAsync(_aggregateTypeName, aggregateId, 1, cancellationToken);
        var events = BuildVerifiedEvents(scan, leaves.Completed);
        var transitions = FindSealModeTransitions(scan.Events);

        StreamVerificationResult result;
        if (scan is { BrokenVersion: { } brokenVersion, Failure: { } failure })
        {
            // Leaves below the break still judge the verified prefix, so a re-sealed prefix shows at its anchored version.
            result = CompareWithLeaves(scan.Events, leaves, brokenVersion) is { } earlierBreak
                ? StreamVerificationResult.Broken(earlierBreak.Version, earlierBreak.Failure)
                : StreamVerificationResult.Broken(brokenVersion, failure);
        }
        else if (CompareWithLeaves(scan.Events, leaves, null) is { } anchoredBreak)
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
                !StoredVersion.TryRead(versionValue, out var version) ||
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
    /// Compares the verified versions with the stream's leaves in completed anchors: a leaf that cannot be read or does
    /// not prove its inclusion in its anchor's root was changed, an anchored version that no longer exists reveals
    /// truncation, and an anchored version whose stored hash differs from its leaf reveals a rewritten or rolled-back
    /// stream. For a chain broken at a version, only findings below it count.
    /// </summary>
    /// <returns>The lowest broken version and why, or <c>null</c> when every anchored version matches.</returns>
    private static (Int64 Version, StreamVerificationFailure Failure)? CompareWithLeaves(IReadOnlyList<ScannedEvent> events,
                                                                                         StreamLeaves leaves,
                                                                                         Int64? brokenVersion)
    {
        (Int64 Version, StreamVerificationFailure Failure)? first = leaves.FirstUnreadableVersion is { } unreadable && !(unreadable >= brokenVersion)
            ? (unreadable, StreamVerificationFailure.AnchoredLeafInvalid)
            : null;
        foreach (var (leaf, anchor) in leaves.Completed.Where(completed => !(completed.Leaf.Version >= brokenVersion)))
        {
            (Int64 Version, StreamVerificationFailure Failure)? finding = !AnchorVerifier.ProvesInclusion(leaf, anchor)
                ? (leaf.Version, StreamVerificationFailure.AnchoredLeafInvalid)
                : leaf.Version > events.Count
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
    /// Gets a value indicating whether every top-level element name of an event document is valid UTF-8. A raw document
    /// decodes names on access, so an invalid one would otherwise surface as an exception during a later lookup.
    /// </summary>
    private static Boolean HasReadableNames(RawBsonDocument document)
    {
        try
        {
            foreach (var _ in document.Names) { }

            return true;
        }
        catch (DecoderFallbackException)
        {
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

            try
            {
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
            catch (Exception ex) when (ex is DecoderFallbackException or FormatException)
            {
                // Paths are resolved on the stored bytes, which decode names on the way; a name that is not valid
                // UTF-8, or a value the reader rejects, makes the event malformed.
                return EventCheck.Failed(StreamVerificationFailure.MalformedIntegrity);
            }
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
                    if (!HasReadableNames(rawDocument))
                    {
                        return scan.Break(expectedVersion, StreamVerificationFailure.MalformedIntegrity);
                    }

                    if (!rawDocument.TryGetValue(versionElement, out var versionValue) ||
                        !StoredVersion.TryRead(versionValue, out var version) ||
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

                    // The erasure event is identified by its reserved discriminator; its records are trusted once its own hash verified.
                    var recordedErasures = new HashSet<UnrecordedErasure>();
                    try
                    {
                        if (rawDocument.TryGetValue(discriminatorElement, out var discriminator) &&
                            EventDocumentFields<TAggregate>.IsErasureEvent(discriminator))
                        {
                            CollectErasureRecords(rawDocument, expectedVersion, recordedErasures);
                        }
                    }
                    catch (Exception ex) when (ex is DecoderFallbackException or FormatException)
                    {
                        // A raw document decodes values on access, so a discriminator, entry or path that is not valid UTF-8,
                        // or a value the reader rejects, such as a UUID that is not 16 bytes long, surfaces here.
                        return scan.Break(expectedVersion, StreamVerificationFailure.MalformedIntegrity);
                    }

                    scan.Events.Add(new ScannedEvent(expectedVersion, integrity.Hash, integrity.PreviousHash, integrity.SealMode, check.PersonalData));
                    scan.RecordedErasures.UnionWith(recordedErasures);
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
                    if (!HasReadableNames(rawDocument) ||
                        !rawDocument.TryGetValue(versionElement, out var versionValue) ||
                        !StoredVersion.TryRead(versionValue, out var version) ||
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
    /// Verifies an event against one completed anchor that covers it.
    /// </summary>
    private async Task<EventAnchorVerificationResult> VerifyEventAtAnchorAsync(AnchorVerifier anchors,
                                                                               Guid aggregateId,
                                                                               Int64 version,
                                                                               CompletedStreamLeaf covering,
                                                                               CancellationToken cancellationToken)
    {
        var (leaf, anchor) = covering;
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

        if (!AnchorVerifier.ProvesInclusion(leaf, anchor))
        {
            return Failed(EventAnchorFailure.InclusionProofInvalid, []);
        }

        var anchorResult = await anchors.VerifyAnchorAsync(anchor, cancellationToken);
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
