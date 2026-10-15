// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// Describes how the stored read model compares with the state replayed from the stream.
/// </summary>
internal enum ReadModelStatus
{
    /// <summary>
    /// The read model was not compared, because the stream's chain is broken.
    /// </summary>
    NotChecked,

    /// <summary>
    /// The read model matches the replayed state, or neither exists.
    /// </summary>
    Matches,

    /// <summary>
    /// The read model differs from the replayed state.
    /// </summary>
    Differs,

    /// <summary>
    /// The stream has state-changing events, but no read model is stored.
    /// </summary>
    Missing
}
