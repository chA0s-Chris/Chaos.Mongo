// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

/// <summary>
/// Resolves element names of stored events through the registered class map, so untyped queries
/// respect conventions that rename elements.
/// </summary>
/// <typeparam name="TAggregate">The aggregate type.</typeparam>
internal static class EventDocumentFields<TAggregate> where TAggregate : class, IAggregate, new()
{
    /// <summary>
    /// Gets the element name of <see cref="Event{TAggregate}.AggregateId"/>.
    /// </summary>
    public static String AggregateId => ElementNameOf(nameof(Event<TAggregate>.AggregateId));

    /// <summary>
    /// Gets the element name of <see cref="Event{TAggregate}.Version"/>.
    /// </summary>
    public static String Version => ElementNameOf(nameof(Event<TAggregate>.Version));

    /// <summary>
    /// Creates a filter matching all events of an aggregate.
    /// </summary>
    /// <typeparam name="TDocument">The untyped document type of the collection.</typeparam>
    /// <param name="aggregateId">The aggregate identifier.</param>
    /// <returns>The filter.</returns>
    public static FilterDefinition<TDocument> ForAggregate<TDocument>(Guid aggregateId)
        => new BsonDocumentFilterDefinition<TDocument>(
            new BsonDocument(AggregateId, new BsonBinaryData(aggregateId, GuidRepresentation.Standard)));

    /// <summary>
    /// Creates a filter matching one version of an aggregate.
    /// </summary>
    /// <typeparam name="TDocument">The untyped document type of the collection.</typeparam>
    /// <param name="aggregateId">The aggregate identifier.</param>
    /// <param name="version">The event version.</param>
    /// <returns>The filter.</returns>
    public static FilterDefinition<TDocument> ForVersion<TDocument>(Guid aggregateId, Int64 version)
        => new BsonDocumentFilterDefinition<TDocument>(
            new BsonDocument
            {
                { AggregateId, new BsonBinaryData(aggregateId, GuidRepresentation.Standard) },
                { Version, version }
            });

    private static String ElementNameOf(String memberName)
        => BsonClassMap.LookupClassMap(typeof(Event<TAggregate>)).GetMemberMap(memberName).ElementName;
}
