// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// The result of verifying that an event is anchored.
/// </summary>
/// <param name="Status">The outcome.</param>
/// <param name="Failure">
/// Why the anchoring does not verify, or <c>null</c> unless <paramref name="Status"/> is
/// <see cref="EventAnchorStatus.Failed"/>.
/// </param>
/// <param name="Anchor">
/// The covering anchor that verified, or the first covering anchor when none did, or <c>null</c> when no anchor covers
/// the event or a leaf or anchor that cannot be read was met (<see cref="EventAnchorFailure.AnchorDataUnreadable"/>).
/// </param>
/// <param name="LeafVersion">
/// The version of the stream's leaf in that anchor, or <c>null</c> when no anchor is named.
/// </param>
/// <param name="Timestamps">The result for each of the anchor's timestamp tokens; empty when they were not verified.</param>
internal sealed record EventAnchorVerificationResult(
    EventAnchorStatus Status,
    EventAnchorFailure? Failure,
    AnchorReference? Anchor,
    Int64? LeafVersion,
    IReadOnlyList<TimestampVerificationResult> Timestamps)
{
    /// <summary>
    /// Gets the result for an event that no completed anchor covers yet.
    /// </summary>
    public static EventAnchorVerificationResult NotAnchored { get; } = new(EventAnchorStatus.NotAnchored, null, null, null, []);

    /// <summary>
    /// Gets the result for an event whose anchoring was not checked because anchors are not configured.
    /// </summary>
    public static EventAnchorVerificationResult NotChecked { get; } = new(EventAnchorStatus.NotChecked, null, null, null, []);
}
