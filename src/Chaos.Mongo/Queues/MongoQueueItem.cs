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
    /// Timestamp the queue item was closed.
    /// </summary>
    [BsonIgnoreIfNull]
    public DateTime? ClosedUtc { get; set; }

    /// <summary>
    /// Timestamp the queue item was created.
    /// </summary>
    public required DateTime CreatedUtc { get; init; }

    /// <summary>
    /// The unique identifier of the queue item.
    /// </summary>
    public required ObjectId Id { get; init; }

    /// <summary>
    /// <c>true</c> if queue item is closed.
    /// </summary>
    public Boolean IsClosed { get; set; }

    /// <summary>
    /// <c>true</c> if queue item is locked.
    /// </summary>
    /// <remarks>
    /// Locking is used to guarantee exclusive access.
    /// </remarks>
    public Boolean IsLocked { get; set; }

    /// <summary>
    /// <c>true</c> if queue item exhausted its retry policy and is now terminal.
    /// </summary>
    public Boolean IsTerminal { get; set; }

    /// <summary>
    /// Timestamp the queue item was locked.
    /// </summary>
    [BsonIgnoreIfNull]
    public DateTime? LockedUtc { get; set; }

    /// <summary>
    /// Number of failed processing attempts for this queue item.
    /// </summary>
    public Int32 RetryCount { get; set; }
}

/// <summary>
/// Representation of a MongoDB queue item with strongly-typed payload.
/// </summary>
/// <typeparam name="TPayload">The type of payload stored in the queue item.</typeparam>
[BsonIgnoreExtraElements]
public class MongoQueueItem<TPayload> : MongoQueueItem
    where TPayload : class, new()
{
    public required TPayload Payload { get; init; }

    public String PayloadType { get; init; } = typeof(TPayload).FullName ??
                                               throw new InvalidOperationException(
                                                   $"Type {typeof(TPayload)} does not have a FullName, which is required for payload deserialization.");
}
