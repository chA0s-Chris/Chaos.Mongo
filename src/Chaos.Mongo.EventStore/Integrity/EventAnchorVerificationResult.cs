// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// The result of verifying that an event is anchored.
/// </summary>
public sealed record EventAnchorVerificationResult
{
    /// <summary>
    /// Gets the covering anchor whose check ended the verification: the one that verified, the one that failed for
    /// another reason than having no valid token, or the first covering anchor when none had a valid token;
    /// <c>null</c> when no anchor covers the event or a leaf or anchor that cannot be read was met
    /// (<see cref="EventAnchorFailure.AnchorDataUnreadable"/>).
    /// </summary>
    public AnchorReference? Anchor { get; init; }

    /// <summary>
    /// Gets why the anchoring does not verify, or <c>null</c> unless <see cref="Status"/> is
    /// <see cref="EventAnchorStatus.Failed"/>.
    /// </summary>
    public EventAnchorFailure? Failure { get; init; }

    /// <summary>
    /// Gets the version of the stream's leaf in that anchor, or <c>null</c> when no anchor is named.
    /// </summary>
    public Int64? LeafVersion { get; init; }

    /// <summary>
    /// Gets the outcome.
    /// </summary>
    public required EventAnchorStatus Status { get; init; }

    /// <summary>
    /// Gets the result for each of the anchor's timestamp tokens; empty when they were not verified.
    /// </summary>
    public required IReadOnlyList<TimestampVerificationResult> Timestamps { get; init; }

    /// <summary>
    /// Gets the result for an event that no completed anchor covers yet.
    /// </summary>
    internal static EventAnchorVerificationResult NotAnchored { get; } = new()
    {
        Status = EventAnchorStatus.NotAnchored,
        Timestamps = []
    };

    /// <summary>
    /// Gets the result for an event whose anchoring was not checked because anchors are not configured.
    /// </summary>
    internal static EventAnchorVerificationResult NotChecked { get; } = new()
    {
        Status = EventAnchorStatus.NotChecked,
        Timestamps = []
    };
}
