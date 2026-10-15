// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// A finding of the completeness check.
/// </summary>
/// <param name="Kind">The kind of finding.</param>
/// <param name="AggregateId">The stream, or <c>null</c> for a broken chain of anchors.</param>
/// <param name="FirstVersion">The first version the finding concerns, or <c>null</c> when it concerns no version.</param>
/// <param name="LastVersion">
/// The last version the finding concerns; for a deleted or truncated stream, the highest anchored version.
/// </param>
/// <param name="HeadVersion">The stream's current head version, or <c>0</c> when no event is left.</param>
/// <param name="Age">
/// For unanchored events, the age of the oldest of them, from its <c>CreatedUtc</c>, which the caller supplied.
/// </param>
/// <param name="Anchor">
/// For retroactively sealed events, the anchor they are protected since; for a deleted or truncated stream, the anchor
/// whose leaf shows its highest anchored version.
/// </param>
/// <param name="AnchorChain">For a broken chain of anchors, the result of its verification.</param>
internal sealed record CompletenessFinding(
    CompletenessFindingKind Kind,
    Guid? AggregateId,
    Int64? FirstVersion,
    Int64? LastVersion,
    Int64? HeadVersion,
    TimeSpan? Age,
    AnchorReference? Anchor,
    AnchorChainVerificationResult? AnchorChain);
