// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// Verifies the chain of anchors and the evidence of single anchors: their digests and their stored timestamp tokens.
/// </summary>
/// <remarks>
/// The chain of anchors only proves consistency up to its last remaining anchor. Without a trusted head kept outside the
/// database, deleting the newest anchors together with the streams they cover cannot be detected.
/// </remarks>
internal sealed class AnchorVerifier
{
    private static readonly Byte[] FirstPreviousDigest = new Byte[AnchorEncoding.HashLength];
    private readonly IntegrityAnchoringOptions _anchoring;
    private readonly AnchorEvidenceStore _evidence;
    private readonly IntegrityVerificationOptions _verification;

    public AnchorVerifier(IMongoHelper mongoHelper, IntegrityAnchoringOptions anchoring, IntegrityVerificationOptions verification)
    {
        ArgumentNullException.ThrowIfNull(mongoHelper);
        ArgumentNullException.ThrowIfNull(anchoring);
        ArgumentNullException.ThrowIfNull(verification);
        _anchoring = anchoring;
        _verification = verification;
        Store = new AnchorStore(mongoHelper, anchoring);
        _evidence = new AnchorEvidenceStore(mongoHelper, anchoring);
    }

    /// <summary>
    /// Gets the store of the anchors and their leaves.
    /// </summary>
    internal AnchorStore Store { get; }

    /// <summary>
    /// Gets the time after a token's time that confirming CRLs must be issued at or after.
    /// </summary>
    private TimeSpan RevocationGracePeriod => _verification.RevocationGracePeriod ?? _anchoring.RevocationGracePeriod;

    /// <summary>
    /// Verifies an anchor: recomputes its digest from its stored fields and verifies each of its stored timestamp
    /// tokens against it. Evidence that is missing or does not match its hash fails only the token that references it.
    /// </summary>
    /// <param name="anchor">The anchor.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The result.</returns>
    public async Task<AnchorVerificationResult> VerifyAnchorAsync(AnchorRecord anchor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        var digestIsValid = RecomputesDigest(anchor);
        var timestamps = new List<TimestampVerificationResult>(anchor.Timestamps.Count);
        foreach (var reference in anchor.Timestamps)
        {
            timestamps.Add(await VerifyTimestampAsync(reference, anchor.AnchorDigest, cancellationToken));
        }

        return new AnchorVerificationResult(digestIsValid, timestamps);
    }

