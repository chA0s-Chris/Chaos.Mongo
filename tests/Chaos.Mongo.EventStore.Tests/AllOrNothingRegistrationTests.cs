// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests;

using Chaos.Mongo.EventStore.Integrity;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using NUnit.Framework;

/// <summary>
/// Class maps are process-global, so every test registers its own aggregate and event types. Each failing
/// configuration registers a passing event type with an intermediate base first, which earlier versions registered
/// before the failing check ran.
/// </summary>
public class AllOrNothingRegistrationTests
{
    [Test]
    public void WithEventStore_ConfiguredReservedErasureDiscriminator_RegistersNoEventClassMapUntilCorrectedRetry()
    {
        var builder = new MongoBuilder(new ServiceCollection());

        var act = () => builder.WithEventStore<AtomicConfiguredReservedAggregate>(es => es.WithEvent<AtomicConfiguredReservedPassingEvent>("Passing")
                                                                                          .WithEvent<AtomicConfiguredReservedEvent>(
                                                                                              PersonalDataErased<AtomicConfiguredReservedAggregate>.Discriminator));

        act.Should().Throw<InvalidOperationException>().WithMessage("*AtomicConfiguredReservedEvent*reserved*");
        BsonClassMap.IsClassMapRegistered(typeof(AtomicConfiguredReservedPassingEvent)).Should().BeFalse();
        BsonClassMap.IsClassMapRegistered(typeof(AtomicConfiguredReservedBaseEvent)).Should().BeFalse();
        BsonClassMap.IsClassMapRegistered(typeof(AtomicConfiguredReservedEvent)).Should().BeFalse();

        builder.WithEventStore<AtomicConfiguredReservedAggregate>(es => es.WithEvent<AtomicConfiguredReservedPassingEvent>("Passing")
                                                                          .WithEvent<AtomicConfiguredReservedEvent>("Reserved"));

        RoundTrip<AtomicConfiguredReservedAggregate>(new AtomicConfiguredReservedEvent()).Should().Be("Reserved");
        RoundTrip<AtomicConfiguredReservedAggregate>(new AtomicConfiguredReservedPassingEvent()).Should().Be("Passing");
    }

