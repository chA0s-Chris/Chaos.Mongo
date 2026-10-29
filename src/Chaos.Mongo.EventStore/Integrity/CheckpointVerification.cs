// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// The result of validating one checkpoint against the stream.
/// </summary>
public sealed record CheckpointVerification
{
    /// <summary>
    /// Gets how the recorded chain hash compares with the stream.
    /// </summary>
    public required CheckpointHashStatus HashStatus { get; init; }

    /// <summary>
    /// Gets a value indicating whether the checkpoint is consistent with the stream. A checkpoint without a recorded
    /// chain hash is consistent when its state matches.
    /// </summary>
    public Boolean IsConsistent => HashStatus != CheckpointHashStatus.Mismatch && StateMatches;

    /// <summary>
    /// Gets whether the checkpoint's state equals the state replayed up to its version.
    /// </summary>
    public required Boolean StateMatches { get; init; }

    /// <summary>
    /// Gets the checkpoint's version.
    /// </summary>
    public required Int64 Version { get; init; }
}
