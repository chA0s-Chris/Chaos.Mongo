// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Queues;

using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

/// <summary>
/// Representation of a MongoDB queue item.
/// </summary>
[BsonIgnoreExtraElements]
public class MongoQueueItem
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MongoQueueItem"/> class.
    /// </summary>
    internal MongoQueueItem() { }

    /// <summary>
    /// Number of times this item has been acquired/claimed for processing, not the number of failures. Every
    /// lock acquisition increments this, including a lease-expiry reclaim after a crash, so an item that
    /// succeeds on its very first acquisition has <c>AttemptCount == 1</c>.
    /// </summary>
    public Int64 AttemptCount { get; internal set; }

    /// <summary>
    /// Timestamp the queue item was closed.
    /// </summary>
    [BsonIgnoreIfNull]
    public DateTime? ClosedUtc { get; internal set; }

    /// <summary>
    /// Timestamp the queue item was created.
    /// </summary>
    public required DateTime CreatedUtc { get; init; }

    /// <summary>
    /// Timestamp a resolved failure (a thrown handler exception, a DI resolution failure, or a poison-payload
    /// read failure) deferred this item's retry, left locked until its lease naturally expires. Distinguishes an
    /// ordinary, already-logged retry from a lock actually abandoned by a crash: only the latter should record
    /// <c>chaos.mongo.queue.lock.recovered</c> on its next reacquisition. Cleared whenever the next acquisition is
    /// granted, so a missing value (including on documents written before this property existed) behaves like a
    /// lock that was never deliberately deferred.
    /// </summary>
    [BsonIgnoreIfNull]
    public DateTime? DeferredUtc { get; internal set; }

    /// <summary>
    /// The unique identifier of the queue item.
    /// </summary>
    public required ObjectId Id { get; init; }

    /// <summary>
    /// <c>true</c> if queue item is closed.
    /// </summary>
    public Boolean IsClosed { get; internal set; }

    /// <summary>
    /// <c>true</c> if queue item is locked.
    /// </summary>
    /// <remarks>
    /// Locking is used to guarantee exclusive access.
    /// </remarks>
    public Boolean IsLocked { get; internal set; }

    /// <summary>
    /// <c>true</c> if the queue item exhausted its retry policy and is now terminal. Reached either when the last
    /// attempt <see cref="MongoQueueDefinition.MaxRetries"/> allows is resolved by a handler failure, a
    /// deserialization failure, or a DI failure, or when a lease-expiry reclaim finds that the existing
    /// <see cref="AttemptCount"/> already exceeds <see cref="MongoQueueDefinition.MaxRetries"/> — in which case
    /// the reclaim transitions the item directly to terminal without granting it a further lock.
    /// </summary>
    public Boolean IsTerminal { get; internal set; }

    /// <summary>
    /// Timestamp the queue item was locked.
    /// </summary>
    [BsonIgnoreIfNull]
    public DateTime? LockedUtc { get; internal set; }
}

/// <summary>
/// Representation of a MongoDB queue item with strongly-typed payload.
/// </summary>
/// <typeparam name="TPayload">The type of payload stored in the queue item.</typeparam>
[BsonIgnoreExtraElements]
public sealed class MongoQueueItem<TPayload> : MongoQueueItem
    where TPayload : class, new()
{
    /// <summary>
    /// The strongly-typed payload of the queue item.
    /// </summary>
    public required TPayload Payload { get; init; }

    /// <summary>
    /// The full type name of <typeparamref name="TPayload"/>, stored alongside the payload for deserialization.
    /// </summary>
    public String PayloadType { get; init; } = typeof(TPayload).FullName ??
                                               throw new InvalidOperationException(
                                                   $"Type {typeof(TPayload)} does not have a FullName, which is required for payload deserialization.");
}
