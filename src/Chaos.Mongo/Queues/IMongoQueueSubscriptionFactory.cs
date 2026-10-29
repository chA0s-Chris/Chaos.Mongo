// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Queues;

/// <summary>
/// Defines a factory for creating and starting queue subscriptions.
/// </summary>
internal interface IMongoQueueSubscriptionFactory
{
    /// <summary>
    /// Creates a new queue subscription and starts it immediately.
    /// </summary>
    /// <typeparam name="TPayload">The type of payload to process.</typeparam>
    /// <param name="queueDefinition">The queue configuration.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes with the started subscription.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="queueDefinition"/> is null.</exception>
    Task<IMongoQueueSubscription<TPayload>> CreateAndRunAsync<TPayload>(MongoQueueDefinition queueDefinition, CancellationToken cancellationToken = default)
        where TPayload : class, new();
}
