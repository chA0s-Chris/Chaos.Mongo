// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore;

/// <summary>
/// Wraps an aggregate snapshot with a composite key for checkpoint storage.
/// Each checkpoint is uniquely identified by the combination of aggregate ID and the stream position
/// of the last state-changing event it contains.
/// </summary>
/// <typeparam name="TAggregate">The aggregate type.</typeparam>
public sealed class CheckpointDocument<TAggregate> where TAggregate : class, IAggregate, new()
{
    /// <summary>
    /// Gets or sets the composite identifier containing aggregate ID and stream position.
    /// </summary>
    public CheckpointId Id { get; set; }

    /// <summary>
    /// Gets or sets the aggregate revision of the snapshot.
    /// Checkpoints stored before revisions existed lack this element and load with <c>0</c>.
    /// </summary>
    public Int64 Revision { get; set; }

    /// <summary>
    /// Gets or sets the aggregate state at this position.
    /// </summary>
    public TAggregate State { get; set; } = null!;
}
