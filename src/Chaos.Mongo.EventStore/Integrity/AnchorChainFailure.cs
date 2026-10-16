// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// Describes why the verification of the chain of anchors failed.
/// </summary>
public enum AnchorChainFailure
{
    /// <summary>
    /// The expected sequence number is missing, so an anchor was deleted or sequence numbers were changed.
    /// </summary>
    SequenceGap,

    /// <summary>
    /// The anchor document cannot be read or uses an unsupported format, or the anchor awaits its timestamps although
    /// a later anchor exists.
    /// </summary>
    MalformedAnchor,

    /// <summary>
    /// The anchor's previous digest does not match its predecessor's digest.
    /// </summary>
    PreviousDigestMismatch,

    /// <summary>
    /// The digest recomputed from the anchor's stored fields does not match its stored digest.
    /// </summary>
    AnchorDigestMismatch,

    /// <summary>
    /// The chain ends before the sequence number of the trusted head.
    /// </summary>
    TrustedHeadMissing,

    /// <summary>
    /// The anchor at the sequence number of the trusted head has a different digest.
    /// </summary>
    TrustedHeadMismatch,

    /// <summary>
    /// The anchor's stored leaves are missing, damaged or do not recompute its Merkle root, so leaves were deleted or
    /// changed, for example together with the streams they cover.
    /// </summary>
    LeafSetMismatch
}
