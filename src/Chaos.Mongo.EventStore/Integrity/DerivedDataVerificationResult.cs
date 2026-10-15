// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// The result of comparing a stream's derived data, its read model and checkpoints, with the stream.
/// </summary>
/// <param name="Stream">The verification of the stream's chain; derived data is only compared for an intact chain.</param>
/// <param name="ReadModel">How the stored read model compares with the replayed state.</param>
/// <param name="Checkpoints">The result for each stored checkpoint, in version order; empty for a broken chain.</param>
internal sealed record DerivedDataVerificationResult(
    StreamVerificationResult Stream,
    ReadModelStatus ReadModel,
    IReadOnlyList<CheckpointVerification> Checkpoints)
{
    /// <summary>
    /// Gets a value indicating whether the chain is intact and the read model and every checkpoint are consistent
    /// with it.
    /// </summary>
    public Boolean IsConsistent => Stream.IsIntact && ReadModel == ReadModelStatus.Matches && Checkpoints.All(c => c.IsConsistent);
}
