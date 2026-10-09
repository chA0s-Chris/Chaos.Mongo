// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// The persisted state and progress of the background sealing sweep of one aggregate type.
/// </summary>
internal sealed class IntegritySealingState
{
    /// <summary>
    /// The identifier of the single state document per aggregate type.
    /// </summary>
    public const String DocumentId = "sealing";

    /// <summary>
    /// Gets or sets the last aggregate checked by the current pass, or <c>null</c> when no pass is in progress
    /// or the pass has not checked a stream yet.
    /// </summary>
    public Guid? Cursor { get; set; }

    /// <summary>
    /// Gets or sets the number of events sealed by the current or last pass.
    /// </summary>
    public Int64 EventsSealed { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the state document.
    /// </summary>
    public String Id { get; set; } = DocumentId;

    /// <summary>
    /// Gets a value indicating whether a pass was started but has not completed, so the next pass resumes it.
    /// </summary>
    public Boolean IsPassInProgress => PassStartedUtc is not null && PassCompletedUtc is null;

    /// <summary>
    /// Gets or sets the last aggregate in which the current pass sealed events, so a resumed pass does not
    /// count that stream in <see cref="StreamsSealed"/> twice.
    /// </summary>
    public Guid? LastSealedStream { get; set; }

    /// <summary>
    /// Gets or sets when the current or last pass completed, or <c>null</c> while a pass is in progress.
    /// </summary>
    public DateTime? PassCompletedUtc { get; set; }

    /// <summary>
    /// Gets or sets when the current or last pass started.
    /// </summary>
    public DateTime? PassStartedUtc { get; set; }

    /// <summary>
    /// Gets or sets the number of streams checked by the current or last pass.
    /// </summary>
    public Int64 StreamsChecked { get; set; }

    /// <summary>
    /// Gets or sets the number of streams in which the current or last pass sealed events.
    /// </summary>
    public Int64 StreamsSealed { get; set; }
}
