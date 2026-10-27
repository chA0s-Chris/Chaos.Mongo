// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using Chaos.Mongo.EventStore.Errors;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using System.Diagnostics.CodeAnalysis;
using System.Text;

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
    /// Gets the element name of <see cref="Event{TAggregate}.AggregateType"/>.
    /// </summary>
    public static String AggregateType => ElementNameOf(nameof(Event<TAggregate>.AggregateType));

    /// <summary>
    /// Gets the element name of <see cref="Event{TAggregate}.CreatedUtc"/>.
    /// </summary>
    public static String CreatedUtc => ElementNameOf(nameof(Event<TAggregate>.CreatedUtc));

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
    /// Computes the genesis value of a stream from the aggregate type stored in its version-1 event, the same bytes an
    /// independent verifier reads, so a renamed aggregate class does not change it.
    /// </summary>
    /// <param name="versionOneDocument">The stored version-1 event.</param>
    /// <param name="aggregateId">The aggregate identifier.</param>
    /// <returns>The genesis value.</returns>
    /// <exception cref="MongoEventStoreException">Thrown when the event stores no aggregate type as a string.</exception>
    public static Byte[] ComputeGenesis(BsonDocument versionOneDocument, Guid aggregateId)
        => TryReadAggregateType(versionOneDocument, out var aggregateType)
            ? EventIntegrityChain.ComputeGenesis(aggregateType, aggregateId)
            : throw new MongoEventStoreException(
                $"Version 1 of aggregate '{aggregateId}' stores no aggregate type as a string, so the stream cannot be sealed.");

    /// <summary>
    /// Creates a filter that excludes events whose concrete type is a registered observational event type.
    /// The concrete discriminator is the last element of a hierarchical discriminator array, or the scalar
    /// discriminator itself. Matching only the concrete discriminator keeps unknown descendants of
    /// observational types in the results, so they still fail during deserialization.
    /// </summary>
    /// <param name="discriminators">The discriminators of the observational event types.</param>
    /// <returns>The filter, empty when there is no observational event type.</returns>
    public static FilterDefinition<Event<TAggregate>> ExcludeObservationalEvents(IReadOnlyList<String> discriminators)
    {
        ArgumentNullException.ThrowIfNull(discriminators);
        if (discriminators.Count == 0)
        {
            return Builders<Event<TAggregate>>.Filter.Empty;
        }

        var discriminatorElement = $"${Discriminator}";
        var concreteDiscriminator = new BsonDocument(
            "$cond",
            new BsonArray
            {
                new BsonDocument("$isArray", discriminatorElement),
                new BsonDocument("$arrayElemAt", new BsonArray
                {
                    discriminatorElement,
                    -1
                }),
                discriminatorElement
            });

        return new BsonDocumentFilterDefinition<Event<TAggregate>>(
            new BsonDocument(
                "$expr",
                new BsonDocument(
                    "$not",
                    new BsonArray
                    {
                        new BsonDocument("$in", new BsonArray
                        {
                            concreteDiscriminator,
                            new BsonDocument("$literal", new BsonArray(discriminators))
                        })
                    })));
    }

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
    /// Gets a value indicating whether a stored discriminator names the erasure event. Its discriminator is reserved,
    /// so comparing the concrete discriminator, the last element of a hierarchical discriminator, is equivalent to
    /// resolving the type and avoids a driver lookup for every event.
    /// </summary>
    /// <param name="discriminator">The stored discriminator value.</param>
    /// <returns><c>true</c> when the discriminator names the erasure event; otherwise <c>false</c>.</returns>
    public static Boolean IsErasureEvent(BsonValue discriminator)
    {
        ArgumentNullException.ThrowIfNull(discriminator);

        var concreteDiscriminator = discriminator is BsonArray hierarchical ? hierarchical.LastOrDefault() : discriminator;
        return concreteDiscriminator is BsonString { Value: PersonalDataErased<TAggregate>.Discriminator };
    }

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

    /// <summary>
    /// Reads the aggregate type stored in an event document without throwing on tampered values.
    /// </summary>
    /// <param name="document">The stored event document.</param>
    /// <param name="aggregateType">The stored aggregate type when it is a string.</param>
    /// <returns><c>true</c> when the document stores the aggregate type as a string; otherwise <c>false</c>.</returns>
    public static Boolean TryReadAggregateType(BsonDocument document, [NotNullWhen(true)] out String? aggregateType)
    {
        ArgumentNullException.ThrowIfNull(document);
        try
        {
            aggregateType = document.TryGetValue(AggregateType, out var value) && value is BsonString { Value: var name } ? name : null;
        }
        catch (Exception ex) when (ex is DecoderFallbackException or FormatException or InvalidOperationException)
        {
            // A raw document decodes its values on access; a string that is not valid UTF-8, or a value the reader
            // rejects, such as a UUID that is not 16 bytes long, is no aggregate type.
            aggregateType = null;
        }

        return aggregateType is not null;
    }

    private static String ElementNameOf(String memberName)
        => BsonClassMap.LookupClassMap(typeof(Event<TAggregate>)).GetMemberMap(memberName).ElementName;
}
