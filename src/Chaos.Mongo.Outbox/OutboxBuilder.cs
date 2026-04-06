// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox;

using Microsoft.Extensions.DependencyInjection;
using System.Collections.Immutable;

/// <summary>
/// Provides a fluent builder for configuring the transactional outbox.
/// </summary>
public sealed class OutboxBuilder
{
    private Boolean _autoStartProcessor;
    private Int32 _batchSize = OutboxOptions.DefaultBatchSize;
    private String _collectionName = OutboxOptions.DefaultCollectionName;
    private TimeSpan _lockTimeout = OutboxOptions.DefaultLockTimeout;
    private Int32 _maxRetries = OutboxOptions.DefaultMaxRetries;
    private TimeSpan _pollingInterval = OutboxOptions.DefaultPollingInterval;
    private TimeSpan? _retentionPeriod;
    private TimeSpan _retryBackoffInitialDelay = OutboxOptions.DefaultRetryBackoffInitialDelay;
    private TimeSpan _retryBackoffMaxDelay = OutboxOptions.DefaultRetryBackoffMaxDelay;

    /// <summary>
    /// Gets the publisher service lifetime.
    /// </summary>
    public ServiceLifetime PublisherLifetime { get; private set; } = ServiceLifetime.Transient;

    /// <summary>
    /// Gets the publisher implementation type.
    /// </summary>
    public Type? PublisherType { get; private set; }

    /// <summary>
    /// Gets the registered message types mapped to their discriminator names.
    /// </summary>
    internal Dictionary<Type, String> MessageTypes { get; } = new();

    /// <summary>
    /// Builds the immutable <see cref="OutboxOptions"/> from the current builder state.
    /// </summary>
    /// <returns>A frozen <see cref="OutboxOptions"/> instance.</returns>
    public OutboxOptions Build()
    {
        return new()
        {
            AutoStartProcessor = _autoStartProcessor,
            BatchSize = _batchSize,
            CollectionName = _collectionName,
            LockTimeout = _lockTimeout,
            MaxRetries = _maxRetries,
            MessageTypeLookup = MessageTypes.ToImmutableDictionary(),
            PollingInterval = _pollingInterval,
            RetentionPeriod = _retentionPeriod,
            RetryBackoffInitialDelay = _retryBackoffInitialDelay,
            RetryBackoffMaxDelay = _retryBackoffMaxDelay
        };
    }

    /// <summary>
    /// Validates the builder configuration.
    /// </summary>
    public void Validate()
    {
        if (PublisherType is null)
        {
            throw new InvalidOperationException(
                "An IOutboxPublisher implementation must be registered. Use WithPublisher<T>() to register one.");
        }

        if (MessageTypes.Count == 0)
        {
            throw new InvalidOperationException(
                "At least one message type must be registered. Use WithMessage<TPayload>() to register message types.");
        }
    }

    /// <summary>
    /// Enables automatic processor startup via the hosted service.
    /// </summary>
    /// <returns>This builder instance for method chaining.</returns>
    public OutboxBuilder WithAutoStartProcessor()
    {
        _autoStartProcessor = true;
        return this;
    }

    /// <summary>
    /// Sets the maximum number of messages to fetch and process in a single batch.
    /// Defaults to <c>100</c>.
    /// </summary>
    /// <param name="batchSize">The batch size.</param>
    /// <returns>This builder instance for method chaining.</returns>
    public OutboxBuilder WithBatchSize(Int32 batchSize)
    {
        if (batchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize), "Batch size must be greater than 0.");

