// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox;

using MongoDB.Bson;
using MongoDB.Bson.Serialization;

/// <summary>
/// Represents an outbox message document as stored in MongoDB.
/// </summary>
/// <remarks>
/// The payload is stored as a <see cref="BsonDocument"/> field, keeping the outbox agnostic of payload types
/// at the storage level. Use <see cref="DeserializePayload{TPayload}"/> for typed access.
/// </remarks>
public sealed class OutboxMessage
{
    /// <summary>
    /// Gets or sets an optional correlation identifier for tracing across systems.
    /// </summary>
    public String? CorrelationId { get; internal init; }

    /// <summary>
    /// Gets or sets the timestamp when the message was written to the outbox.
    /// </summary>
    public DateTime CreatedUtc { get; internal set; }

    /// <summary>
    /// Gets or sets the last error message from a failed processing attempt.
    /// </summary>
    public String? Error { get; internal set; }

    /// <summary>
    /// Gets or sets the timestamp when the message was permanently marked as failed.
    /// Used for TTL cleanup of failed messages.
    /// </summary>
    public DateTime? FailedUtc { get; internal set; }

    /// <summary>
    /// Gets or sets the MongoDB document identifier.
    /// Used as a tie-breaker after retry scheduling and lock timing when sorting eligible messages.
    /// </summary>
    public ObjectId Id { get; internal set; }

    /// <summary>
    /// Gets or sets whether the message is currently being processed.
    /// </summary>
    public Boolean IsLocked { get; internal set; }

    /// <summary>
    /// Gets or sets the opaque claim token identifying the processor's current lock ownership.
    /// A new value is generated every time a message is claimed or reclaimed.
    /// </summary>
    public String? LockId { get; internal set; }

    /// <summary>
    /// Gets or sets the timestamp when the lock was acquired. Used for stale lock detection.
    /// </summary>
    public DateTime? LockedUtc { get; internal set; }

    /// <summary>
    /// Gets or sets when the message becomes eligible for another processing attempt after a failure.
    /// <c>null</c> means the message is eligible immediately.
    /// </summary>
    public DateTime? NextAttemptUtc { get; internal set; }

    /// <summary>
    /// Gets or sets the payload as a raw BSON document.
    /// </summary>
    public BsonDocument Payload { get; internal init; } = [];

    /// <summary>
    /// Gets or sets the timestamp when the message was successfully published.
    /// </summary>
    public DateTime? ProcessedUtc { get; internal set; }

    /// <summary>
    /// Gets or sets the number of failed processing attempts so far.
    /// </summary>
    public Int32 RetryCount { get; internal set; }

    /// <summary>
    /// Gets or sets the processing state of the message.
    /// </summary>
    public OutboxMessageState State { get; internal init; } = OutboxMessageState.Pending;

    /// <summary>
    /// Gets or sets the message type discriminator (e.g., "OrderCreated").
    /// Set automatically from the discriminator registered via the builder API.
    /// </summary>
    public String Type { get; internal init; } = String.Empty;

    /// <summary>
    /// Deserializes the <see cref="Payload"/> to a typed payload object.
    /// </summary>
    /// <typeparam name="TPayload">The payload type to deserialize to.</typeparam>
    /// <returns>The deserialized payload.</returns>
    /// <example>
    ///     <code>
    ///         var order = message.DeserializePayload&lt;OrderCreatedMessage&gt;();
    ///     </code>
    /// </example>
    public TPayload DeserializePayload<TPayload>() => BsonSerializer.Deserialize<TPayload>(Payload);
}
