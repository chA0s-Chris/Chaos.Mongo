// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// How an anchoring run ended.
/// </summary>
internal enum AnchoringRunOutcome
{
    /// <summary>
    /// Another instance holds the anchoring lock.
    /// </summary>
    LockUnavailable,

    /// <summary>
    /// The lock lease could not be extended, so the run stopped; the next run resumes from the stored state.
    /// </summary>
    LockLost,

    /// <summary>
    /// No candidates were available, so no anchor was created and no timestamp requested.
    /// </summary>
    NoCandidates,

    /// <summary>
    /// An anchor still awaits the configured minimum number of timestamp tokens.
    /// </summary>
    AwaitingTimestamps,

    /// <summary>
    /// The run's anchor was completed.
    /// </summary>
    Completed
}
