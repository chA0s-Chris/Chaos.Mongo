// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore;

/// <summary>
/// Defines the contract for aggregate root types used with the event store.
/// </summary>
/// <remarks>
/// Implement this interface on your aggregate types. You may also extend the
/// <see cref="Aggregate"/> convenience base class which provides a default implementation.
/// </remarks>
public interface IAggregate
{
    /// <summary>
    /// Gets or sets the UTC timestamp of when the aggregate was created.
    /// </summary>
    DateTime CreatedUtc { get; set; }

    /// <summary>
    /// Gets or sets the unique identifier of the aggregate.
    /// </summary>
    Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the revision of the aggregate: the number of state-changing events applied to it.
    /// Observational events do not change it.
    /// </summary>
    Int64 Revision { get; set; }

    /// <summary>
    /// Gets or sets the stream position of the last state-changing event applied to the aggregate.
    /// Observational events appended later do not change it.
    /// </summary>
    Int64 Version { get; set; }
}
