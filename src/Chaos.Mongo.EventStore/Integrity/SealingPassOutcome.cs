// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// Describes how a sealing pass ended.
/// </summary>
internal enum SealingPassOutcome
{
    /// <summary>
    /// The pass checked every stream.
    /// </summary>
    Completed,

    /// <summary>
    /// Another instance holds the sweep lock, so no pass was run.
    /// </summary>
    LockUnavailable,

    /// <summary>
    /// The pass lost the sweep lock and stopped at the last checked stream.
    /// </summary>
    LockLost
}
