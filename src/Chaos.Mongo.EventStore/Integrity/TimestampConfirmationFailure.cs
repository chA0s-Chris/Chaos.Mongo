// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// The failed attempts to confirm the revocation status of a timestamp token, kept on its entry so gaps stay visible.
/// CRLs that were only issued too early do not count as failures.
/// </summary>
public sealed record TimestampConfirmationFailure
{
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
