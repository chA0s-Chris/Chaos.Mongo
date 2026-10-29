// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// Identifies an anchor.
/// </summary>
public sealed record AnchorReference
{
    /// <summary>
    /// Gets the anchor identifier.
    /// </summary>
    public required Guid AnchorId { get; init; }

    /// <summary>
    /// Gets the anchor's position in the chain of anchors.
    /// </summary>
    public required Int64 Sequence { get; init; }
}
