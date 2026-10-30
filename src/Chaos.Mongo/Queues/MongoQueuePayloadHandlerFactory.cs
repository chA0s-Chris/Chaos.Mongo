// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Queues;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Provides a factory implementation for creating payload handlers from the dependency injection container.
/// </summary>
internal class MongoQueuePayloadHandlerFactory : IMongoQueuePayloadHandlerFactory
{
    /// <inheritdoc/>
    public IMongoQueuePayloadHandler<TPayload> CreateHandler<TPayload>(IServiceProvider serviceProvider)
        where TPayload : class, new()
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var payloadHandler = serviceProvider.GetService<IMongoQueuePayloadHandler<TPayload>>() ??
                             throw new InvalidOperationException($"No handler for queue payload {typeof(TPayload)} registered.");

        return payloadHandler;
    }
}
