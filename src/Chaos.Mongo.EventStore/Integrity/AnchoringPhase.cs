// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// The points of an anchoring run after which <see cref="AnchoringRun.PhaseCompleted"/> is invoked.
/// </summary>
internal enum AnchoringPhase
{
    /// <summary>
    /// Phase 1: the anchor awaiting its timestamps was completed or retried.
    /// </summary>
    PendingAnchorStamped,

    /// <summary>
    /// Phase 2: candidates were read and reduced to one per stream. Not reported for a run without candidates.
    /// </summary>
    CandidatesRead,

    /// <summary>
    /// Phase 3, before its commit point: the prepared anchor and its leaves were written.
    /// </summary>
    LeavesWritten,

    /// <summary>
    /// Phase 3: the anchor was committed and its candidates acknowledged.
    /// </summary>
    Committed,

    /// <summary>
    /// Phase 4: the new anchor was stamped by the authorities.
    /// </summary>
    Stamped
}
