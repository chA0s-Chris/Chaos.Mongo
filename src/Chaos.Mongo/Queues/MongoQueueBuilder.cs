// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Queues;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Provides a fluent builder for configuring and registering MongoDB queues.
/// </summary>
/// <typeparam name="TPayload">The type of payload stored in the queue.</typeparam>
public sealed class MongoQueueBuilder<TPayload>
    where TPayload : class, new()
{
    private readonly Type _payloadType = typeof(TPayload);
    private readonly IServiceCollection _services;
    private Boolean? _autoStartSubscription;
    private String? _collectionName;
    private Boolean _isRegistered;
    private Func<IServiceProvider, IMongoQueuePayloadHandler<TPayload>>? _payloadHandlerFactory;
    private Type? _payloadHandlerType;
    private Int32? _queryLimit;

    /// <summary>
    /// Initializes a new instance of the <see cref="MongoQueueBuilder{TPayload}"/> class.
    /// </summary>
    /// <param name="services">The service collection to register the queue with.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is null.</exception>
    public MongoQueueBuilder(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <summary>
    /// Registers a MongoDB queue with the given payload type and configuration.
    /// </summary>
    /// <remarks>
    /// The queue is registered as a singleton service and uses the provided handler factory to create a payload handler.
    /// If no collection name is specified, a default name is generated.
    /// <see cref="RegisterQueue"/> is automatically called when <see cref="MongoBuilder.WithQueue"/> is used.
    /// </remarks>
    public void RegisterQueue()
    {
        if (_isRegistered)
        {
            return;
        }

        Validate();

        // Register the payload handler
        if (_payloadHandlerType is not null)
        {
            _services.AddTransient(typeof(IMongoQueuePayloadHandler<TPayload>), _payloadHandlerType);
        }
        else if (_payloadHandlerFactory is not null)
        {
            _services.AddTransient(_payloadHandlerFactory);
        }
        else
        {
            // This should never happen due to Validate(), but adding for safety
            throw new InvalidOperationException("Payload handler type or factory must be specified.");
        }

        var queueDefinition = new MongoQueueDefinition
        {
            CollectionName = _collectionName ?? String.Empty,
            PayloadType = _payloadType,
            QueryLimit = _queryLimit ?? MongoDefaults.QueryLimit,
            PayloadHandlerType = typeof(IMongoQueuePayloadHandler<TPayload>),
            AutoStartSubscription = _autoStartSubscription ?? MongoDefaults.AutoStartSubscription
        };

        _services.AddSingleton<IMongoQueue<TPayload>>(serviceProvider =>
        {
            var finalQueueDefinition = queueDefinition;
            if (String.IsNullOrEmpty(queueDefinition.CollectionName))
            {
                var collectionNameGenerator = serviceProvider.GetRequiredService<IMongoQueueCollectionNameGenerator>();
                var collectionName = collectionNameGenerator.GenerateQueueCollectionName(queueDefinition.PayloadType);
                finalQueueDefinition = queueDefinition with
                {
                    CollectionName = collectionName
                };
            }

            var subscriptionFactory = serviceProvider.GetRequiredService<IMongoQueueSubscriptionFactory>();
            var publisher = serviceProvider.GetRequiredService<IMongoQueuePublisher>();
            return new MongoQueue<TPayload>(finalQueueDefinition, subscriptionFactory, publisher);
        });

        _services.AddSingleton<IMongoQueue>(serviceProvider =>
                                                serviceProvider.GetRequiredService<IMongoQueue<TPayload>>());

        _isRegistered = true;
    }

    /// <summary>
    /// Configures the queue to automatically start its subscription during application startup.
    /// </summary>
    /// <returns>This builder instance for method chaining.</returns>
    public MongoQueueBuilder<TPayload> WithAutoStartSubscription()
    {
        _autoStartSubscription = true;
        return this;
    }

    /// <summary>
    /// Configures the queue to use a specific collection name.
    /// </summary>
    /// <remarks>
    /// If no collection name is specified, a default name is generated based on the payload type using
    /// <see cref="IMongoQueueCollectionNameGenerator"/>.
    /// </remarks>
    /// <param name="collectionName">The name of the MongoDB collection to use for the queue.</param>
    /// <returns>This builder instance for method chaining.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="collectionName"/> is null or whitespace.</exception>
    public MongoQueueBuilder<TPayload> WithCollectionName(String collectionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        _collectionName = collectionName;
        return this;
    }

    /// <summary>
    /// Configures the queue to not automatically start its subscription during application startup.
    /// </summary>
    /// <remarks>
    /// The subscription can be started manually using <see cref="IMongoQueue.StartSubscriptionAsync"/>.
    /// This is the default behavior.
    /// </remarks>
    /// <returns>This builder instance for method chaining.</returns>
    public MongoQueueBuilder<TPayload> WithoutAutoStartSubscription()
    {
        _autoStartSubscription = false;
        return this;
    }

    /// <summary>
    /// Configures the queue to use a specific payload handler type.
    /// </summary>
    /// <typeparam name="TPayloadHandler">The type of payload handler to use.</typeparam>
    /// <returns>This builder instance for method chaining.</returns>
    public MongoQueueBuilder<TPayload> WithPayloadHandler<TPayloadHandler>()
        where TPayloadHandler : class, IMongoQueuePayloadHandler<TPayload>
    {
        _payloadHandlerType = typeof(TPayloadHandler);
        return this;
    }

    /// <summary>
    /// Configures the queue to use a specific payload handler type.
    /// </summary>
    /// <param name="payloadHandlerType">The type of payload handler to use.</param>
    /// <returns>This builder instance for method chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="payloadHandlerType"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="payloadHandlerType"/> is not a non-abstract
    /// class implementing <see cref="IMongoQueuePayloadHandler{TPayload}"/>.
    /// </exception>
    public MongoQueueBuilder<TPayload> WithPayloadHandler(Type payloadHandlerType)
    {
        ArgumentNullException.ThrowIfNull(payloadHandlerType);

        if (!payloadHandlerType.IsClass ||
            payloadHandlerType.IsAbstract ||
            !typeof(IMongoQueuePayloadHandler<TPayload>).IsAssignableFrom(payloadHandlerType))
        {
            throw new InvalidOperationException(
                $"Payload handler type must be a non-abstract class implementing {nameof(IMongoQueuePayloadHandler<>)}.");
        }

        _payloadHandlerType = payloadHandlerType;
        return this;
    }

    /// <summary>
    /// Configures the queue to use a specific payload handler factory.
    /// </summary>
    /// <param name="payloadHandlerFactory">The factory for creating payload handlers.</param>
    /// <returns>This builder instance for method chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="payloadHandlerFactory"/> is null.</exception>
    public MongoQueueBuilder<TPayload> WithPayloadHandler(
        Func<IServiceProvider, IMongoQueuePayloadHandler<TPayload>> payloadHandlerFactory)
    {
        ArgumentNullException.ThrowIfNull(payloadHandlerFactory);
        _payloadHandlerFactory = payloadHandlerFactory;
        return this;
    }

    /// <summary>
    /// Configures the queue to use a specific maximum number of queue items to fetch and process in a single query.
    /// </summary>
    /// <remarks>
    /// The default query limit is <see cref="MongoDefaults.QueryLimit"/>
    /// </remarks>
    /// <param name="queryLimit">The maximum number of queue items to fetch and process in a single query.</param>
    /// <returns>This builder instance for method chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="queryLimit"/> is less than or equal to 0.</exception>
    public MongoQueueBuilder<TPayload> WithQueryLimit(Int32 queryLimit)
    {
        if (queryLimit <= 0)
        {
            throw new ArgumentException("Query limit must be greater than 0.", nameof(queryLimit));
        }

        _queryLimit = queryLimit;
        return this;
    }

    internal void Validate()
    {
        if (_services.Any(s => s.ServiceType == typeof(IMongoQueue<TPayload>)))
        {
            throw new InvalidOperationException(
                $"A registration for a MongoDB queue with payload {typeof(TPayload).Name} already exists.");
        }

        if (_payloadHandlerType is null && _payloadHandlerFactory is null)
        {
            throw new InvalidOperationException("Payload handler type or factory must be specified.");
        }

        if (_payloadHandlerType is not null && _payloadHandlerFactory is not null)
        {
            throw new InvalidOperationException("Payload handler type and factory cannot be specified together.");
        }
    }
}
