// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using MongoDB.Bson;

/// <summary>
/// The queries used to find unsealed events. Each one is a bounded read on the unique
/// <c>(AggregateId, Version)</c> index, so a sweep pass costs work per stream, not per event.
/// </summary>
/// <typeparam name="TAggregate">The aggregate type.</typeparam>
internal static class SealingSweepQueries<TAggregate> where TAggregate : class, IAggregate, new()
{
    /// <summary>
    /// Creates the query for the events at and below an append's predecessor in descending version order.
    /// The append safety net limits it to one sealing chunk plus one event, so its cost stays bounded.
    /// </summary>
    /// <param name="aggregateId">The aggregate identifier.</param>
    /// <param name="predecessorVersion">The version the appended events follow.</param>
    /// <returns>The filter and sort of the query; the projection is empty because whole documents are sealed.</returns>
    public static SweepQuery AppendSealingWindow(Guid aggregateId, Int64 predecessorVersion)
        => new(
            new BsonDocument
            {
                { EventDocumentFields<TAggregate>.AggregateId, new BsonBinaryData(aggregateId, GuidRepresentation.Standard) },
                { EventDocumentFields<TAggregate>.Version, new BsonDocument("$lte", predecessorVersion) }
            },
            new BsonDocument(EventDocumentFields<TAggregate>.Version, -1),
            new BsonDocument());

    /// <summary>
    /// Creates the query for the first stream after the cursor in aggregate identifier order.
    /// </summary>
    /// <param name="cursor">The last checked aggregate, or <c>null</c> to start with the first stream.</param>
    /// <returns>The filter, sort and projection of the query, which is limited to one document.</returns>
    public static SweepQuery NextStream(Guid? cursor)
    {
        var aggregateIdElement = EventDocumentFields<TAggregate>.AggregateId;
        var filter = cursor is { } after
            ? new BsonDocument(aggregateIdElement,
                               new BsonDocument("$gt", new BsonBinaryData(after, GuidRepresentation.Standard)))
            : new BsonDocument();

        return new SweepQuery(
            filter,
            new BsonDocument
            {
                { aggregateIdElement, 1 },
                { EventDocumentFields<TAggregate>.Version, 1 }
            },
            new BsonDocument
            {
                { aggregateIdElement, 1 },
                { "_id", 0 }
            });
    }

    /// <summary>
    /// Creates the query for the head (highest version) of a stream, including its integrity data.
    /// </summary>
    /// <param name="aggregateId">The aggregate identifier.</param>
    /// <returns>The filter, sort and projection of the query, which is limited to one document.</returns>
    public static SweepQuery StreamHead(Guid aggregateId)
        => new(
            new BsonDocument(EventDocumentFields<TAggregate>.AggregateId, new BsonBinaryData(aggregateId, GuidRepresentation.Standard)),
            new BsonDocument(EventDocumentFields<TAggregate>.Version, -1),
            new BsonDocument
            {
                { EventDocumentFields<TAggregate>.Version, 1 },
                { EventIntegrityChain.ElementName, 1 }
            });
}
