// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Queues;

using MongoDB.Driver;

/// <summary>
/// Defines a prioritizer for determining the processing order of queue items.
/// </summary>
public interface IMongoQueuePayloadPrioritizer
{
    /// <summary>
    /// Creates a sort definition to determine the order in which queue items are processed.
    /// </summary>
    /// <typeparam name="TPayload">The type of payload in the queue.</typeparam>
    /// <returns>A MongoDB sort definition for ordering queue items.</returns>
    SortDefinition<MongoQueueItem<TPayload>> CreateSortDefinition<TPayload>() where TPayload : class;
}
