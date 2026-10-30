// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Queues;

/// <summary>
/// Defines a factory for creating payload handlers.
/// </summary>
internal interface IMongoQueuePayloadHandlerFactory
{
    /// <summary>
    /// Creates a handler for the specified payload type.
    /// </summary>
    /// <typeparam name="TPayload">The type of payload to handle.</typeparam>
    /// <param name="serviceProvider">The service provider to resolve the handler from.</param>
    /// <returns>A handler instance for the specified payload type.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no handler is registered for the payload type.</exception>
    IMongoQueuePayloadHandler<TPayload> CreateHandler<TPayload>(IServiceProvider serviceProvider) where TPayload : class, new();
}
