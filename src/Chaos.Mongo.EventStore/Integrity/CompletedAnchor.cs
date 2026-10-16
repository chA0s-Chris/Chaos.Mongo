// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// A completed anchor as passed to the completion hook: its stored fields and its tokens with their evidence, without
/// its leaves; it is resolved through <see cref="AnchorEvidenceResolver"/>. A hook that needs the leaves, for example
/// to replicate them to WORM storage, streams them through <see cref="AnchorLeafReader"/>.
/// The revocation status of its tokens is usually still unconfirmed: the CRLs that confirm it are issued at least a grace
/// period after the tokens' time and are added to the stored anchor later.
/// </summary>
/// <param name="Id">The anchor identifier.</param>
/// <param name="Sequence">The position in the chain of anchors.</param>
/// <param name="FormatVersion">The anchor format version.</param>
/// <param name="HashAlgorithm">The hash algorithm of the tree and the digests.</param>
/// <param name="PreviousDigest">The previous anchor's digest, or 32 zero bytes for the first anchor.</param>
/// <param name="MerkleRoot">The Merkle root over the leaves.</param>
/// <param name="AnchorDigest">The digest the authorities stamped.</param>
/// <param name="LeafCount">The number of leaves.</param>
/// <param name="PeriodStartUtc">The start of the anchored period.</param>
/// <param name="PeriodEndUtc">The end of the anchored period.</param>
/// <param name="OldestCandidateUtc">The oldest commit time among the anchor's candidates.</param>
/// <param name="Timestamps">The valid timestamp tokens with their evidence.</param>
/// <param name="AuthorityFailures">The failed attempts per authority recorded while the anchor awaited its tokens.</param>
/// <param name="CreatedUtc">The time the attempt created the anchor.</param>
/// <param name="CompletedUtc">The time the anchor completed.</param>
public sealed record CompletedAnchor(
    Guid Id,
    Int64 Sequence,
    Int32 FormatVersion,
    String HashAlgorithm,
    Byte[] PreviousDigest,
    Byte[] MerkleRoot,
    Byte[] AnchorDigest,
    Int32 LeafCount,
    DateTime PeriodStartUtc,
    DateTime PeriodEndUtc,
    DateTime OldestCandidateUtc,
    IReadOnlyList<AnchorTimestamp> Timestamps,
    IReadOnlyList<AnchorAuthorityFailure> AuthorityFailures,
    DateTime CreatedUtc,
    DateTime CompletedUtc)
{
    /// <summary>
    /// Creates the self-contained anchor from a completed anchor and its resolved timestamps.
    /// </summary>
    /// <param name="anchor">The completed anchor.</param>
    /// <param name="timestamps">The anchor's timestamps with their evidence, in the anchor's order.</param>
    /// <returns>The self-contained anchor.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when the anchor is not completed or the timestamps do not match the anchor's entries.
    /// </exception>
    internal static CompletedAnchor From(AnchorRecord anchor, IReadOnlyList<AnchorTimestamp> timestamps)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(timestamps);
        if (anchor is not { Status: AnchorStatus.Completed, CompletedUtc: { } completedUtc })
        {
            throw new ArgumentException("The anchor is not completed.", nameof(anchor));
        }

        if (!timestamps.Select(timestamp => timestamp.Authority).SequenceEqual(anchor.Timestamps.Select(timestamp => timestamp.Authority)))
        {
            throw new ArgumentException("The timestamps do not match the anchor's entries.", nameof(timestamps));
        }

        return new CompletedAnchor(anchor.Id,
                                   anchor.Sequence,
                                   anchor.FormatVersion,
                                   anchor.HashAlgorithm,
                                   anchor.PreviousDigest,
                                   anchor.MerkleRoot,
                                   anchor.AnchorDigest,
                                   anchor.LeafCount,
                                   anchor.PeriodStartUtc,
                                   anchor.PeriodEndUtc,
                                   anchor.OldestCandidateUtc,
                                   timestamps,
                                   anchor.AuthorityFailures,
                                   anchor.CreatedUtc,
                                   completedUtc);
    }
}
