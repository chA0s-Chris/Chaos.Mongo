// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore;

using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

/// <summary>
/// Handles BsonClassMap registration for aggregates and events, including discriminator configuration
/// and GuidSerializer setup.
/// </summary>
public static class MongoEventStoreSerializationSetup
{
    private static readonly GuidSerializer _guidStandardSerializer = new(GuidRepresentation.Standard);

    /// <summary>
    /// Ensures a <see cref="GuidSerializer"/> with <see cref="GuidRepresentation.Standard"/> is registered globally.
    /// If the user has already registered a GuidSerializer, their configuration is respected.
    /// </summary>
    public static void EnsureGuidSerializer()
    {
        try
        {
            BsonSerializer.RegisterSerializer(_guidStandardSerializer);
        }
        catch (BsonSerializationException)
        {
            // Already registered — respect existing configuration
        }
    }

    /// <summary>
    /// Registers BsonClassMaps for the aggregate type and its event types.
    /// Guid fields are explicitly configured with <see cref="GuidRepresentation.Standard"/>.
    /// </summary>
    /// <typeparam name="TAggregate">The aggregate type.</typeparam>
    /// <param name="options">The event store options containing event type registrations.</param>
    public static void RegisterClassMaps<TAggregate>(MongoEventStoreOptions<TAggregate> options)
        where TAggregate : class, IAggregate, new()
    {
        // Register the concrete aggregate type
        var aggregateType = typeof(TAggregate);
        if (!BsonClassMap.IsClassMapRegistered(aggregateType))
        {
            if (typeof(Aggregate).IsAssignableFrom(aggregateType))
            {
                // TAggregate extends Aggregate — register base class first
                if (!BsonClassMap.IsClassMapRegistered(typeof(Aggregate)))
                {
                    BsonClassMap.RegisterClassMap<Aggregate>(cm =>
                    {
                        cm.AutoMap();
                        cm.MapIdMember(a => a.Id).SetSerializer(_guidStandardSerializer);
                        cm.SetIsRootClass(true);
                    });
                }

                var aggregateClassMap = new BsonClassMap(aggregateType, BsonClassMap.LookupClassMap(typeof(Aggregate)));
                aggregateClassMap.AutoMap();
                BsonClassMap.RegisterClassMap(aggregateClassMap);
            }
            else
            {
                // TAggregate implements IAggregate directly
                BsonClassMap.RegisterClassMap<TAggregate>(cm =>
                {
                    cm.AutoMap();
                    cm.MapIdMember(a => a.Id).SetSerializer(_guidStandardSerializer);
                });
            }
        }

        // Register the Event<TAggregate> base class map
        var eventBaseType = typeof(Event<TAggregate>);
        if (!BsonClassMap.IsClassMapRegistered(eventBaseType))
        {
            BsonClassMap.RegisterClassMap<Event<TAggregate>>(cm =>
            {
                cm.AutoMap();
                cm.MapIdMember(e => e.Id).SetSerializer(_guidStandardSerializer);
                cm.MapMember(e => e.AggregateId).SetSerializer(_guidStandardSerializer);
                cm.SetIsRootClass(true);
            });
        }

        // Register each concrete event type with its discriminator
        foreach (var (eventType, discriminator) in options.EventTypes)
        {
            if (BsonClassMap.IsClassMapRegistered(eventType))
                continue;

            var classMap = new BsonClassMap(eventType, BsonClassMap.LookupClassMap(eventBaseType));
            classMap.AutoMap();
            classMap.SetDiscriminator(discriminator);
            BsonClassMap.RegisterClassMap(classMap);
        }

        // Register CheckpointDocument<TAggregate>
        var checkpointDocumentType = typeof(CheckpointDocument<TAggregate>);
        if (!BsonClassMap.IsClassMapRegistered(checkpointDocumentType))
        {
            BsonClassMap.RegisterClassMap<CheckpointDocument<TAggregate>>(cm =>
            {
                cm.AutoMap();
                cm.MapIdMember(c => c.Id);
            });
        }
    }
}
