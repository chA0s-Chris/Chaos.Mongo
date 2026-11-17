// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Queues;

/// <summary>
/// Defines a handler for processing queue payloads.
/// </summary>
/// <typeparam name="TPayload">The type of payload to handle.</typeparam>
public interface IMongoQueuePayloadHandler<in TPayload>
    where TPayload : class
{
    /// <summary>
    /// Handles the specified payload asynchronously.
    /// </summary>
    /// <param name="payload">The payload to process.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the payload has been processed.</returns>
    Task HandlePayloadAsync(TPayload payload, CancellationToken cancellationToken = default);
}
