// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// An anchor: the Merkle root over the stream heads changed since the previous anchor, chained to it through the
/// previous anchor's digest and timestamped by one or more authorities.
/// </summary>
/// <param name="Id">The anchor identifier, unique to the attempt that created it.</param>
/// <param name="Status">The lifecycle state.</param>
/// <param name="Sequence">
/// The position in the chain of anchors; fixed when the attempt starts and stored on the anchor document at commit.
/// </param>
/// <param name="FormatVersion">The anchor format version.</param>
/// <param name="HashAlgorithm">The hash algorithm of the tree and the digests.</param>
/// <param name="PreviousDigest">The previous anchor's digest, or 32 zero bytes for the first anchor.</param>
/// <param name="MerkleRoot">The Merkle root over the leaves.</param>
/// <param name="AnchorDigest">The digest the authorities stamp.</param>
/// <param name="LeafCount">The number of leaves.</param>
/// <param name="PeriodStartUtc">The start of the anchored period.</param>
/// <param name="PeriodEndUtc">The end of the anchored period: the commit time of the newest candidate.</param>
/// <param name="OldestCandidateUtc">The oldest commit time among the anchor's candidates.</param>
/// <param name="Timestamps">The valid timestamp tokens obtained so far, referencing their evidence.</param>
/// <param name="AuthorityFailures">The failed attempts per authority.</param>
/// <param name="CreatedUtc">The time the attempt created the anchor.</param>
/// <param name="CompletedUtc">The time the anchor completed, or <c>null</c> before.</param>
internal sealed record AnchorRecord(
    Guid Id,
    AnchorStatus Status,
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
    IReadOnlyList<AnchorTimestampReference> Timestamps,
    IReadOnlyList<AnchorAuthorityFailure> AuthorityFailures,
    DateTime CreatedUtc,
    DateTime? CompletedUtc);
