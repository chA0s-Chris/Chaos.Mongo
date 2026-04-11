// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Queues;

/// <summary>
/// Definition of a MongoDB queue.
/// </summary>
public record MongoQueueDefinition
{
    /// <summary>
    /// Whether to automatically start the subscription.
    /// </summary>
    public required Boolean AutoStartSubscription { get; init; }

    /// <summary>
    /// Duration that closed queue items are retained before TTL cleanup removes them.
    /// <c>null</c> means items are deleted immediately after successful processing.
    /// </summary>
    public TimeSpan? ClosedItemRetention { get; init; } = MongoDefaults.QueueClosedItemRetention;

    /// <summary>
    /// Name of the collection.
    /// </summary>
    public required String CollectionName { get; init; }

    /// <summary>
    /// Duration that a queue item lock remains valid before another consumer may recover it.
    /// </summary>
    public required TimeSpan LockLeaseTime { get; init; }

    /// <summary>
    /// Type of the payload handler.
    /// </summary>
    public required Type PayloadHandlerType { get; init; }

    /// <summary>
    /// Type of the payload.
    /// </summary>
    public required Type PayloadType { get; init; }

    /// <summary>
    /// Number of queue payloads that will be processed sequentially before
    /// the remaining payloads will be sorted again.
    /// </summary>
    public required Int32 QueryLimit { get; init; }
}
