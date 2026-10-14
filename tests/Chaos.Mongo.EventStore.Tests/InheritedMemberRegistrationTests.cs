// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using NUnit.Framework;

/// <summary>
/// Class maps are process-global, so every test registers its own aggregate and event types.
/// </summary>
public class InheritedMemberRegistrationTests
{
    [Test]
    public void WithEventStore_DerivedEventRegisteredBeforeIntermediate_MatchesReverseOrder()
    {
        new MongoBuilder(new ServiceCollection())
            .WithEventStore<InheritanceOrderAggregate>(es => es.WithEvent<InheritanceDerivedFirstEvent>("DerivedFirst")
                                                               .WithEvent<InheritanceBaseFirstEvent>("BaseFirst")
                                                               .WithEvent<InheritanceBaseSecondEvent>("BaseSecond")
                                                               .WithEvent<InheritanceDerivedSecondEvent>("DerivedSecond"));

        var derivedFirst = Serialize<InheritanceOrderAggregate>(new InheritanceDerivedFirstEvent
        {
            Memo = "first"
        });
        var derivedSecond = Serialize<InheritanceOrderAggregate>(new InheritanceDerivedSecondEvent
        {
            Memo = "second"
        });

        Discriminators(derivedFirst).Should().Equal(Root<InheritanceOrderAggregate>(), "BaseFirst", "DerivedFirst");
        Discriminators(derivedSecond).Should().Equal(Root<InheritanceOrderAggregate>(), "BaseSecond", "DerivedSecond");
        derivedFirst[nameof(InheritanceBaseFirstEvent.Memo)].AsString.Should().Be("first");
        derivedSecond[nameof(InheritanceBaseSecondEvent.Memo)].AsString.Should().Be("second");
        Discriminators(Serialize<InheritanceOrderAggregate>(new InheritanceBaseFirstEvent())).Should().Equal(Root<InheritanceOrderAggregate>(), "BaseFirst");
        Discriminators(Serialize<InheritanceOrderAggregate>(new InheritanceBaseSecondEvent())).Should().Equal(Root<InheritanceOrderAggregate>(), "BaseSecond");
    }

    [Test]
    public void WithEventStore_EventDerivedFromIntermediateClasses_RoundTripsInheritedMembers()
    {
        new MongoBuilder(new ServiceCollection())
            .WithEventStore<InheritanceRoundTripAggregate>(es => es.WithEvent<InheritanceRoundTripEvent>("RoundTrip"));
        var @event = new InheritanceRoundTripEvent
        {
            Memo = "memo",
            Detail = 42,
            Note = "note"
        };

        var document = Serialize<InheritanceRoundTripAggregate>(@event);
        var deserialized = BsonSerializer.Deserialize<Event<InheritanceRoundTripAggregate>>(document);

        Discriminators(document).Should().Equal(Root<InheritanceRoundTripAggregate>(), nameof(InheritanceRoundTripBaseEvent), nameof(InheritanceRoundTripMidEvent), "RoundTrip");
        document[nameof(InheritanceRoundTripBaseEvent.Memo)].AsString.Should().Be("memo");
        document[nameof(InheritanceRoundTripMidEvent.Detail)].AsInt32.Should().Be(42);
        deserialized.Should().BeOfType<InheritanceRoundTripEvent>().Which.Should().BeEquivalentTo(@event);
    }

    [Test]
    public void WithEventStore_EventsSharingUnregisteredIntermediate_Succeeds()
    {
        var builder = new MongoBuilder(new ServiceCollection());

        var act = () => builder.WithEventStore<InheritanceSharedAggregate>(es => es.WithEvent<InheritanceSharedFirstEvent>()
                                                                                   .WithEvent<InheritanceSharedSecondEvent>());

        act.Should().NotThrow();
    }

