// Copyright (c) 2025-2026 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore;

/// <summary>
/// Defines the names of MongoDB indexes used by the event store.
/// </summary>
public static class IndexNames
{
    /// <summary>
    /// The name of the unique compound index on <c>(AggregateId, Version)</c> in the events collection.
    /// This index ensures that only one event can exist for a given aggregate at a specific version,
    /// preventing concurrency conflicts.
    /// </summary>
    public const String AggregateIdWithVersionUnique = "AggregateId_with_Version_Unique";
}
