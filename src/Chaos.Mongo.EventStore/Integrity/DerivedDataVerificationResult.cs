// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// The result of comparing a stream's derived data, its read model and checkpoints, with the stream.
/// </summary>
public sealed record DerivedDataVerificationResult
{
    /// <summary>
    /// Gets the result for each stored checkpoint, in version order; empty for a broken chain.
    /// </summary>
    public required IReadOnlyList<CheckpointVerification> Checkpoints { get; init; }

    /// <summary>
    /// Gets whether the read model and the states of the checkpoints record the same creation time. The creation time
    /// records when the first event was appended and cannot be derived from the events, so only this agreement is
    /// checked; it is <c>true</c> for a broken chain, whose derived data is not compared.
    /// </summary>
    public required Boolean CreationTimeConsistent { get; init; }

    /// <summary>
    /// Gets a value indicating whether the chain is intact, the read model and every checkpoint are consistent with
    /// it, and they agree on the creation time.
    /// </summary>
    public Boolean IsConsistent => Stream.IsIntact && ReadModel == ReadModelStatus.Matches && Checkpoints.All(c => c.IsConsistent) && CreationTimeConsistent;

    /// <summary>
    /// Gets how the stored read model compares with the replayed state.
    /// </summary>
    public required ReadModelStatus ReadModel { get; init; }

    /// <summary>
    /// Gets the verification of the stream's chain; derived data is only compared for an intact chain.
    /// </summary>
    public required StreamVerificationResult Stream { get; init; }
}
