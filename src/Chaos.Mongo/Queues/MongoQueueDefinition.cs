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
    /// Duration that successfully processed queue items are retained before TTL cleanup removes them.
    /// <c>null</c> means successful items are deleted immediately after processing.
    /// Terminal failed items stay queryable for dead-letter handling.
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
    /// Maximum number of retries for failed queue items before they become terminal.
    /// <c>null</c> means failed items keep retrying after lease expiry.
    /// </summary>
    public Int32? MaxRetries { get; init; } = MongoDefaults.QueueMaxRetries;

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
