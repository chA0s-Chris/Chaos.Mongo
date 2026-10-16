// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// The progress of the retroactive sealing sweep of one aggregate type.
/// </summary>
/// <param name="StreamsChecked">The number of streams checked by the current or last pass.</param>
/// <param name="StreamsSealed">The number of streams in which the current or last pass sealed events.</param>
/// <param name="EventsSealed">The number of events sealed by the current or last pass.</param>
/// <param name="PassStartedUtc">When the current or last pass started.</param>
/// <param name="PassCompletedUtc">When the last pass completed, or <c>null</c> while a pass is in progress.</param>
public sealed record IntegritySealingStatus(
    Int64 StreamsChecked,
    Int64 StreamsSealed,
    Int64 EventsSealed,
    DateTime? PassStartedUtc,
    DateTime? PassCompletedUtc)
{
    /// <summary>
    /// Gets a value indicating whether a pass was started but has not completed. Revoke the temporary <c>update</c>
    /// permission on the events collection only after a pass completed.
    /// </summary>
    public Boolean IsPassInProgress => PassStartedUtc is not null && PassCompletedUtc is null;
}
