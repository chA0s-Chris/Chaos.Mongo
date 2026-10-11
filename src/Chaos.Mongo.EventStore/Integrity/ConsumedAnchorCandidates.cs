// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// The candidates of one consumed batch, reduced to one per stream.
/// </summary>
/// <param name="Candidates">The candidates, one per stream.</param>
/// <param name="HasMore">Whether more candidates are available right away.</param>
internal sealed record ConsumedAnchorCandidates(IReadOnlyList<AnchorCandidate> Candidates, Boolean HasMore);
