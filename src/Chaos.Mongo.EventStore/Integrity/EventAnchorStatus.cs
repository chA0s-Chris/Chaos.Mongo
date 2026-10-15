// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// Describes the outcome of verifying that an event is anchored.
/// </summary>
internal enum EventAnchorStatus
{
    /// <summary>
    /// Anchors are not configured, so the event's anchoring was not checked.
    /// </summary>
    NotChecked,

    /// <summary>
    /// No completed anchor covers the event yet.
    /// </summary>
    NotAnchored,

    /// <summary>
    /// The event is covered by a completed anchor with a valid inclusion proof, a valid digest and at least one valid
    /// timestamp token.
    /// </summary>
    Anchored,

    /// <summary>
    /// A completed anchor covers the event, but its evidence does not verify.
    /// </summary>
    Failed
}
