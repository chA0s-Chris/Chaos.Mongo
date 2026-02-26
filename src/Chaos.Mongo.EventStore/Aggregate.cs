// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore;

/// <summary>
/// Abstract base class for all aggregate root types used with the event store.
/// </summary>
/// <remarks>
/// Derived types must have a parameterless constructor.
/// The recommended pattern is that the first event for any aggregate should be a creation event
/// (e.g., <c>OrderCreatedEvent</c>) that initializes the aggregate's required state.
/// </remarks>
public abstract class Aggregate : IAggregate
{
    /// <summary>
    /// Gets or sets the UTC timestamp of when the aggregate was created.
    /// </summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>
    /// Gets or sets the unique identifier of the aggregate.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the version of the aggregate after the last applied event.
    /// </summary>
    public Int64 Version { get; set; }
}
