// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Queues;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Provides a factory implementation for creating payload handlers from the dependency injection container.
/// </summary>
internal class MongoQueuePayloadHandlerFactory : IMongoQueuePayloadHandlerFactory
{
    private readonly IServiceProvider _serviceProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="MongoQueuePayloadHandlerFactory"/> class.
    /// </summary>
    /// <param name="serviceProvider">The service provider for resolving handlers.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="serviceProvider"/> is null.</exception>
    public MongoQueuePayloadHandlerFactory(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        _serviceProvider = serviceProvider;
    }

    /// <inheritdoc/>
    public IMongoQueuePayloadHandler<TPayload> CreateHandler<TPayload>()
        where TPayload : class, new()
    {
        var payloadHandler = _serviceProvider.GetService<IMongoQueuePayloadHandler<TPayload>>() ??
                             throw new InvalidOperationException($"No handler for queue payload {typeof(TPayload)} registered.");

        return payloadHandler;
    }
}
