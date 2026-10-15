// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// Describes why the anchoring of an event does not verify.
/// </summary>
internal enum EventAnchorFailure
{
    /// <summary>
    /// The chain from the event to the anchored version does not verify, for example because a version is missing or
    /// an event was changed.
    /// </summary>
    ChainBroken,

    /// <summary>
    /// The stored hash of the anchored version differs from the hash in the anchor's leaf.
    /// </summary>
    LeafHashMismatch,

    /// <summary>
    /// The leaf's inclusion proof does not lead to the anchor's Merkle root.
    /// </summary>
    InclusionProofInvalid,

    /// <summary>
    /// The digest recomputed from the anchor's stored fields does not match its stored digest.
    /// </summary>
    AnchorDigestMismatch,

    /// <summary>
    /// None of the anchor's timestamp tokens is valid.
    /// </summary>
    NoValidTimestamp
}
