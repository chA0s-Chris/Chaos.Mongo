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
/// <param name="AnchorId">The identifier of the anchor.</param>
/// <param name="AnchorSequence">The sequence number of the anchor.</param>
/// <param name="Index">The zero-based position of the leaf in the anchor's sorted leaves.</param>
/// <param name="AggregateType">The aggregate type name.</param>
/// <param name="AggregateId">The aggregate identifier.</param>
/// <param name="Version">The anchored stream head version.</param>
/// <param name="Hash">The chain hash of that version.</param>
/// <param name="Proof">The inclusion proof of the leaf.</param>
public sealed record AnchorLeaf(
    Guid AnchorId,
    Int64 AnchorSequence,
    Int32 Index,
    String AggregateType,
    Guid AggregateId,
    Int64 Version,
    Byte[] Hash,
    IReadOnlyList<Byte[]> Proof);
