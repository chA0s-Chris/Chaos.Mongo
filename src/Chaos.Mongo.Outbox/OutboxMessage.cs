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
    /// Initializes a new instance of the <see cref="OutboxMessage"/> class.
    /// </summary>
    internal OutboxMessage() { }

    /// <summary>
    /// Gets or sets the number of times this message has been claimed for processing, not the number of
    /// failures. Every lock claim increments this, including a lease-expiry reclaim after a crash, so a
    /// message that succeeds on its very first claim has <c>AttemptCount == 1</c>.
    /// </summary>
    public Int64 AttemptCount { get; internal set; }

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
    /// Gets or sets the processing state of the message.
    /// </summary>
    public OutboxMessageState State { get; internal init; } = OutboxMessageState.Pending;

    /// <summary>
    /// Gets or sets the message type discriminator (e.g., "OrderCreated").
    /// Set automatically from the discriminator registered via the builder API.
    /// </summary>
    public String Type { get; internal init; } = String.Empty;

    /// <summary>
    /// Creates an outbox message as an <see cref="IOutboxPublisher"/> receives it, for tests of a publisher.
    /// </summary>
    /// <remarks>
    /// The outbox creates the messages it stores itself. The created message is claimed, like one the processor hands
    /// to a publisher: <see cref="IsLocked"/> is <c>true</c>, <see cref="LockedUtc"/> is the current UTC time and
    /// <see cref="LockId"/> is a newly generated value. The other members describing the processing state keep their
    /// default values.
    /// </remarks>
    /// <param name="type">The message type discriminator.</param>
    /// <param name="payload">The payload as a raw BSON document.</param>
    /// <param name="correlationId">An optional correlation identifier.</param>
    /// <param name="id">The document identifier. Defaults to a newly generated identifier.</param>
    /// <param name="createdUtc">The UTC time the message was written to the outbox. Defaults to the current time.</param>
    /// <param name="attemptCount">
    /// The number of times the message was claimed. Defaults to <c>1</c>, which is what a publisher sees on the first
    /// delivery attempt.
    /// </param>
    /// <returns>The outbox message.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> or <paramref name="payload"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="type"/> is empty or whitespace, or the <see cref="DateTime.Kind"/> of
    /// <paramref name="createdUtc"/> is not <see cref="DateTimeKind.Utc"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="attemptCount"/> is less than <c>1</c>.</exception>
    public static OutboxMessage Create(String type,
                                       BsonDocument payload,
                                       String? correlationId = null,
                                       ObjectId? id = null,
                                       DateTime? createdUtc = null,
                                       Int64 attemptCount = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentOutOfRangeException.ThrowIfLessThan(attemptCount, 1);
        if (createdUtc is { Kind: not DateTimeKind.Utc })
        {
            throw new ArgumentException("The creation time must be a UTC time.", nameof(createdUtc));
        }

        var now = DateTime.UtcNow;
        return new OutboxMessage
        {
            AttemptCount = attemptCount,
            CorrelationId = correlationId,
            CreatedUtc = createdUtc ?? now,
            Id = id ?? ObjectId.GenerateNewId(),
            IsLocked = true,
            LockedUtc = now,
            LockId = Guid.NewGuid().ToString(),
            Payload = payload,
            Type = type
        };
    }

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
