// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Queues;

/// <summary>
/// Defines a publisher for adding payloads to MongoDB queue collections.
/// </summary>
public interface IMongoQueuePublisher
{
    /// <summary>
    /// Publishes a payload to the specified queue.
    /// </summary>
    /// <typeparam name="TPayload">The type of payload to publish.</typeparam>
    /// <param name="queueDefinition">The queue configuration.</param>
    /// <param name="payload">The payload to publish.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the payload has been published.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="queueDefinition"/> or <paramref name="payload"/> is
    /// null.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the payload type does not match the queue payload type or
    /// collection name is empty.
    /// </exception>
    Task PublishAsync<TPayload>(MongoQueueDefinition queueDefinition, TPayload payload, CancellationToken cancellationToken)
        where TPayload : class, new();
}