    /// <summary>
    /// Verifies the chain of anchors: committed sequence numbers must be contiguous from 1, each anchor's previous
    /// digest must equal its predecessor's digest, each digest must recompute from its stored fields, and each Merkle
    /// root must recompute from the anchor's stored leaves. Only the newest anchor may still await its timestamps, and
    /// with a trusted head, every anchor up to it must be completed.
    /// </summary>
    /// <remarks>
    /// Recomputing the roots binds the stored leaves to the anchor digests, which stream verification compares streams
    /// with, so deleting or changing leaves together with the streams they cover is detected unless the digests are
    /// recomputed as well. Digests and roots involve no secret, so the chain proves consistency, not authenticity: an
    /// anchor is authentic up to a trusted head, whose digest is kept outside the database, and otherwise only through
    /// its timestamp tokens, which <see cref="VerifyAnchorAsync"/> verifies. Anchors after the trusted head, or all
    /// anchors without one, may have been rebuilt or appended consistently by someone with write access. The status of
    /// the newest anchor is not covered by its digest either. The cost grows with the stored leaves.
    /// </remarks>
    /// <param name="trustedHead">
    /// An anchor kept outside the database that the chain must contain unchanged, or <c>null</c> to check consistency
    /// only.
    /// </param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The result, with the first broken sequence number if any.</returns>
    /// <exception cref="ArgumentException">Thrown when the trusted head has no positive sequence number or no SHA-256 digest.</exception>
    public async Task<AnchorChainVerificationResult> VerifyAnchorChainAsync(TrustedAnchorHead? trustedHead = null, CancellationToken cancellationToken = default)
    {
        if (trustedHead is not null && (trustedHead.Sequence < 1 || trustedHead.AnchorDigest.Length != AnchorEncoding.HashLength))
        {
            throw new ArgumentException("A trusted head needs a positive sequence number and a SHA-256 anchor digest.", nameof(trustedHead));
        }

        var expectedSequence = 1L;
        var previousDigest = FirstPreviousDigest;
        Byte[]? trustedSequenceDigest = null;
        var previousAwaitsTimestamps = false;
        await foreach (var document in Store.ReadCommittedAnchorDocumentsAsync(cancellationToken))
        {
            if (!AnchorStore.TryRead(document, AnchorStore.ReadAnchor, out var anchor))
            {
                return Broken(expectedSequence, AnchorChainFailure.MalformedAnchor);
            }

            if (anchor.Sequence != expectedSequence)
            {
                return Broken(expectedSequence, AnchorChainFailure.SequenceGap);
            }

            // No anchor is created while one awaits its timestamps, so only the newest anchor can await them.
            if (previousAwaitsTimestamps)
            {
                return new AnchorChainVerificationResult(expectedSequence - 1, AnchorChainFailure.MalformedAnchor, expectedSequence - 2);
            }

            // The completion hook only sees completed anchors, so every anchor up to a trusted head was completed; a
            // status changed back to awaiting its timestamps would hide the anchor's leaves from stream verification.
            if (anchor.Status is not (AnchorStatus.AwaitingTimestamp or AnchorStatus.Completed) ||
                (anchor.Sequence <= trustedHead?.Sequence && anchor.Status != AnchorStatus.Completed) ||
                anchor.FormatVersion != AnchorEncoding.FormatVersion ||
                !String.Equals(anchor.HashAlgorithm, AnchorEncoding.HashAlgorithmName, StringComparison.Ordinal) ||
                anchor.AnchorDigest.Length != AnchorEncoding.HashLength)
            {
                return Broken(expectedSequence, AnchorChainFailure.MalformedAnchor);
            }

            if (!anchor.PreviousDigest.AsSpan().SequenceEqual(previousDigest))
            {
                return Broken(expectedSequence, AnchorChainFailure.PreviousDigestMismatch);
            }

            if (!RecomputesDigest(anchor))
            {
                return Broken(expectedSequence, AnchorChainFailure.AnchorDigestMismatch);
            }

            if (!await RecomputesRootAsync(anchor, cancellationToken))
            {
                return Broken(expectedSequence, AnchorChainFailure.LeafSetMismatch);
            }

            if (anchor.Sequence == trustedHead?.Sequence)
            {
                trustedSequenceDigest = anchor.AnchorDigest;
            }

            previousDigest = anchor.AnchorDigest;
            previousAwaitsTimestamps = anchor.Status == AnchorStatus.AwaitingTimestamp;
            expectedSequence++;
        }

        if (trustedHead is not null)
        {
            if (trustedSequenceDigest is null)
            {
                return Broken(expectedSequence, AnchorChainFailure.TrustedHeadMissing);
            }

            if (!trustedSequenceDigest.AsSpan().SequenceEqual(trustedHead.AnchorDigest))
            {
                return Broken(trustedHead.Sequence, AnchorChainFailure.TrustedHeadMismatch);
            }
        }

        return new AnchorChainVerificationResult(null, null, expectedSequence - 1);

        AnchorChainVerificationResult Broken(Int64 sequence, AnchorChainFailure failure)
            => new(sequence, failure, expectedSequence - 1);
    }

    /// <summary>
    /// Computes the Merkle leaf hash of a stored leaf, whose chain hash must have the length of a SHA-256 hash.
    /// </summary>
    internal static Byte[] HashLeaf(AnchorLeaf leaf)
        => MerkleTree.HashLeaf(AnchorEncoding.EncodeLeaf(new AnchorCandidate(leaf.AggregateType, leaf.AggregateId, leaf.Version, leaf.Hash, default)));

