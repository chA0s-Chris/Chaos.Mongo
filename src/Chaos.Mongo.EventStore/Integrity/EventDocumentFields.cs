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
    /// Gets the element name of the discriminator. It is resolved through the event serializer rather than
    /// <see cref="BsonSerializer.LookupDiscriminatorConvention"/>, which lets the class map register the
    /// hierarchical convention of its root class first; looking the convention up directly before the first
    /// event is serialized would cache the scalar convention and change how every event is stored.
    /// </summary>
    public static String Discriminator
        => BsonSerializer.LookupSerializer<Event<TAggregate>>() is IHasDiscriminatorConvention { DiscriminatorConvention: { } convention }
            ? convention.ElementName
            : BsonSerializer.LookupDiscriminatorConvention(typeof(Event<TAggregate>)).ElementName;

    /// <summary>
    /// Gets the element name of <see cref="Event{TAggregate}.Revision"/>.
    /// </summary>
    public static String Revision => ElementNameOf(nameof(Event<TAggregate>.Revision));

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

    /// <summary>
    /// Resolves the event type stored with a discriminator through the driver's discriminator lookup. A
    /// hierarchical discriminator resolves to its concrete type, its last element.
    /// </summary>
    /// <param name="discriminator">The stored discriminator value.</param>
    /// <returns>The event type, or <c>null</c> when the discriminator is unknown.</returns>
    public static Type? ResolveEventType(BsonValue discriminator)
    {
        ArgumentNullException.ThrowIfNull(discriminator);

        var concreteDiscriminator = discriminator is BsonArray hierarchical ? hierarchical.LastOrDefault() : discriminator;
        if (concreteDiscriminator is null)
        {
            return null;
        }

        try
        {
            var eventType = BsonSerializer.LookupActualType(typeof(Event<TAggregate>), concreteDiscriminator);
            return typeof(Event<TAggregate>).IsAssignableFrom(eventType) ? eventType : null;
        }
        catch (BsonSerializationException)
        {
            return null;
        }
    }

    private static String ElementNameOf(String memberName)
        => BsonClassMap.LookupClassMap(typeof(Event<TAggregate>)).GetMemberMap(memberName).ElementName;
}
