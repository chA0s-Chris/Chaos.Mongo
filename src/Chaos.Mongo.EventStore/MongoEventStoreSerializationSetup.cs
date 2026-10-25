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
    /// <summary>
    /// Guards the ownership check and the registration of a checkpoint class map with its ownership record, so a
    /// concurrent registration of the same aggregate sees either no class map or an owned one.
    /// </summary>
    private static readonly Object CheckpointClassMapLock = new();

    private static readonly GuidSerializer GuidStandardSerializer = new(GuidRepresentation.Standard);

    /// <summary>
    /// The checkpoint class maps the event store registered itself. Class maps are process-global, so later
    /// registrations, for example in a separate container, reuse them, while a class map registered by a consumer is
    /// rejected under protection. Read and written only under <see cref="CheckpointClassMapLock"/>.
    /// </summary>
    private static readonly HashSet<Type> OwnCheckpointClassMaps = [];

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
    /// Registers BsonClassMaps for the aggregate type and its event types along their class hierarchies, so members
    /// declared on intermediate base classes are stored. Guid fields are explicitly configured with
    /// <see cref="GuidRepresentation.Standard"/>.
    /// </summary>
    /// <typeparam name="TAggregate">The aggregate type.</typeparam>
    /// <param name="options">The event store options containing event type registrations.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when an event type maps a member to the reserved <c>_integrity</c> element, when an event type uses the
    /// discriminator reserved for the personal data erasure event, when two registered event class maps of the
    /// aggregate share a discriminator, when integrity protection is enabled but the event base class map was
    /// registered without the integrity member, or when the event base class map or the class map declaring
    /// <see cref="IAggregate.Revision"/> was registered without the revision member. In each of these cases, no event
    /// class map of the call is registered, so a retry with corrected options can succeed unless the failure was caused
    /// by a class map registered before the event store.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the event types contain <see cref="ObservationalEvent{TAggregate}"/> itself.
    /// </exception>
    public static void RegisterClassMaps<TAggregate>(MongoEventStoreOptions<TAggregate> options)
        where TAggregate : class, IAggregate, new()
    {
        // The observational base class is left out of the class map hierarchy, so it has no class map to register.
        if (options.EventTypes.ContainsKey(typeof(ObservationalEvent<TAggregate>)))
        {
            throw new ArgumentException(
                $"The observational event base class of aggregate type {typeof(TAggregate).Name} cannot be registered " +
                "itself. Register the concrete or intermediate observational event types instead.",
                nameof(options));
        }

        EnsureCheckpointClassMapOwned(options);

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

                var aggregateClassMaps = new List<BsonClassMap>();
                BuildClassMapHierarchy(aggregateType, typeof(Aggregate), null, aggregateClassMaps, static _ => { });
                RegisterBuiltClassMaps(aggregateClassMaps);
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
        RegisterPersonalDataErasedClassMap<TAggregate>();

        // Build the missing class maps of each event type along its hierarchy and register them only once every check
        // has passed, because class maps cannot be unregistered. A built intermediate type receives its configured
        // discriminator even when a derived event type is built first.
        var eventClassMaps = new List<BsonClassMap>();
        foreach (var (eventType, discriminator) in options.EventTypes)
        {
            if (String.Equals(discriminator, PersonalDataErased<TAggregate>.Discriminator, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Event type {eventType.Name} uses the discriminator '{discriminator}', which is reserved by the event store " +
                    "for its personal data erasure event.");
            }

            if (BsonClassMap.IsClassMapRegistered(eventType))
            {
                var registeredClassMap = GetRegisteredClassMap(eventType);
                if (String.Equals(registeredClassMap.Discriminator, PersonalDataErased<TAggregate>.Discriminator, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"The class map of event type {eventType.Name} was registered before the event store with the discriminator " +
                        $"'{registeredClassMap.Discriminator}', which is reserved by the event store for its personal data erasure event.");
                }

                EnsureNoReservedElementCollision<TAggregate>(registeredClassMap, eventClassMaps);
            }

            BuildClassMapHierarchy(eventType, eventBaseType, typeof(ObservationalEvent<TAggregate>), eventClassMaps, classMap =>
            {
                if (options.EventTypes.TryGetValue(classMap.ClassType, out var configuredDiscriminator))
                {
                    classMap.SetDiscriminator(configuredDiscriminator);
                }

                EnsureNoReservedElementCollision<TAggregate>(classMap, eventClassMaps);
            });
        }

        EnsureUniqueEventDiscriminators<TAggregate>(eventClassMaps);
        RegisterBuiltClassMaps(eventClassMaps);

        // Derive the replay filter from the registered class maps rather than from EventTypes, because class
        // maps registered by consumers keep their own discriminators. The erasure event is observational too.
        options.ObservationalEventDiscriminators = options.EventTypes.Keys
                                                          .Where(t => typeof(ObservationalEvent<TAggregate>).IsAssignableFrom(t))
                                                          .Select(t => GetRegisteredClassMap(t).Discriminator)
                                                          .Append(PersonalDataErased<TAggregate>.Discriminator)
                                                          .ToList();

        // Marks are validated whether or not protection is enabled, so a misconfigured mark surfaces before the
        // feature is switched on.
        options.PersonalDataPaths = PersonalDataPathResolver.Resolve(options);

        // Register CheckpointId
        if (!BsonClassMap.IsClassMapRegistered(typeof(CheckpointId)))
        {
            BsonClassMap.RegisterClassMap<CheckpointId>(cm =>
            {
                cm.AutoMap();
                cm.MapMember(c => c.AggregateId).SetSerializer(GuidStandardSerializer);
            });
        }

        // Register CheckpointDocument<TAggregate>. The chain hash is mapped regardless of whether protection is enabled,
        // so a protected store can reuse a class map registered for an unprotected one.
        var checkpointDocumentType = typeof(CheckpointDocument<TAggregate>);
        lock (CheckpointClassMapLock)
        {
            if (!BsonClassMap.IsClassMapRegistered(checkpointDocumentType))
            {
                BsonClassMap.RegisterClassMap<CheckpointDocument<TAggregate>>(cm =>
                {
                    cm.AutoMap();
                    cm.MapIdMember(c => c.Id);
                    cm.MapMember(c => c.ChainHash)
                      .SetElementName(CheckpointDocument<TAggregate>.ChainHashElementName)
                      .SetIgnoreIfNull(true);
                });
                OwnCheckpointClassMaps.Add(checkpointDocumentType);
            }
        }
    }

    /// <summary>
    /// Builds the class map of <paramref name="classType"/> on top of the class map of its direct base type, so
    /// members declared on intermediate classes are mapped. Missing base types are built first, including those
    /// above class maps registered by consumers, before <paramref name="configure"/> sees the new class map.
    /// Nothing is registered, so a failing check leaves the registry unchanged. Intermediate base class maps are not
    /// looked up with <see cref="BsonClassMap.LookupClassMap"/>, because that freezes them and auto-registers their
    /// missing base types with default settings.
    /// </summary>
    /// <param name="classType">The type to build.</param>
    /// <param name="rootType">The registered base type at which building stops.</param>
    /// <param name="transparentType">
    /// A base type without stored members that is left out of the class map hierarchy, or <c>null</c>.
    /// </param>
    /// <param name="builtClassMaps">
    /// The class maps built so far, to which each new class map is added after its base types.
    /// </param>
    /// <param name="configure">Configures each class map created by this method before it is added.</param>
    private static void BuildClassMapHierarchy(Type classType,
                                               Type rootType,
                                               Type? transparentType,
                                               List<BsonClassMap> builtClassMaps,
                                               Action<BsonClassMap> configure)
    {
        if (classType == rootType || classType == transparentType || classType.BaseType is not { } baseType)
        {
            return;
        }

        BuildClassMapHierarchy(baseType, rootType, transparentType, builtClassMaps, configure);
        if (BsonClassMap.IsClassMapRegistered(classType) || builtClassMaps.Exists(classMap => classMap.ClassType == classType))
        {
            return;
        }

        var baseClassMap = baseType == rootType || baseType == transparentType
            ? BsonClassMap.LookupClassMap(rootType)
            : builtClassMaps.Find(classMap => classMap.ClassType == baseType) ?? GetRegisteredClassMap(baseType);
        var classMap = new BsonClassMap(classType, baseClassMap);
        classMap.AutoMap();
        configure(classMap);
        builtClassMaps.Add(classMap);
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

    /// <summary>
    /// Under protection, checkpoints record the chain hash in a non-public member that only the event store's own
    /// class map maps. A checkpoint class map registered by a consumer is rejected before anything is registered.
    /// </summary>
    private static void EnsureCheckpointClassMapOwned<TAggregate>(MongoEventStoreOptions<TAggregate> options)
        where TAggregate : class, IAggregate, new()
    {
        var checkpointDocumentType = typeof(CheckpointDocument<TAggregate>);
        lock (CheckpointClassMapLock)
        {
            if (!options.IntegrityProtectionEnabled ||
                !BsonClassMap.IsClassMapRegistered(checkpointDocumentType) ||
                OwnCheckpointClassMaps.Contains(checkpointDocumentType))
            {
                return;
            }
        }

        throw new InvalidOperationException(
            $"Integrity protection is enabled for aggregate type {typeof(TAggregate).Name}, but the class map of " +
            $"{nameof(CheckpointDocument<TAggregate>)} was registered before the event store. Checkpoints record their chain hash " +
            "through the event store's own class map; let the event store register it.");
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
    /// set once a class map is frozen, so intermediate base classes registered by consumers or built by this call
    /// are covered.
    /// </summary>
    private static void EnsureNoReservedElementCollision<TAggregate>(BsonClassMap eventClassMap, IEnumerable<BsonClassMap> builtClassMaps)
        where TAggregate : class, IAggregate, new()
    {
        var knownClassMaps = BsonClassMap.GetRegisteredClassMaps().Concat(builtClassMaps).ToDictionary(classMap => classMap.ClassType);
        for (var type = eventClassMap.ClassType;
             type is not null && type != typeof(Event<TAggregate>);
             type = type.BaseType)
        {
            var classMap = type == eventClassMap.ClassType ? eventClassMap : knownClassMaps.GetValueOrDefault(type);
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

    /// <summary>
    /// Registering class maps for intermediate types adds their discriminators to the driver's registry. The driver
    /// accepts a discriminator shared by two types of one hierarchy, but then fails every read of it as ambiguous.
    /// Class maps are process-global, so every registered event class map of the aggregate is checked together with
    /// those built by this call, including those of earlier registrations of the same aggregate and those registered
    /// by consumers.
    /// </summary>
    private static void EnsureUniqueEventDiscriminators<TAggregate>(IEnumerable<BsonClassMap> builtClassMaps)
        where TAggregate : class, IAggregate, new()
    {
        var typesByDiscriminator = new Dictionary<String, Type>();
        foreach (var classMap in BsonClassMap.GetRegisteredClassMaps().Concat(builtClassMaps))
        {
            if (!typeof(Event<TAggregate>).IsAssignableFrom(classMap.ClassType) || classMap.Discriminator is not { } discriminator)
            {
                continue;
            }

            if (!typesByDiscriminator.TryAdd(discriminator, classMap.ClassType))
            {
                throw new InvalidOperationException(
                    $"Event types {typesByDiscriminator[discriminator].Name} and {classMap.ClassType.Name} of aggregate type " +
                    $"{typeof(TAggregate).Name} share the discriminator '{discriminator}', so events stored with it could " +
                    "not be read. Register one of them with a distinct discriminator, for example with " +
                    "WithEvent<TEvent>(discriminator).");
            }
        }
    }

    private static BsonClassMap GetRegisteredClassMap(Type classType)
        => BsonClassMap.GetRegisteredClassMaps().First(classMap => classMap.ClassType == classType);

    private static Boolean HasElementName(BsonClassMap classMap, String memberName, String elementName)
        => String.Equals(classMap.GetMemberMap(memberName)?.ElementName, elementName, StringComparison.Ordinal);

    /// <summary>
    /// Registers class maps built by <see cref="BuildClassMapHierarchy"/>, base types first.
    /// </summary>
    private static void RegisterBuiltClassMaps(List<BsonClassMap> builtClassMaps)
    {
        foreach (var classMap in builtClassMaps)
        {
            BsonClassMap.RegisterClassMap(classMap);
        }
    }

    private static void RegisterEventIntegrityClassMap()
    {
        RegisterPersonalDataCommitmentClassMap();
        if (BsonClassMap.IsClassMapRegistered(typeof(EventIntegrity)))
        {
            return;
        }

        // Element names are pinned so convention packs cannot change the stored format, and extra
        // elements are ignored so fields added by later format versions do not break typed reads.
        // Every member is required, so a removed member is malformed instead of silently defaulting,
        // which would, for example, turn a missing seal mode into Append. The personal data commitments
        // are the only optional member: they are omitted when no path is recorded, so events without
        // personal data keep the original format.
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
            cm.GetMemberMap(i => i.PersonalData).SetElementName(nameof(EventIntegrity.PersonalData)).SetIgnoreIfNull(true);
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
            cm.GetMemberMap(s => s.StreamsFailed).SetElementName(nameof(IntegritySealingState.StreamsFailed));
            cm.GetMemberMap(s => s.EventsSealed).SetElementName(nameof(IntegritySealingState.EventsSealed));
        });
    }

    private static void RegisterPersonalDataCommitmentClassMap()
    {
        if (BsonClassMap.IsClassMapRegistered(typeof(PersonalDataCommitment)))
        {
            return;
        }

        // The salt is the only optional member: its absence records that the value was erased.
        BsonClassMap.RegisterClassMap<PersonalDataCommitment>(cm =>
        {
            cm.AutoMap();
            cm.SetIgnoreExtraElements(true);
            cm.GetMemberMap(c => c.Path).SetElementName(nameof(PersonalDataCommitment.Path)).SetIsRequired(true);
            cm.GetMemberMap(c => c.Salt).SetElementName(nameof(PersonalDataCommitment.Salt)).SetIgnoreIfNull(true);
            cm.GetMemberMap(c => c.Commitment).SetElementName(nameof(PersonalDataCommitment.Commitment)).SetIsRequired(true);
        });
    }

    /// <summary>
    /// Registers the erasure event for the aggregate type, whether or not protection is enabled, so streams
    /// containing it still deserialize. Element names are pinned because the verifier reads the event raw, and the
    /// eraser's marker is never mapped, even by a convention that maps non-public members, so it cannot be stored and
    /// read back to pass the append guard. A class map registered before the event store must have the same shape.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a class map of the erasure event or its entries was registered before the event store with another
    /// discriminator, other element names or the marker mapped.
    /// </exception>
    private static void RegisterPersonalDataErasedClassMap<TAggregate>()
        where TAggregate : class, IAggregate, new()
    {
        RegisterPersonalDataErasureEntryClassMap();
        var eventType = typeof(PersonalDataErased<TAggregate>);
        if (BsonClassMap.IsClassMapRegistered(eventType))
        {
            var registered = GetRegisteredClassMap(eventType);
            if (!String.Equals(registered.Discriminator, PersonalDataErased<TAggregate>.Discriminator, StringComparison.Ordinal) ||
                !HasElementName(registered, nameof(PersonalDataErased<TAggregate>.Reference), PersonalDataErased<TAggregate>.ReferenceElement) ||
                !HasElementName(registered, nameof(PersonalDataErased<TAggregate>.Entries), PersonalDataErased<TAggregate>.EntriesElement) ||
                registered.GetMemberMap(nameof(PersonalDataErased<TAggregate>.CreatedByEraser)) is not null)
            {
                throw new InvalidOperationException(
                    $"The class map of {eventType.Name} for {typeof(TAggregate).Name} was registered before the event store with another discriminator, " +
                    "other element names or the eraser's marker mapped, so verification would not recognize its erasure records. Let the event " +
                    "store register it.");
            }

            return;
        }

        var classMap = new BsonClassMap(eventType, BsonClassMap.LookupClassMap(typeof(Event<TAggregate>)));
        classMap.AutoMap();
        classMap.SetDiscriminator(PersonalDataErased<TAggregate>.Discriminator);
        classMap.GetMemberMap(nameof(PersonalDataErased<TAggregate>.Reference)).SetElementName(PersonalDataErased<TAggregate>.ReferenceElement);
        classMap.GetMemberMap(nameof(PersonalDataErased<TAggregate>.Entries)).SetElementName(PersonalDataErased<TAggregate>.EntriesElement);
        if (classMap.GetMemberMap(nameof(PersonalDataErased<TAggregate>.CreatedByEraser)) is { } marker)
        {
            classMap.UnmapMember(marker.MemberInfo);
        }

        BsonClassMap.RegisterClassMap(classMap);
    }

    /// <summary>
    /// Registers the erasure entries with pinned element names, or checks a class map registered before the event store.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a class map of the erasure entries was registered before the event store with other element names.
    /// </exception>
    private static void RegisterPersonalDataErasureEntryClassMap()
    {
        if (BsonClassMap.IsClassMapRegistered(typeof(PersonalDataErasureEntry)))
        {
            var registered = GetRegisteredClassMap(typeof(PersonalDataErasureEntry));
            if (!HasElementName(registered, nameof(PersonalDataErasureEntry.Version), PersonalDataErasureEntry.VersionElement) ||
                !HasElementName(registered, nameof(PersonalDataErasureEntry.Paths), PersonalDataErasureEntry.PathsElement))
            {
                throw new InvalidOperationException(
                    $"The class map of {nameof(PersonalDataErasureEntry)} was registered before the event store with other element names, so " +
                    "verification would not recognize erasure records. Let the event store register it.");
            }

            return;
        }

        BsonClassMap.RegisterClassMap<PersonalDataErasureEntry>(cm =>
        {
            cm.AutoMap();
            cm.SetIgnoreExtraElements(true);
            cm.GetMemberMap(e => e.Version).SetElementName(PersonalDataErasureEntry.VersionElement);
            cm.GetMemberMap(e => e.Paths).SetElementName(PersonalDataErasureEntry.PathsElement);
        });
    }
}