        _batchSize = batchSize;
        return this;
    }

    /// <summary>
    /// Sets the name of the MongoDB collection used for outbox messages.
    /// Defaults to <c>"Outbox"</c>.
    /// </summary>
    /// <param name="collectionName">The collection name.</param>
    /// <returns>This builder instance for method chaining.</returns>
    public OutboxBuilder WithCollectionName(String collectionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        _collectionName = collectionName;
        return this;
    }

    /// <summary>
    /// Sets the lock timeout for stale lock recovery.
    /// Defaults to <c>5 minutes</c>.
    /// </summary>
    /// <param name="lockTimeout">The lock timeout.</param>
    /// <returns>This builder instance for method chaining.</returns>
    public OutboxBuilder WithLockTimeout(TimeSpan lockTimeout)
    {
        if (lockTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lockTimeout), "Lock timeout must be positive.");

        _lockTimeout = lockTimeout;
        return this;
    }

    /// <summary>
    /// Sets the maximum number of retry attempts before a message is permanently marked as failed.
    /// Defaults to <c>5</c>.
    /// </summary>
    /// <param name="maxRetries">The maximum retry count.</param>
    /// <returns>This builder instance for method chaining.</returns>
    public OutboxBuilder WithMaxRetries(Int32 maxRetries)
    {
        if (maxRetries < 0)
            throw new ArgumentOutOfRangeException(nameof(maxRetries), "Max retries must be non-negative.");

        _maxRetries = maxRetries;
        return this;
    }

    /// <summary>
    /// Registers a payload type with an explicit discriminator name.
    /// The discriminator is used as the <see cref="OutboxMessage.Type"/> field value.
    /// </summary>
    /// <typeparam name="TPayload">The payload type.</typeparam>
    /// <param name="discriminator">
    /// The discriminator name. If <c>null</c>, defaults to the class name.
    /// </param>
    /// <returns>This builder instance for method chaining.</returns>
    public OutboxBuilder WithMessage<TPayload>(String? discriminator = null)
        where TPayload : class, new()
    {
        var payloadType = typeof(TPayload);
        if (discriminator is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(discriminator);
        else
            discriminator = payloadType.Name;

        MessageTypes[payloadType] = discriminator;
        return this;
    }

    /// <summary>
    /// Sets the interval between polling attempts when no messages are available.
    /// Defaults to <c>5 seconds</c>.
    /// </summary>
    /// <param name="pollingInterval">The polling interval.</param>
    /// <returns>This builder instance for method chaining.</returns>
    public OutboxBuilder WithPollingInterval(TimeSpan pollingInterval)
    {
        if (pollingInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(pollingInterval), "Polling interval must be positive.");

        _pollingInterval = pollingInterval;
        return this;
    }

    /// <summary>
    /// Registers the user's <see cref="IOutboxPublisher"/> implementation with the default transient lifetime.
    /// </summary>
    /// <typeparam name="TPublisher">The publisher implementation type.</typeparam>
    /// <returns>This builder instance for method chaining.</returns>
    public OutboxBuilder WithPublisher<TPublisher>()
        where TPublisher : class, IOutboxPublisher
    {
        PublisherType = typeof(TPublisher);
        PublisherLifetime = ServiceLifetime.Transient;
        return this;
    }

    /// <summary>
    /// Registers the user's <see cref="IOutboxPublisher"/> implementation with a specific service lifetime.
    /// </summary>
    /// <typeparam name="TPublisher">The publisher implementation type.</typeparam>
    /// <param name="lifetime">The service lifetime for the publisher.</param>
    /// <returns>This builder instance for method chaining.</returns>
    public OutboxBuilder WithPublisher<TPublisher>(ServiceLifetime lifetime)
        where TPublisher : class, IOutboxPublisher
    {
        PublisherType = typeof(TPublisher);
        PublisherLifetime = lifetime;
        return this;
    }

    /// <summary>
    /// Configures TTL-based cleanup of processed and failed messages.
    /// Omit to retain messages indefinitely.
    /// </summary>
    /// <param name="retentionPeriod">The retention period after which messages are automatically removed.</param>
    /// <returns>This builder instance for method chaining.</returns>
    public OutboxBuilder WithRetentionPeriod(TimeSpan retentionPeriod)
    {
        if (retentionPeriod <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retentionPeriod), "Retention period must be positive.");

        _retentionPeriod = retentionPeriod;
        return this;
    }

    /// <summary>
    /// Configures the exponential retry backoff parameters.
    /// </summary>
    /// <param name="initialDelay">The initial delay between retry attempts.</param>
    /// <param name="maxDelay">The maximum delay cap.</param>
    /// <returns>This builder instance for method chaining.</returns>
    public OutboxBuilder WithRetryBackoff(TimeSpan initialDelay, TimeSpan maxDelay)
    {
        if (initialDelay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(initialDelay), "Initial delay must be positive.");
        if (maxDelay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxDelay), "Max delay must be positive.");

        _retryBackoffInitialDelay = initialDelay;
        _retryBackoffMaxDelay = maxDelay;
        return this;
    }
}
