// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// Describes a finding of the completeness check.
/// </summary>
internal enum CompletenessFindingKind
{
    /// <summary>
    /// The chain of anchors is broken, so findings about deleted or truncated streams may be incomplete.
    /// </summary>
    AnchorChainBroken,

    /// <summary>
    /// Events of a stream are not covered by any completed anchor yet.
    /// </summary>
    Unanchored,

    /// <summary>
    /// Every event of a stream was deleted, although a completed anchor covers its head.
    /// </summary>
    StreamDeleted,

    /// <summary>
    /// A stream ends below a version a completed anchor covers, so its newest events were deleted.
    /// </summary>
    StreamTruncated,

    /// <summary>
    /// Retroactively sealed events are protected since an anchor, not since their creation.
    /// </summary>
    RetroactivelyProtected,

    /// <summary>
    /// Events of a stream carry no integrity data.
    /// </summary>
    Unsealed
}
