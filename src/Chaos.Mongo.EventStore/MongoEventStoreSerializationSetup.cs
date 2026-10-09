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
    /// Thrown when an event type maps a member to the reserved <c>_integrity</c> element, when integrity
    /// protection is enabled but the event base class map was registered without the integrity member, or when
    /// the event base class map or the class map declaring <see cref="IAggregate.Revision"/> was registered without
    /// the revision member.
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

        EnsureAggregateMapsRevision<TAggregate>();

        // Register the Event<TAggregate> base class map. The integrity member is mapped regardless of
        // whether protection is enabled: class maps are process-global and shared by every store.
        RegisterEventIntegrityClassMap();
        RegisterIntegritySealingStateClassMap();
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
        EnsureEventBaseMapsRevision<TAggregate>();

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

    /// <summary>
    /// The read model's revision is checked against the stream on every append, so a class map registered by a
    /// consumer for the type declaring <see cref="IAggregate.Revision"/> must map it. Otherwise every read model would
    /// load as a document stored before revisions existed.
    /// </summary>
    private static void EnsureAggregateMapsRevision<TAggregate>()
        where TAggregate : class, IAggregate, new()
    {
        var declaringType = typeof(TAggregate).GetProperty(nameof(IAggregate.Revision))?.DeclaringType;
        if (declaringType is null || !BsonClassMap.IsClassMapRegistered(declaringType))
        {
            return;
        }

        var classMap = GetRegisteredClassMap(declaringType);
        if (classMap.DeclaredMemberMaps.Any(m => m.MemberName == nameof(IAggregate.Revision)))
        {
            return;
        }

        throw new InvalidOperationException(
            $"The class map of {declaringType.Name} for aggregate type {typeof(TAggregate).Name} was registered before the " +
            $"event store and does not map the {nameof(IAggregate.Revision)} member, which the event store requires. " +
            $"Map {nameof(IAggregate.Revision)}, for example with AutoMap().");
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

    /// <summary>
    /// Every append reads the stored revision, so an event base class map registered by a consumer must map it.
    /// </summary>
    private static void EnsureEventBaseMapsRevision<TAggregate>()
        where TAggregate : class, IAggregate, new()
    {
        var eventBaseMap = GetRegisteredClassMap(typeof(Event<TAggregate>));
        if (eventBaseMap.DeclaredMemberMaps.Any(m => m.MemberName == nameof(Event<TAggregate>.Revision)))
        {
            return;
        }

        throw new InvalidOperationException(
            $"The class map of {eventBaseMap.ClassType.Name} for aggregate type {typeof(TAggregate).Name} was registered " +
            $"before the event store and does not map the {nameof(Event<TAggregate>.Revision)} member, which the event store " +
            "requires. Let the event store register the event base class map.");
    }

    /// <summary>
    /// Walks the CLR type hierarchy rather than <see cref="BsonClassMap.BaseClassMap"/>, which is only
    /// set once a class map is frozen, so intermediate base classes registered by consumers are covered.
    /// </summary>
    private static void EnsureNoReservedElementCollision<TAggregate>(BsonClassMap eventClassMap)
        where TAggregate : class, IAggregate, new()
    {
        var registeredClassMaps = BsonClassMap.GetRegisteredClassMaps().ToDictionary(classMap => classMap.ClassType);
        for (var type = eventClassMap.ClassType;
             type is not null && type != typeof(Event<TAggregate>);
             type = type.BaseType)
        {
            var classMap = type == eventClassMap.ClassType ? eventClassMap : registeredClassMaps.GetValueOrDefault(type);
            if (classMap is null)
            {
                continue;
            }

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

        // Element names are pinned so convention packs cannot change the stored format, and extra
        // elements are ignored so fields added by later format versions do not break typed reads.
        // Every member is required, so a removed member is malformed instead of silently defaulting,
        // which would, for example, turn a missing seal mode into Append.
        BsonClassMap.RegisterClassMap<EventIntegrity>(cm =>
        {
            cm.AutoMap();
            cm.SetIgnoreExtraElements(true);
            cm.GetMemberMap(i => i.FormatVersion).SetElementName(nameof(EventIntegrity.FormatVersion)).SetIsRequired(true);
            cm.GetMemberMap(i => i.Algorithm).SetElementName(nameof(EventIntegrity.Algorithm)).SetIsRequired(true);
            cm.GetMemberMap(i => i.PreviousHash).SetElementName(nameof(EventIntegrity.PreviousHash)).SetIsRequired(true);
            cm.GetMemberMap(i => i.Hash).SetElementName(nameof(EventIntegrity.Hash)).SetIsRequired(true);
            cm.GetMemberMap(i => i.SealMode)
              .SetElementName(nameof(EventIntegrity.SealMode))
              .SetIsRequired(true)
              .SetSerializer(new EnumSerializer<IntegritySealMode>(BsonType.String));
        });
    }

    private static void RegisterIntegritySealingStateClassMap()
    {
        if (BsonClassMap.IsClassMapRegistered(typeof(IntegritySealingState)))
        {
            return;
        }

        // Element names and the GUID representation of the aggregate identifiers are pinned, so neither
        // convention packs nor a globally registered GUID serializer change the stored state.
        BsonClassMap.RegisterClassMap<IntegritySealingState>(cm =>
        {
            cm.AutoMap();
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(s => s.Id);
            cm.GetMemberMap(s => s.Cursor)
              .SetElementName(nameof(IntegritySealingState.Cursor))
              .SetSerializer(new NullableSerializer<Guid>(GuidStandardSerializer));
            cm.GetMemberMap(s => s.LastSealedStream)
              .SetElementName(nameof(IntegritySealingState.LastSealedStream))
              .SetSerializer(new NullableSerializer<Guid>(GuidStandardSerializer));
            cm.GetMemberMap(s => s.PassStartedUtc).SetElementName(nameof(IntegritySealingState.PassStartedUtc));
            cm.GetMemberMap(s => s.PassCompletedUtc).SetElementName(nameof(IntegritySealingState.PassCompletedUtc));
            cm.GetMemberMap(s => s.StreamsChecked).SetElementName(nameof(IntegritySealingState.StreamsChecked));
            cm.GetMemberMap(s => s.StreamsSealed).SetElementName(nameof(IntegritySealingState.StreamsSealed));
            cm.GetMemberMap(s => s.EventsSealed).SetElementName(nameof(IntegritySealingState.EventsSealed));
        });
    }
}
