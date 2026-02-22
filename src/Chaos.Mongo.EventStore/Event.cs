// Copyright (c) 2025-2026 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore;

/// <summary>
/// Abstract base class for domain events targeting a specific aggregate type.
/// </summary>
/// <typeparam name="TAggregate">The aggregate type this event applies to.</typeparam>
public abstract class Event<TAggregate> where TAggregate : class, IAggregate
{
    /// <summary>
    /// Gets or sets the identifier of the aggregate this event belongs to.
    /// </summary>
    public Guid AggregateId { get; set; }

    /// <summary>
    /// Gets or sets the discriminator string for the aggregate type.
    /// Set automatically by the event store.
    /// </summary>
    public String AggregateType { get; set; } = String.Empty;

    /// <summary>
    /// Gets or sets the UTC timestamp of when the event was created.
    /// Set automatically by the event store on append if not provided.
    /// </summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>
    /// Gets or sets the unique event identifier.
    /// Used for idempotency: duplicate event IDs are rejected by the unique index on <c>_id</c>.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the monotonically increasing version per aggregate.
    /// Set by the event store.
    /// </summary>
    public Int64 Version { get; set; }

    /// <summary>
    /// Applies this event's changes to the given aggregate instance.
    /// </summary>
    /// <param name="aggregate">The aggregate to modify.</param>
    public abstract void Execute(TAggregate aggregate);
}
