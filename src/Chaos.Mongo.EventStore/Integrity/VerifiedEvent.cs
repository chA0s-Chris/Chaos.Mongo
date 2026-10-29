// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// The integrity data of one verified event of a stream.
/// </summary>
public sealed record VerifiedEvent
{
    /// <summary>
    /// Gets the event's chain hash.
    /// </summary>
    public required Byte[] Hash { get; init; }

    /// <summary>
    /// Gets the state of each personal data value the event records.
    /// </summary>
    public required IReadOnlyList<PersonalDataPathStatus> PersonalData { get; init; }

    /// <summary>
    /// Gets the predecessor's chain hash, or the stream's genesis value for version 1.
    /// </summary>
    public required Byte[] PreviousHash { get; init; }

    /// <summary>
    /// Gets how the event was sealed.
    /// </summary>
    public required IntegritySealMode SealMode { get; init; }

    /// <summary>
    /// Gets the first completed anchor whose leaf for the stream has a version at or above the event's, or <c>null</c>
    /// while the event is not anchored or anchors are not configured. For a retroactively sealed event, it is
    /// protected since this anchor.
    /// </summary>
    public AnchorReference? SealingAnchor { get; init; }

    /// <summary>
    /// Gets the event version.
    /// </summary>
    public required Int64 Version { get; init; }
}
