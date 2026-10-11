// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// A protected aggregate type as seen by code that spans all types, such as the anchor candidate sources.
/// Element names and queries are resolved per type, because class maps may differ between types.
/// </summary>
internal abstract class ProtectedStreamType
{
    /// <summary>
    /// Gets the element name of the aggregate identifier in the events collection.
    /// </summary>
    public abstract String AggregateIdElement { get; }

    /// <summary>
    /// Gets the aggregate type name, as used for the genesis value of the chain.
    /// </summary>
    public abstract String AggregateTypeName { get; }

    /// <summary>
    /// Gets the name of the events collection.
    /// </summary>
    public abstract String EventsCollectionName { get; }

    /// <summary>
    /// Gets the element name of the version in the events collection.
    /// </summary>
    public abstract String VersionElement { get; }

    /// <summary>
    /// Creates the query for the first stream after the cursor.
    /// </summary>
    /// <param name="cursor">The last scanned aggregate, or <c>null</c> to start with the first stream.</param>
    /// <returns>The query.</returns>
    public abstract SweepQuery NextStream(Guid? cursor);

    /// <summary>
    /// Creates the query for the head of a stream, including its integrity data.
    /// </summary>
    /// <param name="aggregateId">The aggregate identifier.</param>
    /// <returns>The query.</returns>
    public abstract SweepQuery StreamHead(Guid aggregateId);
}