    [Test]
    public void WithEventStore_IntermediateMapsReservedElement_RegistersNoEventClassMapUntilCorrectedRetry()
    {
        var builder = new MongoBuilder(new ServiceCollection());

        var act = () => builder.WithEventStore<AtomicReservedElementAggregate>(es => es.WithEvent<AtomicReservedElementPassingEvent>("Passing")
                                                                                       .WithEvent<AtomicReservedElementEvent>("Colliding"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*AtomicReservedElementBaseEvent*'_integrity'*reserved*");
        BsonClassMap.IsClassMapRegistered(typeof(AtomicReservedElementPassingEvent)).Should().BeFalse();
        BsonClassMap.IsClassMapRegistered(typeof(AtomicReservedElementPassingBaseEvent)).Should().BeFalse();
        BsonClassMap.IsClassMapRegistered(typeof(AtomicReservedElementBaseEvent)).Should().BeFalse();
        BsonClassMap.IsClassMapRegistered(typeof(AtomicReservedElementEvent)).Should().BeFalse();

        builder.WithEventStore<AtomicReservedElementAggregate>(es => es.WithEvent<AtomicReservedElementPassingEvent>("Corrected"));

        RoundTrip<AtomicReservedElementAggregate>(new AtomicReservedElementPassingEvent()).Should().Be("Corrected");
    }

    [Test]
    public void WithEventStore_PreRegisteredReservedErasureDiscriminator_RegistersNoEventClassMap()
    {
        BsonClassMap.RegisterClassMap<AtomicPreRegisteredReservedEvent>(cm =>
        {
            cm.AutoMap();
            cm.SetDiscriminator(PersonalDataErased<AtomicPreRegisteredReservedAggregate>.Discriminator);
        });
        var builder = new MongoBuilder(new ServiceCollection());

        var act = () => builder.WithEventStore<AtomicPreRegisteredReservedAggregate>(es => es.WithEvent<AtomicPreRegisteredReservedPassingEvent>("Passing")
                                                                                             .WithEvent<AtomicPreRegisteredReservedEvent>());

        act.Should().Throw<InvalidOperationException>().WithMessage("*AtomicPreRegisteredReservedEvent*registered before the event store*reserved*");
        BsonClassMap.IsClassMapRegistered(typeof(AtomicPreRegisteredReservedPassingEvent)).Should().BeFalse();
        BsonClassMap.IsClassMapRegistered(typeof(AtomicPreRegisteredReservedBaseEvent)).Should().BeFalse();
    }

    [Test]
    public void WithEventStore_SharedDiscriminator_RegistersNoEventClassMapUntilCorrectedRetry()
    {
        var builder = new MongoBuilder(new ServiceCollection());

        var act = () => builder.WithEventStore<AtomicSharedAggregate>(es => es.WithEvent<AtomicSharedPassingEvent>("Passing")
                                                                              .WithEvent<AtomicSharedFirstEvent>("Shared")
                                                                              .WithEvent<AtomicSharedSecondEvent>("Shared"));

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*AtomicSharedFirstEvent*AtomicSharedSecondEvent*'Shared'*");
        BsonClassMap.IsClassMapRegistered(typeof(AtomicSharedPassingEvent)).Should().BeFalse();
        BsonClassMap.IsClassMapRegistered(typeof(AtomicSharedBaseEvent)).Should().BeFalse();
        BsonClassMap.IsClassMapRegistered(typeof(AtomicSharedFirstEvent)).Should().BeFalse();
        BsonClassMap.IsClassMapRegistered(typeof(AtomicSharedSecondEvent)).Should().BeFalse();

        builder.WithEventStore<AtomicSharedAggregate>(es => es.WithEvent<AtomicSharedPassingEvent>("Passing")
                                                              .WithEvent<AtomicSharedFirstEvent>("First")
                                                              .WithEvent<AtomicSharedSecondEvent>("Second"));

        RoundTrip<AtomicSharedAggregate>(new AtomicSharedFirstEvent()).Should().Be("First");
        RoundTrip<AtomicSharedAggregate>(new AtomicSharedSecondEvent()).Should().Be("Second");
        RoundTrip<AtomicSharedAggregate>(new AtomicSharedPassingEvent()).Should().Be("Passing");
    }

    /// <summary>
    /// Serializes and deserializes the event and returns its stored discriminator after checking that it reads
    /// back as the same type.
    /// </summary>
    private static String RoundTrip<TAggregate>(Event<TAggregate> @event)
        where TAggregate : class, IAggregate, new()
    {
        var document = @event.ToBsonDocument<Event<TAggregate>>();
        BsonSerializer.Deserialize<Event<TAggregate>>(document).Should().BeOfType(@event.GetType());
        return document["_t"].AsBsonArray.Last().AsString;
    }
}

public sealed class AtomicSharedAggregate : Aggregate;

public abstract class AtomicSharedBaseEvent : Event<AtomicSharedAggregate>
{
    public String? Memo { get; set; }
}

public sealed class AtomicSharedPassingEvent : AtomicSharedBaseEvent
{
    public override void Execute(AtomicSharedAggregate aggregate) { }
}

public sealed class AtomicSharedFirstEvent : Event<AtomicSharedAggregate>
{
    public override void Execute(AtomicSharedAggregate aggregate) { }
}

public sealed class AtomicSharedSecondEvent : Event<AtomicSharedAggregate>
{
    public override void Execute(AtomicSharedAggregate aggregate) { }
}

public sealed class AtomicConfiguredReservedAggregate : Aggregate;

public abstract class AtomicConfiguredReservedBaseEvent : Event<AtomicConfiguredReservedAggregate>
{
    public String? Memo { get; set; }
}

public sealed class AtomicConfiguredReservedPassingEvent : AtomicConfiguredReservedBaseEvent
{
    public override void Execute(AtomicConfiguredReservedAggregate aggregate) { }
}

public sealed class AtomicConfiguredReservedEvent : Event<AtomicConfiguredReservedAggregate>
{
    public override void Execute(AtomicConfiguredReservedAggregate aggregate) { }
}

public sealed class AtomicPreRegisteredReservedAggregate : Aggregate;

public abstract class AtomicPreRegisteredReservedBaseEvent : Event<AtomicPreRegisteredReservedAggregate>
{
    public String? Memo { get; set; }
}

public sealed class AtomicPreRegisteredReservedPassingEvent : AtomicPreRegisteredReservedBaseEvent
{
    public override void Execute(AtomicPreRegisteredReservedAggregate aggregate) { }
}

public sealed class AtomicPreRegisteredReservedEvent : Event<AtomicPreRegisteredReservedAggregate>
{
    public override void Execute(AtomicPreRegisteredReservedAggregate aggregate) { }
}

public sealed class AtomicReservedElementAggregate : Aggregate;

public abstract class AtomicReservedElementPassingBaseEvent : Event<AtomicReservedElementAggregate>
{
    public String? Memo { get; set; }
}

public sealed class AtomicReservedElementPassingEvent : AtomicReservedElementPassingBaseEvent
{
    public override void Execute(AtomicReservedElementAggregate aggregate) { }
}

public abstract class AtomicReservedElementBaseEvent : Event<AtomicReservedElementAggregate>
{
    [BsonElement("_integrity")]
    public String? Marker { get; set; }
}

public sealed class AtomicReservedElementEvent : AtomicReservedElementBaseEvent
{
    public override void Execute(AtomicReservedElementAggregate aggregate) { }
}