    [Test]
    public void WithEventStore_IntermediateRegisteredByEventStoreMapsReservedElement_ThrowsWithoutRegisteringIt()
    {
        var builder = new MongoBuilder(new ServiceCollection());

        var act = () => builder.WithEventStore<InheritanceReservedAggregate>(es => es.WithEvent<InheritanceReservedEvent>());

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*InheritanceReservedBaseEvent*Marker*InheritanceReservedBaseEvent*'_integrity'*reserved*");
        BsonClassMap.IsClassMapRegistered(typeof(InheritanceReservedBaseEvent)).Should().BeFalse();
        BsonClassMap.IsClassMapRegistered(typeof(InheritanceReservedEvent)).Should().BeFalse();
    }

    [Test]
    public void WithEventStore_IntermediateSharesDiscriminatorWithDerivedEvent_ThrowsInvalidOperationException()
    {
        var builder = new MongoBuilder(new ServiceCollection());

        var act = () => builder.WithEventStore<InheritanceCollisionAggregate>(es => es.WithEvent<InheritanceCollisionV2Event>(nameof(InheritanceCollisionV1Event)));

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*InheritanceCollisionAggregate*'InheritanceCollisionV1Event'*")
           .Which.Message.Should().Contain(nameof(InheritanceCollisionV2Event));
    }

    [Test]
    public void WithEventStore_IntermediateSharesDiscriminatorWithEarlierRegistration_ThrowsInvalidOperationException()
    {
        new MongoBuilder(new ServiceCollection())
            .WithEventStore<InheritanceEarlierAggregate>(es => es.WithEvent<InheritanceEarlierEvent>(nameof(InheritanceEarlierBaseEvent)));
        var builder = new MongoBuilder(new ServiceCollection());

        var act = () => builder.WithEventStore<InheritanceEarlierAggregate>(es => es.WithEvent<InheritanceLaterEvent>());

        act.Should().Throw<InvalidOperationException>()
           .WithMessage($"*'{nameof(InheritanceEarlierBaseEvent)}'*")
           .Which.Message.Should().Contain(nameof(InheritanceEarlierEvent));
    }

    [Test]
    public void WithEventStore_ObservationalEventDerivedDirectly_KeepsDiscriminator()
    {
        new MongoBuilder(new ServiceCollection())
            .WithEventStore<InheritanceObservationalAggregate>(es => es.WithEvent<InheritanceObservedEvent>("Observed"));

        var document = Serialize<InheritanceObservationalAggregate>(new InheritanceObservedEvent());

        Discriminators(document).Should().Equal(Root<InheritanceObservationalAggregate>(), "Observed");
    }

    [Test]
    public void WithEventStore_ObservationalEventWithIntermediate_RoundTripsInheritedMembers()
    {
        new MongoBuilder(new ServiceCollection())
            .WithEventStore<InheritanceObservationalBaseAggregate>(es => es.WithEvent<InheritanceInspectedEvent>("Inspected"));
        var @event = new InheritanceInspectedEvent
        {
            Inspector = "auditor"
        };

        var document = Serialize<InheritanceObservationalBaseAggregate>(@event);
        var deserialized = BsonSerializer.Deserialize<Event<InheritanceObservationalBaseAggregate>>(document);

        Discriminators(document).Should().Equal(Root<InheritanceObservationalBaseAggregate>(), nameof(InheritanceInspectionEvent), "Inspected");
        document[nameof(InheritanceInspectionEvent.Inspector)].AsString.Should().Be("auditor");
        deserialized.Should().BeOfType<InheritanceInspectedEvent>().Which.Inspector.Should().Be("auditor");
    }

    [Test]
    public void WithEventStore_PreRegisteredEventAboveUnregisteredConfiguredIntermediate_AppliesConfiguredDiscriminator()
    {
        BsonClassMap.RegisterClassMap<InheritancePreRegisteredLeafEvent>(cm => cm.AutoMap());

        new MongoBuilder(new ServiceCollection())
            .WithEventStore<InheritancePreRegisteredLeafAggregate>(es => es.WithEvent<InheritancePreRegisteredLeafEvent>("Leaf")
                                                                           .WithEvent<InheritanceConfiguredBaseEvent>("ConfiguredBase"));

        var document = Serialize<InheritancePreRegisteredLeafAggregate>(new InheritancePreRegisteredLeafEvent
        {
            Memo = "memo"
        });

        Discriminators(document).Should().Equal(Root<InheritancePreRegisteredLeafAggregate>(), "ConfiguredBase", nameof(InheritancePreRegisteredLeafEvent));
        document[nameof(InheritanceConfiguredBaseEvent.Memo)].AsString.Should().Be("memo");
    }

