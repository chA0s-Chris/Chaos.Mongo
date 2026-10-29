// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// The progress of the retroactive sealing sweep of one aggregate type.
/// </summary>
public sealed record IntegritySealingStatus
{
    /// <summary>
    /// Gets the number of events sealed by the current or last pass.
    /// </summary>
    public required Int64 EventsSealed { get; init; }

    /// <summary>
    /// Gets a value indicating whether the last pass completed and sealed every stream it checked. Revoke the temporary
    /// <c>update</c> permission on the events collection only then: a pass also completes when it skipped streams it
    /// could not seal.
    /// </summary>
    public Boolean IsCompleteWithoutFailures => PassCompletedUtc is not null && StreamsFailed == 0;

    /// <summary>
    /// Gets a value indicating whether a pass was started but has not completed.
    /// </summary>
    public Boolean IsPassInProgress => PassStartedUtc is not null && PassCompletedUtc is null;

    /// <summary>
    /// Gets when the last pass completed, or <c>null</c> while a pass is in progress.
    /// </summary>
    public DateTime? PassCompletedUtc { get; init; }

    /// <summary>
    /// Gets when the current or last pass started.
    /// </summary>
    public DateTime? PassStartedUtc { get; init; }

    /// <summary>
    /// Gets the number of streams checked by the current or last pass.
    /// </summary>
    public required Int64 StreamsChecked { get; init; }

    /// <summary>
    /// Gets the number of streams the current or last pass could not seal; it logged them and continued with the next
    /// stream.
    /// </summary>
    public required Int64 StreamsFailed { get; init; }

    /// <summary>
    /// Gets the number of streams in which the current or last pass sealed events.
    /// </summary>
    public required Int64 StreamsSealed { get; init; }
}
