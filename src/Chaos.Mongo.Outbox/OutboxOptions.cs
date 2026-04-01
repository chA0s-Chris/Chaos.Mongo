// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox;

using System.Collections.Immutable;

/// <summary>
/// Configuration options for the transactional outbox.
/// </summary>
public sealed class OutboxOptions
{
    /// <summary>
    /// Gets or sets whether the processor should be started automatically via the hosted service.
    /// Defaults to <c>false</c>.
    /// </summary>
    public Boolean AutoStartProcessor { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of messages to fetch and process in a single batch.
    /// Defaults to <c>100</c>.
    /// </summary>
    public Int32 BatchSize { get; set; } = 100;

    /// <summary>
    /// Gets or sets the name of the MongoDB collection used for outbox messages.
    /// Defaults to <c>"Outbox"</c>.
    /// </summary>
    public String CollectionName { get; set; } = "Outbox";

    /// <summary>
    /// Gets or sets the lock timeout after which a locked message is considered stale and can be reclaimed.
    /// Defaults to <c>5 minutes</c>.
    /// </summary>
    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets the maximum number of retry attempts before a message is permanently marked as failed.
    /// Defaults to <c>5</c>.
    /// </summary>
    public Int32 MaxRetries { get; set; } = 5;

    /// <summary>
    /// Gets a frozen lookup from payload type to discriminator, built after configuration is complete.
    /// </summary>
    public ImmutableDictionary<Type, String> MessageTypeLookup { get; set; } = ImmutableDictionary<Type, String>.Empty;

    /// <summary>
    /// Gets the registered message types mapped to their discriminator names.
    /// </summary>
    public Dictionary<Type, String> MessageTypes { get; } = new();

    /// <summary>
    /// Gets or sets the interval between polling attempts when no messages are available.
    /// Defaults to <c>5 seconds</c>.
    /// </summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets the optional retention period for processed and failed messages.
    /// When set, TTL indexes are created to automatically remove messages after this period.
    /// When <c>null</c>, messages are retained indefinitely.
    /// </summary>
    public TimeSpan? RetentionPeriod { get; set; }

    /// <summary>
    /// Gets or sets the initial delay for exponential retry backoff.
    /// Defaults to <c>5 seconds</c>.
    /// </summary>
    public TimeSpan RetryBackoffInitialDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets the maximum delay cap for exponential retry backoff.
    /// Defaults to <c>5 minutes</c>.
    /// </summary>
    public TimeSpan RetryBackoffMaxDelay { get; set; } = TimeSpan.FromMinutes(5);
}