    [Test]
    public void WithEventStore_PreRegisteredIntermediateAggregateClassMap_IsUsedUnchanged()
    {
        var intermediateClassMap = BsonClassMap.RegisterClassMap<InheritancePreRegisteredOwnedAggregate>(cm =>
        {
            cm.AutoMap();
            cm.MapMember(a => a.Owner).SetElementName("owner");
        });

        new MongoBuilder(new ServiceCollection())
            .WithEventStore<InheritancePreRegisteredAggregate>(es => es.WithEvent<InheritancePreRegisteredAggregateEvent>());

        var document = new InheritancePreRegisteredAggregate
        {
            Owner = "owner",
            Balance = 7
        }.ToBsonDocument();

        BsonClassMap.GetRegisteredClassMaps().Should().ContainSingle(cm => cm.ClassType == typeof(InheritancePreRegisteredOwnedAggregate))
                    .Which.Should().BeSameAs(intermediateClassMap);
        document["owner"].AsString.Should().Be("owner");
        document[nameof(InheritancePreRegisteredAggregate.Balance)].AsInt32.Should().Be(7);
        BsonSerializer.Deserialize<InheritancePreRegisteredAggregate>(document).Owner.Should().Be("owner");
    }

    [Test]
    public void WithEventStore_PreRegisteredIntermediateClassMap_IsUsedUnchanged()
    {
        var intermediateClassMap = BsonClassMap.RegisterClassMap<InheritancePreRegisteredBaseEvent>(cm =>
        {
            cm.AutoMap();
            cm.MapMember(e => e.Memo).SetElementName("memo");
            cm.SetDiscriminator("PreRegisteredBase");
        });

        new MongoBuilder(new ServiceCollection())
            .WithEventStore<InheritancePreRegisteredEventAggregate>(es => es.WithEvent<InheritancePreRegisteredDerivedEvent>("Derived"));

        var document = Serialize<InheritancePreRegisteredEventAggregate>(new InheritancePreRegisteredDerivedEvent
        {
            Memo = "memo"
        });

        BsonClassMap.GetRegisteredClassMaps().Should().ContainSingle(cm => cm.ClassType == typeof(InheritancePreRegisteredBaseEvent))
                    .Which.Should().BeSameAs(intermediateClassMap);
        Discriminators(document).Should().Equal(Root<InheritancePreRegisteredEventAggregate>(), "PreRegisteredBase", "Derived");
        document["memo"].AsString.Should().Be("memo");
    }

    [Test]
    public void WithEventStore_PreRegisteredIntermediateSharesDiscriminator_ThrowsInvalidOperationException()
    {
        BsonClassMap.RegisterClassMap<InheritancePreRegisteredCollisionBaseEvent>(cm =>
        {
            cm.AutoMap();
            cm.SetDiscriminator("Shared");
        });
        var builder = new MongoBuilder(new ServiceCollection());

        var act = () => builder.WithEventStore<InheritancePreRegisteredCollisionAggregate>(es => es.WithEvent<InheritancePreRegisteredCollisionEvent>("Shared"));

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*InheritancePreRegisteredCollisionBaseEvent*'Shared'*")
           .Which.Message.Should().Contain(nameof(InheritancePreRegisteredCollisionEvent));
    }

