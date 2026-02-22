// Copyright (c) 2025-2026 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore;

/// <summary>
/// Configuration options for an event store for a specific aggregate type.
/// </summary>
/// <typeparam name="TAggregate">The aggregate type.</typeparam>
public sealed class MongoEventStoreOptions<TAggregate> where TAggregate : class, IAggregate, new()
{
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
    /// Gets the name of the events collection.
    /// </summary>
    public String EventsCollectionName => $"{CollectionPrefix}{EventsCollectionSuffix}";

    /// <summary>
    /// Gets or sets the suffix appended to the collection prefix for the events collection.
    /// Defaults to <c>"_Events"</c>.
    /// </summary>
    public String EventsCollectionSuffix { get; set; } = "_Events";

    /// <summary>
    /// Gets the event types registered for this aggregate, mapped to their discriminator names.
    /// </summary>
    public Dictionary<Type, String> EventTypes { get; } = new();

    /// <summary>
    /// Gets the name of the read-model collection.
    /// </summary>
    public String ReadModelCollectionName => CollectionPrefix;
}
