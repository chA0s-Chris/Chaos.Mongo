// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// The result of an anchoring run.
/// </summary>
/// <param name="Outcome">How the run ended.</param>
/// <param name="Anchor">The anchor the run ended with, if any.</param>
/// <param name="OldestCandidateUtc">
/// The oldest candidate commit time stored on the anchor still awaiting its timestamps, if the run stopped there;
/// otherwise the oldest commit time among the candidates the run read, or <c>null</c> without candidates.
/// </param>
internal sealed record AnchoringRunResult(AnchoringRunOutcome Outcome, AnchorRecord? Anchor = null, DateTime? OldestCandidateUtc = null);
