// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// The failed attempts of one authority to stamp an anchor, kept on the anchor so gaps stay visible.
/// </summary>
public sealed record AnchorAuthorityFailure
{
    /// <summary>
    /// Gets the name of the authority.
    /// </summary>
    public required String Authority { get; init; }

    /// <summary>
    /// Gets the number of failed attempts.
    /// </summary>
    public required Int32 FailureCount { get; init; }

    /// <summary>
    /// Gets the message of the last failure.
    /// </summary>
    public required String LastFailure { get; init; }

    /// <summary>
    /// Gets the time of the last failure.
    /// </summary>
    public required DateTime LastFailureUtc { get; init; }
}
