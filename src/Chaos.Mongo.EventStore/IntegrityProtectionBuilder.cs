// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore;

/// <summary>
/// Configures the integrity protection of an aggregate type: the background sweep that seals events stored without
/// integrity data retroactively.
/// </summary>
/// <typeparam name="TAggregate">The aggregate type.</typeparam>
public sealed class IntegrityProtectionBuilder<TAggregate> where TAggregate : class, IAggregate, new()
{
    private readonly MongoEventStoreOptions<TAggregate> _options;

    internal IntegrityProtectionBuilder(MongoEventStoreOptions<TAggregate> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>
    /// Sets the maximum number of events sealed retroactively in one transaction. An append to a stream with more
    /// unsealed predecessors fails until the sweep caught the stream up. Defaults to 1,000.
    /// </summary>
    /// <param name="chunkSize">The chunk size.</param>
    /// <returns>This builder instance for method chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="chunkSize"/> is not positive.</exception>
    public IntegrityProtectionBuilder<TAggregate> WithSealingChunkSize(Int32 chunkSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSize);
        _options.SealingChunkSize = chunkSize;
        return this;
    }

    /// <summary>
    /// Sets the delay between two passes of the sealing sweep. Defaults to 24 hours.
    /// </summary>
    /// <param name="interval">The interval.</param>
    /// <returns>This builder instance for method chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="interval"/> is not positive.</exception>
    public IntegrityProtectionBuilder<TAggregate> WithSealingSweepInterval(TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        _options.SealingSweepInterval = interval;
        return this;
    }

    /// <summary>
    /// Sets the delay before a failed sealing pass, or one that lost the sweep lock, is retried. Defaults to 1 minute.
    /// </summary>
    /// <param name="retryDelay">The retry delay.</param>
    /// <returns>This builder instance for method chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="retryDelay"/> is not positive.</exception>
    public IntegrityProtectionBuilder<TAggregate> WithSealingSweepRetryDelay(TimeSpan retryDelay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retryDelay, TimeSpan.Zero);
        _options.SealingSweepRetryDelay = retryDelay;
        return this;
    }
}
