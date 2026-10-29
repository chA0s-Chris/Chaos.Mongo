// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Queues;

using MongoDB.Driver;

/// <summary>
/// Provides a default prioritizer implementation that processes queue items in FIFO order based on their ID.
/// </summary>
public sealed class MongoQueuePayloadPrioritizer : IMongoQueuePayloadPrioritizer
{
    /// <inheritdoc/>
    public SortDefinition<MongoQueueItem<TPayload>> CreateSortDefinition<TPayload>()
        where TPayload : class, new()
        => Builders<MongoQueueItem<TPayload>>.Sort
                                             .Ascending(x => x.Id);
}
