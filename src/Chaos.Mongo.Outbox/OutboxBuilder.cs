// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox;

using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using System.Collections.Immutable;

/// <summary>
/// Provides a fluent builder for configuring the transactional outbox.
/// </summary>
public sealed class OutboxBuilder
{
    private Boolean _autoStartProcessor;
    private Int32 _batchSize = OutboxOptions.DefaultBatchSize;
    private String _collectionName = OutboxOptions.DefaultCollectionName;
    private TimeSpan _lockLeaseTime = OutboxOptions.DefaultLockLeaseTime;
    private Int32 _maxRetries = OutboxOptions.DefaultMaxRetries;
    private TimeSpan _pollingInterval = OutboxOptions.DefaultPollingInterval;
    private FilterDefinition<OutboxMessage>? _processingFilter;
    private TimeSpan? _retentionPeriod;
    private TimeSpan _retryBackoffInitialDelay = OutboxOptions.DefaultRetryBackoffInitialDelay;
    private TimeSpan _retryBackoffMaxDelay = OutboxOptions.DefaultRetryBackoffMaxDelay;

    /// <summary>
    /// Initializes a new instance of the <see cref="OutboxBuilder"/> class.
    /// </summary>
    internal OutboxBuilder() { }

    /// <summary>
    /// Gets the registered message types mapped to their discriminator names.
    /// </summary>
    internal Dictionary<Type, String> MessageTypes { get; } = new();

    /// <summary>
    /// Gets the publisher factory configured by
    /// <see cref="WithPublisher(Func{IServiceProvider, IOutboxPublisher}, ServiceLifetime)"/>.
    /// </summary>
    /// <remarks>
    /// Once a publisher is configured, exactly one of <see cref="PublisherType"/> and <see cref="PublisherFactory"/> is
    /// non-null.
    /// </remarks>
    internal Func<IServiceProvider, IOutboxPublisher>? PublisherFactory { get; private set; }

    /// <summary>
    /// Gets the publisher service lifetime.
    /// </summary>
    internal ServiceLifetime PublisherLifetime { get; private set; } = ServiceLifetime.Transient;

    /// <summary>
    /// Gets the publisher implementation type configured by <see cref="WithPublisher{TPublisher}()"/>.
    /// </summary>
    /// <remarks>
    /// Once a publisher is configured, exactly one of <see cref="PublisherType"/> and <see cref="PublisherFactory"/> is
    /// non-null.
    /// </remarks>
    internal Type? PublisherType { get; private set; }

    /// <summary>
    /// Enables automatic processor startup via the hosted service.
    /// Omit this call to leave processor startup to the caller; defaults to disabled.
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
    /// Sets the lock lease time for stale lock recovery.
    /// Defaults to <c>5 minutes</c>.
    /// </summary>
    /// <param name="leaseTime">The lock lease time.</param>
    /// <returns>This builder instance for method chaining.</returns>
    public OutboxBuilder WithLockLeaseTime(TimeSpan leaseTime)
    {
        if (leaseTime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseTime), "Lock lease time must be positive.");

        _lockLeaseTime = leaseTime;
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
    /// Sets an additional startup-configured filter that narrows message selection and atomic claiming.
    /// </summary>
    /// <param name="filter">The filter, which must not be mutated after configuration.</param>
    /// <returns>This builder instance for method chaining.</returns>
    /// <exception cref="ArgumentNullException">The filter is <c>null</c>.</exception>
    /// <remarks>
    /// Replaces any previously configured processing filter. Omit this call to use normal eligibility rules.
    /// Message-type registration and publisher routing are configured separately.
    /// </remarks>
    public OutboxBuilder WithProcessingFilter(FilterDefinition<OutboxMessage> filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        _processingFilter = filter;
        return this;
    }

