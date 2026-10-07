// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore;

using Chaos.Mongo.EventStore.Integrity;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

/// <summary>
/// Handles BsonClassMap registration for aggregates and events, including discriminator configuration
/// and GuidSerializer setup.
/// </summary>
public static class MongoEventStoreSerializationSetup
{
    private static readonly GuidSerializer GuidStandardSerializer = new(GuidRepresentation.Standard);

    /// <summary>
    /// Ensures a <see cref="GuidSerializer"/> with <see cref="GuidRepresentation.Standard"/> is registered globally.
    /// If the user has already registered a GuidSerializer, their configuration is respected.
    /// </summary>
    public static void EnsureGuidSerializer()
    {
        try
        {
            BsonSerializer.RegisterSerializer(GuidStandardSerializer);
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
    /// <exception cref="InvalidOperationException">
    /// Thrown when an event type maps a member to the reserved <c>_integrity</c> element, or when integrity
    /// protection is enabled but the event base class map was registered without the integrity member.
    /// </exception>
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
                        cm.MapIdMember(a => a.Id).SetSerializer(GuidStandardSerializer);
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
                    cm.MapIdMember(a => a.Id).SetSerializer(GuidStandardSerializer);
                });
            }
        }

        // Register the Event<TAggregate> base class map. The integrity member is mapped regardless of
        // whether protection is enabled: class maps are process-global and shared by every store.
        RegisterEventIntegrityClassMap();
        var eventBaseType = typeof(Event<TAggregate>);
        if (!BsonClassMap.IsClassMapRegistered(eventBaseType))
        {
            BsonClassMap.RegisterClassMap<Event<TAggregate>>(cm =>
            {
                cm.AutoMap();
                cm.MapIdMember(e => e.Id).SetSerializer(GuidStandardSerializer);
                cm.MapMember(e => e.AggregateId).SetSerializer(GuidStandardSerializer);
                cm.MapMember(e => e.Integrity)
                  .SetElementName(EventIntegrityChain.ElementName)
                  .SetIgnoreIfNull(true);
                cm.SetIsRootClass(true);
            });
        }

        EnsureEventBaseMapsIntegrity(options);

        // Register each concrete event type with its discriminator
        foreach (var (eventType, discriminator) in options.EventTypes)
        {
            if (BsonClassMap.IsClassMapRegistered(eventType))
            {
                EnsureNoReservedElementCollision<TAggregate>(GetRegisteredClassMap(eventType));
                continue;
            }

            var classMap = new BsonClassMap(eventType, BsonClassMap.LookupClassMap(eventBaseType));
            classMap.AutoMap();
            classMap.SetDiscriminator(discriminator);
            EnsureNoReservedElementCollision<TAggregate>(classMap);
            BsonClassMap.RegisterClassMap(classMap);
        }

        // Register CheckpointId
        if (!BsonClassMap.IsClassMapRegistered(typeof(CheckpointId)))
        {
            BsonClassMap.RegisterClassMap<CheckpointId>(cm =>
            {
                cm.AutoMap();
                cm.MapMember(c => c.AggregateId).SetSerializer(GuidStandardSerializer);
            });
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

    private static void EnsureEventBaseMapsIntegrity<TAggregate>(MongoEventStoreOptions<TAggregate> options)
        where TAggregate : class, IAggregate, new()
    {
        if (!options.IntegrityProtectionEnabled)
        {
            return;
        }

        var eventBaseMap = GetRegisteredClassMap(typeof(Event<TAggregate>));
        if (eventBaseMap.DeclaredMemberMaps.Any(m => m.MemberName == nameof(Event<TAggregate>.Integrity)))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Integrity protection is enabled for aggregate type {typeof(TAggregate).Name}, but the class map of " +
            $"{eventBaseMap.ClassType.Name} was registered before the event store and does not map the reserved " +
            $"'{EventIntegrityChain.ElementName}' element. Let the event store register the event base class map.");
    }

    private static void EnsureNoReservedElementCollision<TAggregate>(BsonClassMap eventClassMap)
        where TAggregate : class, IAggregate, new()
    {
        for (var classMap = eventClassMap;
             classMap is not null && classMap.ClassType != typeof(Event<TAggregate>);
             classMap = classMap.BaseClassMap)
        {
            var collision = classMap.DeclaredMemberMaps.FirstOrDefault(m => m.ElementName == EventIntegrityChain.ElementName);
            if (collision is not null)
            {
                throw new InvalidOperationException(
                    $"Event type {eventClassMap.ClassType.Name} maps member {collision.MemberName} of {classMap.ClassType.Name} " +
                    $"to the element name '{EventIntegrityChain.ElementName}', which is reserved by the event store.");
            }
        }
    }

    private static BsonClassMap GetRegisteredClassMap(Type classType)
        => BsonClassMap.GetRegisteredClassMaps().First(classMap => classMap.ClassType == classType);

    private static void RegisterEventIntegrityClassMap()
    {
        if (BsonClassMap.IsClassMapRegistered(typeof(EventIntegrity)))
        {
            return;
        }

        BsonClassMap.RegisterClassMap<EventIntegrity>(cm =>
        {
            cm.AutoMap();
            cm.GetMemberMap(i => i.SealMode).SetSerializer(new EnumSerializer<IntegritySealMode>(BsonType.String));
        });
    }
}
