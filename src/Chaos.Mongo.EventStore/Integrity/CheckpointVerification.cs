// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// The result of validating one checkpoint against the stream.
/// </summary>
/// <param name="Version">The checkpoint's version.</param>
/// <param name="HashStatus">How the recorded chain hash compares with the stream.</param>
/// <param name="StateMatches">Whether the checkpoint's state equals the state replayed up to its version.</param>
public sealed record CheckpointVerification(Int64 Version, CheckpointHashStatus HashStatus, Boolean StateMatches)
{
    /// <summary>
    /// Gets a value indicating whether the checkpoint is consistent with the stream. A checkpoint without a recorded
    /// chain hash is consistent when its state matches.
    /// </summary>
    public Boolean IsConsistent => HashStatus != CheckpointHashStatus.Mismatch && StateMatches;
}