    /// <summary>
    /// Registers a factory that creates the user's <see cref="IOutboxPublisher"/> with a specific service lifetime.
    /// </summary>
    /// <param name="factory">The factory creating the publisher.</param>
    /// <param name="lifetime">The service lifetime for the publisher. Defaults to <see cref="ServiceLifetime.Transient"/>.</param>
    /// <returns>This builder instance for method chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <c>null</c>.</exception>
    /// <remarks>
    /// Replaces any previously configured publisher type or factory. The processor resolves the publisher once per
    /// nonempty batch from a fresh scope, so transient and scoped factories receive that scope's provider and may
    /// resolve scoped services. A singleton factory is invoked once with the root provider and must not resolve
    /// scoped services. If the factory returns <c>null</c>, publisher resolution throws an
    /// <see cref="InvalidOperationException"/> naming the outbox.
    /// </remarks>
    public OutboxBuilder WithPublisher(Func<IServiceProvider, IOutboxPublisher> factory,
                                       ServiceLifetime lifetime = ServiceLifetime.Transient)
    {
        ArgumentNullException.ThrowIfNull(factory);
        PublisherFactory = factory;
        PublisherType = null;
        PublisherLifetime = lifetime;
        return this;
    }

    /// <summary>
    /// Registers the user's <see cref="IOutboxPublisher"/> implementation with the default transient lifetime.
    /// </summary>
    /// <typeparam name="TPublisher">The publisher implementation type.</typeparam>
    /// <returns>This builder instance for method chaining.</returns>
    /// <remarks>Replaces any previously configured publisher type or factory.</remarks>
    public OutboxBuilder WithPublisher<TPublisher>()
        where TPublisher : class, IOutboxPublisher
        => WithPublisher<TPublisher>(ServiceLifetime.Transient);

    /// <summary>
    /// Registers the user's <see cref="IOutboxPublisher"/> implementation with a specific service lifetime.
    /// </summary>
    /// <typeparam name="TPublisher">The publisher implementation type.</typeparam>
    /// <param name="lifetime">The service lifetime for the publisher.</param>
    /// <returns>This builder instance for method chaining.</returns>
    /// <remarks>Replaces any previously configured publisher type or factory.</remarks>
    public OutboxBuilder WithPublisher<TPublisher>(ServiceLifetime lifetime)
        where TPublisher : class, IOutboxPublisher
    {
        PublisherType = typeof(TPublisher);
        PublisherFactory = null;
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
    /// Defaults to a <c>5</c> second initial delay and a <c>5</c> minute maximum delay.
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

    /// <summary>
    /// Builds the immutable <see cref="OutboxOptions"/> from the current builder state.
    /// </summary>
    /// <returns>A frozen <see cref="OutboxOptions"/> instance.</returns>
    internal OutboxOptions Build() => Build("Default");

    internal OutboxOptions Build(String identity)
    {
        return new OutboxOptions
        {
            Identity = identity,
            AutoStartProcessor = _autoStartProcessor,
            BatchSize = _batchSize,
            CollectionName = _collectionName,
            LockLeaseTime = _lockLeaseTime,
            MaxRetries = _maxRetries,
            MessageTypeLookup = MessageTypes.ToImmutableDictionary(),
            PollingInterval = _pollingInterval,
            ProcessingFilter = _processingFilter,
            RetentionPeriod = _retentionPeriod,
            RetryBackoffInitialDelay = _retryBackoffInitialDelay,
            RetryBackoffMaxDelay = _retryBackoffMaxDelay
        };
    }

    /// <summary>
    /// Validates the builder configuration.
    /// </summary>
    internal void Validate()
    {
        if (PublisherType is null && PublisherFactory is null)
        {
            throw new InvalidOperationException(
                "An IOutboxPublisher implementation must be registered. Use WithPublisher<T>() or WithPublisher(Func<IServiceProvider, IOutboxPublisher>) to register one.");
        }

        if (MessageTypes.Count == 0)
        {
            throw new InvalidOperationException(
                "At least one message type must be registered. Use WithMessage<TPayload>() to register message types.");
        }
    }
}
