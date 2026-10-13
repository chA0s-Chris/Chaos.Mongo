// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// A completed anchor as passed to the completion hook: its stored fields and tokens, without its leaves. A hook that
/// needs the leaves, for example to replicate them to WORM storage, streams them through <see cref="AnchorLeafReader"/>.
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
internal sealed record CompletedAnchor(
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
    /// Creates the hook payload from a completed anchor.
    /// </summary>
    /// <param name="anchor">The completed anchor.</param>
    /// <returns>The payload.</returns>
    /// <exception cref="ArgumentException">Thrown when the anchor is not completed.</exception>
    public static CompletedAnchor From(AnchorRecord anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        if (anchor is not { Status: AnchorStatus.Completed, CompletedUtc: { } completedUtc })
        {
            throw new ArgumentException("The anchor is not completed.", nameof(anchor));
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
                                   anchor.Timestamps,
                                   anchor.AuthorityFailures,
                                   anchor.CreatedUtc,
                                   completedUtc);
    }
}
