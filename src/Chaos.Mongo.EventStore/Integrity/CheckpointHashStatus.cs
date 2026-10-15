// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// Describes how a checkpoint's recorded chain hash compares with the stream.
/// </summary>
internal enum CheckpointHashStatus
{
    /// <summary>
    /// The recorded chain hash equals the hash of the event at the checkpoint's version.
    /// </summary>
    Matches,

    /// <summary>
    /// The checkpoint records no chain hash, for example because it was written before protection was enabled.
    /// </summary>
    Unverifiable,

    /// <summary>
    /// The recorded chain hash differs from the hash of the event at the checkpoint's version, or no event exists at it.
    /// </summary>
    Mismatch
}
