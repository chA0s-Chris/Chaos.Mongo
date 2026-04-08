// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox;

/// <summary>
/// Delivers outbox messages to an external system (message broker, API, etc.).
/// </summary>
/// <remarks>
///     <para>
///     Implement this interface to deliver messages to your broker of choice (RabbitMQ, Kafka, Azure Service Bus, HTTP,
///     etc.).
///     </para>
///     <para>
///     Throwing an exception signals a failed attempt; the processor will increment <see cref="OutboxMessage.RetryCount"/>
///     and retry later according to the configured retry schedule.
///     </para>
///     <para>
///     The publisher receives the full <see cref="OutboxMessage"/> including <see cref="OutboxMessage.Type"/>,
///     <see cref="OutboxMessage.CorrelationId"/>, and <see cref="OutboxMessage.Payload"/> (as <c>BsonDocument</c>).
///     </para>
/// </remarks>
public interface IOutboxPublisher
{
    /// <summary>
    /// Publishes the specified outbox message to an external system.
    /// </summary>
    /// <param name="message">The outbox message to publish.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes when the message has been published.</returns>
    Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken = default);
}
