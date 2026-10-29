// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// One leaf of an anchor with its inclusion proof, so a single leaf suffices to verify one stream head against the
/// anchor.
/// </summary>
/// <remarks>
/// Records compare byte arrays and lists by reference, so compare the hash with <c>SequenceEqual</c> and the proof's
/// hashes one by one, since <c>SequenceEqual</c> on a list of byte arrays compares references too.
/// </remarks>
public sealed record AnchorLeaf
{
    /// <summary>
    /// Gets the aggregate identifier.
    /// </summary>
    public required Guid AggregateId { get; init; }

    /// <summary>
    /// Gets the aggregate type name.
    /// </summary>
    public required String AggregateType { get; init; }

    /// <summary>
    /// Gets the identifier of the anchor.
    /// </summary>
    public required Guid AnchorId { get; init; }

    /// <summary>
    /// Gets the sequence number of the anchor.
    /// </summary>
    public required Int64 AnchorSequence { get; init; }

    /// <summary>
    /// Gets the chain hash of that version.
    /// </summary>
    public required Byte[] Hash { get; init; }

    /// <summary>
    /// Gets the zero-based position of the leaf in the anchor's sorted leaves.
    /// </summary>
    public required Int32 Index { get; init; }

    /// <summary>
    /// Gets the inclusion proof of the leaf.
    /// </summary>
    public required IReadOnlyList<Byte[]> Proof { get; init; }

    /// <summary>
    /// Gets the anchored stream head version.
    /// </summary>
    public required Int64 Version { get; init; }
}
