// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Queues;

/// <summary>
/// Provides a MongoDB-based queue implementation for publishing and subscribing to strongly-typed messages.
/// </summary>
/// <typeparam name="TPayload">The type of payload stored in the queue.</typeparam>
internal class MongoQueue<TPayload> : IMongoQueue<TPayload> where TPayload : class, new()
{
    private readonly Object _disposeLock = new();
    private readonly IMongoQueuePublisher _publisher;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly IMongoQueueSubscriptionFactory _subscriptionFactory;
    private Boolean _disposed;
    private IMongoQueueSubscription<TPayload>? _subscription;

    /// <summary>
    /// Initializes a new instance of the <see cref="MongoQueue{TPayload}"/> class.
    /// </summary>
    /// <param name="queueDefinition">The queue configuration.</param>
    /// <param name="subscriptionFactory">The factory for creating subscriptions.</param>
    /// <param name="publisher">The publisher for adding items to the queue.</param>
    /// <exception cref="ArgumentNullException">Thrown when any parameter is null.</exception>
    public MongoQueue(MongoQueueDefinition queueDefinition,
                      IMongoQueueSubscriptionFactory subscriptionFactory,
                      IMongoQueuePublisher publisher)
    {
        ArgumentNullException.ThrowIfNull(queueDefinition);
        ArgumentNullException.ThrowIfNull(subscriptionFactory);
        ArgumentNullException.ThrowIfNull(publisher);
        QueueDefinition = queueDefinition;
        _subscriptionFactory = subscriptionFactory;
        _publisher = publisher;
    }

    /// <inheritdoc/>
    public Boolean IsRunning => _subscription?.IsActive ?? false;

    /// <inheritdoc/>
    public MongoQueueDefinition QueueDefinition { get; }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        lock (_disposeLock)
        {
            if (_disposed)
                return;

            _disposed = true;
        }

        await StopSubscriptionAsync();
        _semaphore.Dispose();
    }

    /// <inheritdoc/>
    public Task PublishAsync(Object payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var payloadType = payload.GetType();

        if (!typeof(TPayload).IsAssignableFrom(payloadType))
        {
            throw new InvalidOperationException($"Payload type {payloadType} is not assignable to {typeof(TPayload)}.");
        }

        return PublishAsync((TPayload)payload, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task StartSubscriptionAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            if (_subscription is not null)
            {
                if (_subscription.IsActive)
                {
                    // subscription is already running

                    return;
                }

                await _subscription.DisposeAsync();
            }

            _subscription = await _subscriptionFactory.CreateAndRunAsync<TPayload>(QueueDefinition, cancellationToken);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc/>
    public async Task StopSubscriptionAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            if (_subscription is not null)
            {
                if (_subscription.IsActive)
                {
                    await _subscription.StopAsync(cancellationToken);
                }

                await _subscription.DisposeAsync();
                _subscription = null;
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc/>
    public Task PublishAsync(TPayload payload, CancellationToken cancellationToken = default)
        => _publisher.PublishAsync(QueueDefinition, payload, cancellationToken);
}
