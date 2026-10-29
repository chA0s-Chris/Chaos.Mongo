// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore;

using Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// Configuration options for an event store for a specific aggregate type.
/// </summary>
/// <typeparam name="TAggregate">The aggregate type.</typeparam>
public sealed class MongoEventStoreOptions<TAggregate> where TAggregate : class, IAggregate, new()
{
    private readonly Dictionary<Type, String> _eventTypes = new();

    /// <summary>
    /// Gets or sets a value indicating whether the MongoDB 8+ client bulk-write optimization is enabled.
    /// Defaults to <c>false</c>.
    /// </summary>
    public Boolean BulkWriteOptimizationEnabled { get; internal set; }

    /// <summary>
    /// Gets the name of the checkpoint collection.
    /// </summary>
    public String CheckpointCollectionName => $"{CollectionPrefix}{CheckpointCollectionSuffix}";

    /// <summary>
    /// Gets or sets the suffix appended to the collection prefix for the checkpoint collection.
    /// Defaults to <c>"_Checkpoints"</c>.
    /// </summary>
    public String CheckpointCollectionSuffix { get; internal set; } = "_Checkpoints";

    /// <summary>
    /// Gets or sets the checkpoint interval. When set to a value greater than 0,
    /// checkpoints are created every N versions.
    /// A value of 0 or less means checkpoints are disabled.
    /// </summary>
    public Int32 CheckpointInterval { get; internal set; }

    /// <summary>
    /// Gets a value indicating whether checkpoints are enabled.
    /// </summary>
    public Boolean CheckpointsEnabled => CheckpointInterval > 0;

    /// <summary>
    /// Gets the collection prefix used to derive collection names.
    /// Defaults to the aggregate type name.
    /// </summary>
    public String CollectionPrefix { get; internal set; } = typeof(TAggregate).Name;

    /// <summary>
    /// Gets the event types registered for this aggregate, mapped to their discriminator names.
    /// </summary>
    public IReadOnlyDictionary<Type, String> EventTypes => _eventTypes;

    /// <summary>
    /// Gets the name of the events collection.
    /// </summary>
    public String EventsCollectionName => $"{CollectionPrefix}{EventsCollectionSuffix}";

    /// <summary>
    /// Gets or sets the suffix appended to the collection prefix for the events collection.
    /// Defaults to <c>"_Events"</c>.
    /// </summary>
    public String EventsCollectionSuffix { get; internal set; } = "_Events";

    /// <summary>
    /// Gets a value indicating whether appended events are sealed into a per-stream hash chain. Enabled with
    /// <see cref="MongoEventStoreBuilder{TAggregate}.WithIntegrityProtection"/>. Defaults to <c>false</c>.
    /// </summary>
    public Boolean IntegrityProtectionEnabled { get; internal set; }

    /// <summary>
    /// Gets or sets the number of additional attempts an append with store-assigned versions makes after the
    /// first one, when another writer commits to the same stream concurrently. Defaults to 3.
    /// </summary>
    public Int32 MaxAppendRetries { get; internal set; } = 3;

    /// <summary>
    /// Gets the name of the read-model collection.
    /// </summary>
    public String ReadModelCollectionName => CollectionPrefix;

    /// <summary>
    /// Gets the maximum number of events sealed retroactively in one transaction. An append to a stream with more
    /// unsealed predecessors fails until the sealing sweep caught the stream up. Defaults to 1,000.
    /// </summary>
    public Int32 SealingChunkSize { get; internal set; } = 1000;

    /// <summary>
    /// Gets a value indicating whether the background sealing sweep is registered for a protected aggregate type.
    /// Defaults to <c>true</c>; see <see cref="IntegrityProtectionBuilder{TAggregate}.WithoutSealingSweep"/>.
    /// </summary>
    public Boolean SealingSweepEnabled { get; internal set; } = true;

    /// <summary>
    /// Gets the delay between two passes of the background sealing sweep. Defaults to 24 hours.
    /// </summary>
    public TimeSpan SealingSweepInterval { get; internal set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Gets the delay before a failed sealing pass, or one that lost the sweep lock, is retried. The retry resumes the
    /// pass. Defaults to 1 minute.
    /// </summary>
    public TimeSpan SealingSweepRetryDelay { get; internal set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Gets the name of the collection that stores the integrity sealing state.
    /// </summary>
    internal String IntegrityStateCollectionName => $"{CollectionPrefix}{IntegrityStateCollectionSuffix}";

    /// <summary>
    /// Gets or sets the suffix appended to the collection prefix for the integrity sealing state collection.
    /// Defaults to <c>"_IntegrityState"</c>.
    /// </summary>
    internal String IntegrityStateCollectionSuffix { get; set; } = "_IntegrityState";

    /// <summary>
    /// Gets or sets the discriminators of the registered observational event types, which reconstruction
    /// excludes from replay. Set when the class maps are registered.
    /// </summary>
    internal IReadOnlyList<String> ObservationalEventDiscriminators { get; set; } = [];

    /// <summary>
    /// Gets the personal data marks of each event type, as given at registration.
    /// </summary>
    internal Dictionary<Type, List<PersonalDataMark>> PersonalDataMarks { get; } = new();

    /// <summary>
    /// Gets or sets the resolved element paths of the personal data of each event type. Set when the class maps
    /// are registered. Stored here rather than in the process-global class maps, so stores configured differently
    /// can verify the same events.
    /// </summary>
    internal IReadOnlyDictionary<Type, IReadOnlyList<String>> PersonalDataPaths { get; set; } = new Dictionary<Type, IReadOnlyList<String>>();

    /// <summary>
    /// Registers an event type's discriminator, overwriting any existing registration for the same type.
    /// </summary>
    internal void AddEventType(Type eventType, String discriminator)
        => _eventTypes[eventType] = discriminator;
}
