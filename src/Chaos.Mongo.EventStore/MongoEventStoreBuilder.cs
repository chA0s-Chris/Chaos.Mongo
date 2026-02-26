// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore;

/// <summary>
/// Fluent builder for configuring an event store for a specific aggregate type.
/// </summary>
/// <typeparam name="TAggregate">The aggregate type.</typeparam>
public sealed class MongoEventStoreBuilder<TAggregate> where TAggregate : class, IAggregate, new()
{
    /// <summary>
    /// Gets the options being configured by this builder.
    /// </summary>
    public MongoEventStoreOptions<TAggregate> Options { get; } = new();

    /// <summary>
    /// Sets the suffix appended to the collection prefix for the checkpoint collection.
    /// Defaults to <c>"_Checkpoints"</c>.
    /// </summary>
    /// <param name="suffix">The checkpoint collection suffix.</param>
    /// <returns>This builder instance for method chaining.</returns>
    public MongoEventStoreBuilder<TAggregate> WithCheckpointCollectionSuffix(String suffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suffix);
        Options.CheckpointCollectionSuffix = suffix;
        return this;
    }

    /// <summary>
    /// Enables checkpoint creation at the specified interval.
    /// </summary>
    /// <param name="interval">
    /// The number of versions between checkpoints. For example, an interval of 100 means
    /// a checkpoint is created at versions 100, 200, 300, etc.
    /// </param>
    /// <returns>This builder instance for method chaining.</returns>
    public MongoEventStoreBuilder<TAggregate> WithCheckpoints(Int32 interval)
    {
        if (interval <= 0)
            throw new ArgumentOutOfRangeException(nameof(interval), "Checkpoint interval must be greater than 0.");

        Options.CheckpointInterval = interval;
        return this;
    }

    /// <summary>
    /// Sets the collection prefix used to derive collection names.
    /// </summary>
    /// <remarks>
    /// The events collection will be named <c>{prefix}{EventsCollectionSuffix}</c>,
    /// the read-model collection will be named <c>{prefix}</c>,
    /// and the checkpoint collection (if enabled) will be named <c>{prefix}{CheckpointCollectionSuffix}</c>.
    /// </remarks>
    /// <param name="prefix">The collection prefix.</param>
    /// <returns>This builder instance for method chaining.</returns>
    public MongoEventStoreBuilder<TAggregate> WithCollectionPrefix(String prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        Options.CollectionPrefix = prefix;
        return this;
    }

    /// <summary>
    /// Registers an event type for this aggregate.
    /// </summary>
    /// <typeparam name="TEvent">The event type.</typeparam>
    /// <param name="discriminator">
    /// Optional discriminator name for MongoDB serialization.
    /// Defaults to the class name (not the full name).
    /// It is recommended to provide explicit discriminator names to avoid breaking changes
    /// when refactoring class names or namespaces.
    /// </param>
    /// <returns>This builder instance for method chaining.</returns>
    public MongoEventStoreBuilder<TAggregate> WithEvent<TEvent>(String? discriminator = null)
        where TEvent : Event<TAggregate>
    {
        var eventType = typeof(TEvent);
        discriminator ??= eventType.Name;
        Options.EventTypes[eventType] = discriminator;
        return this;
    }

    /// <summary>
    /// Sets the suffix appended to the collection prefix for the events collection.
    /// Defaults to <c>"_Events"</c>.
    /// </summary>
    /// <param name="suffix">The events collection suffix.</param>
    /// <returns>This builder instance for method chaining.</returns>
    public MongoEventStoreBuilder<TAggregate> WithEventsCollectionSuffix(String suffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suffix);
        Options.EventsCollectionSuffix = suffix;
        return this;
    }
}
