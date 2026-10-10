// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// Selects how anchoring finds the stream heads that changed since the last anchor.
/// </summary>
internal enum AnchorCandidateSourceKind
{
    /// <summary>
    /// No candidates are collected.
    /// </summary>
    None,

    /// <summary>
    /// Every append writes a pending-anchor entry in its transaction (option E of #154).
    /// </summary>
    PendingCollection,

    /// <summary>
    /// A change stream on the protected events collections yields the candidates (option D of #154).
    /// </summary>
    ChangeStream
}
