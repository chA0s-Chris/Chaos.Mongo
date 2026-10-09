// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore;

/// <summary>
/// Configuration options for an event store for a specific aggregate type.
/// </summary>
/// <typeparam name="TAggregate">The aggregate type.</typeparam>
public sealed class MongoEventStoreOptions<TAggregate> where TAggregate : class, IAggregate, new()
{
    /// <summary>
    /// Gets or sets a value indicating whether the MongoDB 8+ client bulk-write optimization is enabled.
    /// Defaults to <c>false</c>.
    /// </summary>
    public Boolean BulkWriteOptimizationEnabled { get; set; }

    /// <summary>
    /// Gets the name of the checkpoint collection.
    /// </summary>
    public String CheckpointCollectionName => $"{CollectionPrefix}{CheckpointCollectionSuffix}";

    /// <summary>
    /// Gets or sets the suffix appended to the collection prefix for the checkpoint collection.
    /// Defaults to <c>"_Checkpoints"</c>.
    /// </summary>
    public String CheckpointCollectionSuffix { get; set; } = "_Checkpoints";

    /// <summary>
    /// Gets or sets the checkpoint interval. When set to a value greater than 0,
    /// checkpoints are created every N versions.
    /// A value of 0 or less means checkpoints are disabled.
    /// </summary>
    public Int32 CheckpointInterval { get; set; }

    /// <summary>
    /// Gets a value indicating whether checkpoints are enabled.
    /// </summary>
    public Boolean CheckpointsEnabled => CheckpointInterval > 0;

    /// <summary>
    /// Gets the collection prefix used to derive collection names.
    /// Defaults to the aggregate type name.
    /// </summary>
    public String CollectionPrefix { get; set; } = typeof(TAggregate).Name;

    /// <summary>
    /// Gets the event types registered for this aggregate, mapped to their discriminator names.
    /// </summary>
    public Dictionary<Type, String> EventTypes { get; } = new();

    /// <summary>
    /// Gets the name of the events collection.
    /// </summary>
    public String EventsCollectionName => $"{CollectionPrefix}{EventsCollectionSuffix}";

    /// <summary>
    /// Gets or sets the suffix appended to the collection prefix for the events collection.
    /// Defaults to <c>"_Events"</c>.
    /// </summary>
    public String EventsCollectionSuffix { get; set; } = "_Events";

    /// <summary>
    /// Gets the name of the read-model collection.
    /// </summary>
    public String ReadModelCollectionName => CollectionPrefix;

    /// <summary>
    /// Gets or sets a value indicating whether appended events are sealed into a per-stream hash chain.
    /// Defaults to <c>false</c>.
    /// </summary>
    internal Boolean IntegrityProtectionEnabled { get; set; }

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
    /// Gets or sets the maximum number of events sealed retroactively in one transaction. The append
    /// safety net uses the same bound. Defaults to 1,000.
    /// </summary>
    internal Int32 SealingChunkSize { get; set; } = 1000;

    /// <summary>
    /// Gets or sets the delay between two passes of the background sealing sweep. Defaults to 24 hours.
    /// </summary>
    internal TimeSpan SealingSweepInterval { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Gets or sets the delay before a failed sealing pass, or one that lost the sweep lock, is retried.
    /// The retry resumes the pass.
    /// Defaults to 1 minute.
    /// </summary>
    internal TimeSpan SealingSweepRetryDelay { get; set; } = TimeSpan.FromMinutes(1);
}
