// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity;

using Chaos.Mongo.EventStore.Integrity;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using NUnit.Framework;
using System.Reflection;

/// <remarks>
/// Class-map registration is process-global and irreversible, so every test uses dedicated
/// aggregate and event types that no other test registers.
/// </remarks>
public class IntegrityRegistrationTests
{
    [Test]
    public void EventIntegrityClassMap_PinsElementNamesAndIgnoresExtraElements()
    {
        new MongoBuilder(new ServiceCollection()).WithEventStore<RegistrationFormatAggregate>(es => es
                                                                                                  .WithEvent<RegistrationFormatEvent>());

        var classMap = BsonClassMap.LookupClassMap(typeof(EventIntegrity));

        classMap.IgnoreExtraElements.Should().BeTrue();
        classMap.AllMemberMaps.Select(m => m.ElementName)
                .Should().BeEquivalentTo("FormatVersion", "Algorithm", "PreviousHash", "Hash", "SealMode");
    }

    [Test]
    public void IntegrityTypesAndMembers_AreNotPublic()
    {
        var integrityTypes = typeof(EventIntegrity).Assembly
                                                   .GetTypes()
                                                   .Where(t => t.Namespace == typeof(EventIntegrity).Namespace)
                                                   .ToList();

        integrityTypes.Should().NotBeEmpty();
        integrityTypes.Should().OnlyContain(t => !t.IsVisible);

        typeof(Event<RegistrationVisibilityAggregate>)
            .GetProperty(nameof(Event<RegistrationVisibilityAggregate>.Integrity), BindingFlags.Instance | BindingFlags.NonPublic)
            .Should().NotBeNull();
        typeof(MongoEventStoreOptions<RegistrationVisibilityAggregate>)
            .GetProperty(nameof(MongoEventStoreOptions<RegistrationVisibilityAggregate>.IntegrityProtectionEnabled),
                         BindingFlags.Instance | BindingFlags.NonPublic)
            .Should().NotBeNull();
        typeof(MongoEventStoreBuilder<RegistrationVisibilityAggregate>)
            .GetMethod(nameof(MongoEventStoreBuilder<RegistrationVisibilityAggregate>.WithIntegrityProtection),
                       BindingFlags.Instance | BindingFlags.NonPublic)
            .Should().NotBeNull();
    }

    [Test]
    public void WithEventStore_EventMapsReservedElement_ThrowsInvalidOperationException()
    {
        var builder = new MongoBuilder(new ServiceCollection());

        var act = () => builder.WithEventStore<RegistrationCollisionAggregate>(es => es.WithEvent<RegistrationCollidingEvent>());

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*RegistrationCollidingEvent*Marker*'_integrity'*reserved*");
    }

    [Test]
    public void WithEventStore_PreRegisteredBaseMapWithProtection_ThrowsInvalidOperationException()
    {
        BsonClassMap.RegisterClassMap<Event<RegistrationPreRegisteredAggregate>>(cm => cm.AutoMap());
        var builder = new MongoBuilder(new ServiceCollection());

        var act = () => builder.WithEventStore<RegistrationPreRegisteredAggregate>(es => es
                                                                                         .WithEvent<RegistrationPreRegisteredEvent>()
                                                                                         .WithIntegrityProtection());

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*RegistrationPreRegisteredAggregate*registered before the event store*'_integrity'*");
    }

    [Test]
    public void WithEventStore_PreRegisteredBaseMapWithoutProtection_Succeeds()
    {
        BsonClassMap.RegisterClassMap<Event<RegistrationUnprotectedAggregate>>(cm => cm.AutoMap());
        var builder = new MongoBuilder(new ServiceCollection());

        var act = () => builder.WithEventStore<RegistrationUnprotectedAggregate>(es => es.WithEvent<RegistrationUnprotectedEvent>());

        act.Should().NotThrow();
    }

    [Test]
    public void WithEventStore_PreRegisteredEventMapsReservedElement_ThrowsInvalidOperationException()
    {
        BsonClassMap.RegisterClassMap<RegistrationPreRegisteredCollidingEvent>(cm =>
        {
            cm.AutoMap();
            cm.MapMember(e => e.Marker).SetElementName("_integrity");
        });
        var builder = new MongoBuilder(new ServiceCollection());

        var act = () => builder.WithEventStore<RegistrationPreRegisteredCollisionAggregate>(es => es
                                                                                                .WithEvent<
                                                                                                    RegistrationPreRegisteredCollidingEvent>());

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*RegistrationPreRegisteredCollidingEvent*Marker*'_integrity'*reserved*");
    }

    [Test]
    public void WithEventStore_PreRegisteredIntermediateBaseMapsReservedElement_ThrowsInvalidOperationException()
    {
        BsonClassMap.RegisterClassMap<RegistrationIntermediateBase>(cm =>
        {
            cm.AutoMap();
            cm.MapMember(e => e.Marker).SetElementName("_integrity");
        });
        var builder = new MongoBuilder(new ServiceCollection());

        var act = () => builder.WithEventStore<RegistrationIntermediateAggregate>(es => es.WithEvent<RegistrationIntermediateEvent>());

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*RegistrationIntermediateEvent*Marker*RegistrationIntermediateBase*'_integrity'*reserved*");
    }
}

public sealed class RegistrationVisibilityAggregate : Aggregate;

public sealed class RegistrationCollisionAggregate : Aggregate;

public sealed class RegistrationCollidingEvent : Event<RegistrationCollisionAggregate>
{
    [BsonElement("_integrity")]
    public String? Marker { get; set; }

    public override void Execute(RegistrationCollisionAggregate aggregate) { }
}

public sealed class RegistrationPreRegisteredAggregate : Aggregate;

public sealed class RegistrationPreRegisteredEvent : Event<RegistrationPreRegisteredAggregate>
{
    public override void Execute(RegistrationPreRegisteredAggregate aggregate) { }
}

public sealed class RegistrationUnprotectedAggregate : Aggregate;

public sealed class RegistrationUnprotectedEvent : Event<RegistrationUnprotectedAggregate>
{
    public override void Execute(RegistrationUnprotectedAggregate aggregate) { }
}

public sealed class RegistrationPreRegisteredCollisionAggregate : Aggregate;

public sealed class RegistrationPreRegisteredCollidingEvent : Event<RegistrationPreRegisteredCollisionAggregate>
{
    public String? Marker { get; set; }

    public override void Execute(RegistrationPreRegisteredCollisionAggregate aggregate) { }
}

public sealed class RegistrationFormatAggregate : Aggregate;

public sealed class RegistrationFormatEvent : Event<RegistrationFormatAggregate>
{
    public override void Execute(RegistrationFormatAggregate aggregate) { }
}

public sealed class RegistrationIntermediateAggregate : Aggregate;

public abstract class RegistrationIntermediateBase : Event<RegistrationIntermediateAggregate>
{
    public String? Marker { get; set; }
}

public sealed class RegistrationIntermediateEvent : RegistrationIntermediateBase
{
    public override void Execute(RegistrationIntermediateAggregate aggregate) { }
}