    /// <summary>
    /// Verifies that a stored leaf proves its inclusion in its anchor's Merkle root.
    /// </summary>
    internal static Boolean ProvesInclusion(AnchorLeaf leaf, AnchorRecord anchor)
        => leaf.Hash.Length == AnchorEncoding.HashLength &&
           MerkleTree.VerifyInclusion(HashLeaf(leaf), leaf.Index, anchor.LeafCount, leaf.Proof, anchor.MerkleRoot);

    private static Boolean RecomputesDigest(AnchorRecord anchor)
    {
        try
        {
            return AnchorEncoding.ComputeAnchorDigest(anchor.FormatVersion, anchor.Sequence, anchor.PreviousDigest, anchor.MerkleRoot)
                                 .AsSpan()
                                 .SequenceEqual(anchor.AnchorDigest);
        }
        catch (ArgumentException)
        {
            // A digest or root of the wrong length cannot belong to a valid anchor.
            return false;
        }
    }

    private async Task<IReadOnlyList<Byte[]>> ReadAllAsync(IReadOnlyList<Byte[]> hashes, CancellationToken cancellationToken)
    {
        var items = new List<Byte[]>(hashes.Count);
        foreach (var hash in hashes)
        {
            items.Add(await _evidence.ReadAsync(hash, cancellationToken));
        }

        return items;
    }

    /// <summary>
    /// Recomputes an anchor's Merkle root from its stored leaves: exactly <see cref="AnchorRecord.LeafCount"/> readable
    /// leaves with the indexes from 0 that carry the anchor's sequence number.
    /// </summary>
    private async Task<Boolean> RecomputesRootAsync(AnchorRecord anchor, CancellationToken cancellationToken)
    {
        if (anchor.LeafCount < 1)
        {
            return false;
        }

        // The stored leaf count is not trusted to size the list.
        var leafHashes = new List<Byte[]>();
        await foreach (var document in Store.ReadLeafDocumentsAsync(anchor.Id, cancellationToken))
        {
            if (leafHashes.Count == anchor.LeafCount ||
                !AnchorStore.TryRead(document, AnchorStore.ReadLeaf, out var leaf) ||
                leaf.Index != leafHashes.Count ||
                leaf.AnchorSequence != anchor.Sequence ||
                leaf.Hash.Length != AnchorEncoding.HashLength)
            {
                return false;
            }

            leafHashes.Add(HashLeaf(leaf));
        }

        return leafHashes.Count == anchor.LeafCount && MerkleTree.Build(leafHashes).Root.AsSpan().SequenceEqual(anchor.MerkleRoot);
    }

    private async Task<TimestampVerificationResult> VerifyTimestampAsync(AnchorTimestampReference reference,
                                                                         Byte[] anchorDigest,
                                                                         CancellationToken cancellationToken)
    {
        AnchorTimestamp timestamp;
        try
        {
            timestamp = new AnchorTimestamp(reference.Authority,
                                            await _evidence.ReadAsync(reference.TokenHash, cancellationToken),
                                            await ReadAllAsync(reference.CertificateChainHashes, cancellationToken),
                                            await ReadAllAsync(reference.CertificateRevocationListHashes, cancellationToken),
                                            reference.TimestampUtc,
                                            reference.ObtainedUtc)
            {
                RevocationStatus = reference.RevocationStatus,
                RevocationStatusUtc = reference.RevocationStatusUtc,
                ConfirmingRevocationLists = await ReadAllAsync(reference.ConfirmingRevocationListHashes, cancellationToken)
            };
        }
        catch (Exception ex) when (ex is AnchorEvidenceException or ArgumentException)
        {
            // A reference that is not a SHA-256 hash cannot address stored evidence.
            return new TimestampVerificationResult(reference.Authority,
                                                   reference.TimestampUtc,
                                                   false,
                                                   ex is AnchorEvidenceException ? ex.Message : "The timestamp references malformed evidence.",
                                                   TimestampRevocationStatus.Unconfirmed,
                                                   reference.RevocationStatus);
        }

        return StoredTimestampVerifier.Verify(timestamp, anchorDigest, _verification.FindTrust(reference.Authority), RevocationGracePeriod);
    }
}
