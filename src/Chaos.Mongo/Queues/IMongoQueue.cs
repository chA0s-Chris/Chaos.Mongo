// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Queues;

/// <summary>
/// Represents a MongoDB-based queue for publishing and subscribing to messages.
/// </summary>
public interface IMongoQueue : IAsyncDisposable
{
    /// <summary>
    /// Gets a value indicating whether the queue subscription is currently running.
    /// </summary>
    Boolean IsRunning { get; }

    /// <summary>
    /// Gets the queue configuration and metadata.
    /// </summary>
    MongoQueueDefinition QueueDefinition { get; }

    /// <summary>
    /// Publishes a payload to the queue.
    /// </summary>
    /// <param name="payload">The payload to publish.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the payload has been published.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="payload"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the payload type is not compatible with the queue.</exception>
    Task PublishAsync(Object payload, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts the queue subscription to begin processing items.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the subscription has started.</returns>
    Task StartSubscriptionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the queue subscription and waits for current processing to complete.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token representing the shutdown timeout.</param>
    /// <returns>A task that completes when the subscription has stopped.</returns>
    Task StopSubscriptionAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a strongly-typed MongoDB-based queue for publishing and subscribing to messages.
/// </summary>
/// <typeparam name="TPayload">The type of payload stored in the queue.</typeparam>
public interface IMongoQueue<in TPayload> : IMongoQueue
    where TPayload : class
{
    /// <summary>
    /// Publishes a strongly-typed payload to the queue.
    /// </summary>
    /// <param name="payload">The payload to publish.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the payload has been published.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="payload"/> is null.</exception>
    Task PublishAsync(TPayload payload, CancellationToken cancellationToken = default);
}
