// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// The integrity data of one verified event of a stream.
/// </summary>
/// <param name="Version">The event version.</param>
/// <param name="Hash">The event's chain hash.</param>
/// <param name="PreviousHash">The predecessor's chain hash, or the stream's genesis value for version 1.</param>
/// <param name="SealMode">How the event was sealed.</param>
/// <param name="SealingAnchor">
/// The first completed anchor whose leaf for the stream has a version at or above the event's, or <c>null</c> while
/// the event is not anchored or anchors are not configured. For a retroactively sealed event, it is protected since
/// this anchor.
/// </param>
/// <param name="PersonalData">The state of each personal data value the event records.</param>
internal sealed record VerifiedEvent(
    Int64 Version,
    Byte[] Hash,
    Byte[] PreviousHash,
    IntegritySealMode SealMode,
    AnchorReference? SealingAnchor,
    IReadOnlyList<PersonalDataPathStatus> PersonalData);