    [Test]
    public void WithEventStore_StoredShorterDiscriminatorArray_DeserializesToEventType()
    {
        new MongoBuilder(new ServiceCollection())
            .WithEventStore<InheritanceLegacyAggregate>(es => es.WithEvent<InheritanceLegacyEvent>("Legacy"));
        var document = Serialize<InheritanceLegacyAggregate>(new InheritanceLegacyEvent
        {
            Note = "note"
        });

        // Earlier versions stored neither the intermediate discriminator nor the inherited members.
        document["_t"] = new BsonArray
        {
            Root<InheritanceLegacyAggregate>(),
            "Legacy"
        };
        document.Remove(nameof(InheritanceLegacyBaseEvent.Memo));
        var deserialized = BsonSerializer.Deserialize<Event<InheritanceLegacyAggregate>>(document);

        deserialized.Should().BeOfType<InheritanceLegacyEvent>().Which.Note.Should().Be("note");
    }

    [Test]
    public void WithEventStore_UnregisteredIntermediateEvents_UseDefaultDiscriminators()
    {
        new MongoBuilder(new ServiceCollection())
            .WithEventStore<InheritanceDefaultAggregate>(es => es.WithEvent<InheritanceDefaultEvent>("Default"));

        var document = Serialize<InheritanceDefaultAggregate>(new InheritanceDefaultEvent
        {
            Payload = "payload"
        });

        Discriminators(document).Should().Equal(Root<InheritanceDefaultAggregate>(), "Attributed", "InheritanceGenericBaseEvent<String>", "Default");
        document[nameof(InheritanceGenericBaseEvent<String>.Payload)].AsString.Should().Be("payload");
    }

    private static IEnumerable<String> Discriminators(BsonDocument document)
        => document["_t"].AsBsonArray.Select(value => value.AsString);

    private static String Root<TAggregate>()
        => $"Event<{typeof(TAggregate).Name}>";

    private static BsonDocument Serialize<TAggregate>(Event<TAggregate> @event)
        where TAggregate : class, IAggregate, new()
        => @event.ToBsonDocument<Event<TAggregate>>();
}

public sealed class InheritanceOrderAggregate : Aggregate;

public class InheritanceBaseFirstEvent : Event<InheritanceOrderAggregate>
{
    public String? Memo { get; set; }

    public override void Execute(InheritanceOrderAggregate aggregate) { }
}

public sealed class InheritanceDerivedFirstEvent : InheritanceBaseFirstEvent;

public class InheritanceBaseSecondEvent : Event<InheritanceOrderAggregate>
{
    public String? Memo { get; set; }

    public override void Execute(InheritanceOrderAggregate aggregate) { }
}

public sealed class InheritanceDerivedSecondEvent : InheritanceBaseSecondEvent;

public sealed class InheritanceRoundTripAggregate : Aggregate;

public abstract class InheritanceRoundTripBaseEvent : Event<InheritanceRoundTripAggregate>
{
    public String? Memo { get; set; }
}

public abstract class InheritanceRoundTripMidEvent : InheritanceRoundTripBaseEvent
{
    public Int32 Detail { get; set; }
}

public sealed class InheritanceRoundTripEvent : InheritanceRoundTripMidEvent
{
    public String? Note { get; set; }

    public override void Execute(InheritanceRoundTripAggregate aggregate) { }
}

public sealed class InheritanceSharedAggregate : Aggregate;

public abstract class InheritanceSharedBaseEvent : Event<InheritanceSharedAggregate>
{
    public String? Memo { get; set; }

    public override void Execute(InheritanceSharedAggregate aggregate) { }
}

public sealed class InheritanceSharedFirstEvent : InheritanceSharedBaseEvent;

public sealed class InheritanceSharedSecondEvent : InheritanceSharedBaseEvent;

public sealed class InheritanceReservedAggregate : Aggregate;

public abstract class InheritanceReservedBaseEvent : Event<InheritanceReservedAggregate>
{
    [BsonElement("_integrity")]
    public String? Marker { get; set; }
}

public sealed class InheritanceReservedEvent : InheritanceReservedBaseEvent
{
    public override void Execute(InheritanceReservedAggregate aggregate) { }
}

public sealed class InheritanceCollisionAggregate : Aggregate;

