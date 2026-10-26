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
    NoValidTimestamp,

    /// <summary>
    /// A leaf of the stream, or the committed anchor it belongs to, cannot be read where the verification met it, before
    /// an anchor verified; no anchor is named in the result, because none could be checked there.
    /// </summary>
    AnchorDataUnreadable
}
