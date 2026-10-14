// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// The lifecycle state of an anchor document.
/// </summary>
internal enum AnchorStatus
{
    /// <summary>
    /// An attempt is writing the anchor's leaves; it has no sequence number yet.
    /// </summary>
    Preparing,

    /// <summary>
    /// The anchor is committed with its sequence number and acknowledged candidates, and awaits its timestamps.
    /// </summary>
    AwaitingTimestamp,

    /// <summary>
    /// The anchor holds the configured minimum number of valid timestamp tokens.
    /// </summary>
    Completed,

    /// <summary>
    /// The attempt was abandoned by a crash or superseded by a later run; its leaves are deleted.
    /// </summary>
    Discarded
}
