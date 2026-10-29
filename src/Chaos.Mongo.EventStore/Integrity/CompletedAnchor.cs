// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// A completed anchor as passed to the completion hook: its stored fields and its tokens with their evidence resolved
/// from the evidence store, without its leaves. A hook that needs the leaves, for example to replicate them to WORM
/// storage, streams them through <see cref="IAnchorLeafReader"/>.
/// The revocation status of its tokens is usually still unconfirmed: the CRLs that confirm it are issued at least a grace
/// period after the tokens' time and are added to the stored anchor later.
/// </summary>
public sealed record CompletedAnchor
{
    /// <summary>
    /// Gets the digest the authorities stamped.
    /// </summary>
    public required Byte[] AnchorDigest { get; init; }

    /// <summary>
    /// Gets the failed attempts per authority recorded while the anchor awaited its tokens.
    /// </summary>
    public required IReadOnlyList<AnchorAuthorityFailure> AuthorityFailures { get; init; }

    /// <summary>
    /// Gets the time the anchor completed.
    /// </summary>
    public required DateTime CompletedUtc { get; init; }

    /// <summary>
    /// Gets the time the attempt created the anchor.
    /// </summary>
    public required DateTime CreatedUtc { get; init; }

    /// <summary>
    /// Gets the anchor format version.
    /// </summary>
    public required Int32 FormatVersion { get; init; }

    /// <summary>
    /// Gets the hash algorithm of the tree and the digests.
    /// </summary>
    public required String HashAlgorithm { get; init; }

    /// <summary>
    /// Gets the anchor identifier.
    /// </summary>
    public required Guid Id { get; init; }

    /// <summary>
    /// Gets the number of leaves.
    /// </summary>
    public required Int32 LeafCount { get; init; }

    /// <summary>
    /// Gets the Merkle root over the leaves.
    /// </summary>
    public required Byte[] MerkleRoot { get; init; }

    /// <summary>
    /// Gets the oldest commit time among the anchor's candidates.
    /// </summary>
    public required DateTime OldestCandidateUtc { get; init; }

    /// <summary>
    /// Gets the end of the anchored period.
    /// </summary>
    public required DateTime PeriodEndUtc { get; init; }

    /// <summary>
    /// Gets the start of the anchored period.
    /// </summary>
    public required DateTime PeriodStartUtc { get; init; }

    /// <summary>
    /// Gets the previous anchor's digest, or 32 zero bytes for the first anchor.
    /// </summary>
    public required Byte[] PreviousDigest { get; init; }

    /// <summary>
    /// Gets the position in the chain of anchors.
    /// </summary>
    public required Int64 Sequence { get; init; }

    /// <summary>
    /// Gets the valid timestamp tokens with their evidence.
    /// </summary>
    public required IReadOnlyList<AnchorTimestamp> Timestamps { get; init; }

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

        return new CompletedAnchor
        {
            Id = anchor.Id,
            Sequence = anchor.Sequence,
            FormatVersion = anchor.FormatVersion,
            HashAlgorithm = anchor.HashAlgorithm,
            PreviousDigest = anchor.PreviousDigest,
            MerkleRoot = anchor.MerkleRoot,
            AnchorDigest = anchor.AnchorDigest,
            LeafCount = anchor.LeafCount,
            PeriodStartUtc = anchor.PeriodStartUtc,
            PeriodEndUtc = anchor.PeriodEndUtc,
            OldestCandidateUtc = anchor.OldestCandidateUtc,
            Timestamps = timestamps,
            AuthorityFailures = anchor.AuthorityFailures,
            CreatedUtc = anchor.CreatedUtc,
            CompletedUtc = completedUtc
        };
    }
}