public class InheritanceCollisionV1Event : Event<InheritanceCollisionAggregate>
{
    public override void Execute(InheritanceCollisionAggregate aggregate) { }
}

public sealed class InheritanceCollisionV2Event : InheritanceCollisionV1Event;

public sealed class InheritanceEarlierAggregate : Aggregate;

public sealed class InheritanceEarlierEvent : Event<InheritanceEarlierAggregate>
{
    public override void Execute(InheritanceEarlierAggregate aggregate) { }
}

public abstract class InheritanceEarlierBaseEvent : Event<InheritanceEarlierAggregate>;

public sealed class InheritanceLaterEvent : InheritanceEarlierBaseEvent
{
    public override void Execute(InheritanceEarlierAggregate aggregate) { }
}

public sealed class InheritanceObservationalAggregate : Aggregate;

public sealed class InheritanceObservedEvent : ObservationalEvent<InheritanceObservationalAggregate>;

public sealed class InheritanceObservationalBaseAggregate : Aggregate;

public abstract class InheritanceInspectionEvent : ObservationalEvent<InheritanceObservationalBaseAggregate>
{
    public String? Inspector { get; set; }
}

public sealed class InheritanceInspectedEvent : InheritanceInspectionEvent;

public sealed class InheritancePreRegisteredLeafAggregate : Aggregate;

public class InheritanceConfiguredBaseEvent : Event<InheritancePreRegisteredLeafAggregate>
{
    public String? Memo { get; set; }

    public override void Execute(InheritancePreRegisteredLeafAggregate aggregate) { }
}

public sealed class InheritancePreRegisteredLeafEvent : InheritanceConfiguredBaseEvent;

public abstract class InheritancePreRegisteredOwnedAggregate : Aggregate
{
    public String? Owner { get; set; }
}

public sealed class InheritancePreRegisteredAggregate : InheritancePreRegisteredOwnedAggregate
{
    public Int32 Balance { get; set; }
}

public sealed class InheritancePreRegisteredAggregateEvent : Event<InheritancePreRegisteredAggregate>
{
    public override void Execute(InheritancePreRegisteredAggregate aggregate) { }
}

public sealed class InheritancePreRegisteredEventAggregate : Aggregate;

public abstract class InheritancePreRegisteredBaseEvent : Event<InheritancePreRegisteredEventAggregate>
{
    public String? Memo { get; set; }
}

public sealed class InheritancePreRegisteredDerivedEvent : InheritancePreRegisteredBaseEvent
{
    public override void Execute(InheritancePreRegisteredEventAggregate aggregate) { }
}

public sealed class InheritancePreRegisteredCollisionAggregate : Aggregate;

public abstract class InheritancePreRegisteredCollisionBaseEvent : Event<InheritancePreRegisteredCollisionAggregate>;

public sealed class InheritancePreRegisteredCollisionEvent : InheritancePreRegisteredCollisionBaseEvent
{
    public override void Execute(InheritancePreRegisteredCollisionAggregate aggregate) { }
}

public sealed class InheritanceLegacyAggregate : Aggregate;

public abstract class InheritanceLegacyBaseEvent : Event<InheritanceLegacyAggregate>
{
    public String? Memo { get; set; }
}

public sealed class InheritanceLegacyEvent : InheritanceLegacyBaseEvent
{
    public String? Note { get; set; }

    public override void Execute(InheritanceLegacyAggregate aggregate) { }
}

public sealed class InheritanceDefaultAggregate : Aggregate;

[BsonDiscriminator("Attributed")]
public abstract class InheritanceAttributedBaseEvent : Event<InheritanceDefaultAggregate>;

public abstract class InheritanceGenericBaseEvent<TPayload> : InheritanceAttributedBaseEvent
{
    public TPayload? Payload { get; set; }
}

public sealed class InheritanceDefaultEvent : InheritanceGenericBaseEvent<String>
{
    public override void Execute(InheritanceDefaultAggregate aggregate) { }
}
