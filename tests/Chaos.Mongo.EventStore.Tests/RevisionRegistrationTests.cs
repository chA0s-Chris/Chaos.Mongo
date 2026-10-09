// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson.Serialization;
using NUnit.Framework;

public class RevisionRegistrationTests
{
    [Test]
    public void WithEventStore_PreRegisteredAggregateMapWithoutRevision_ThrowsInvalidOperationException()
    {
        BsonClassMap.RegisterClassMap<RevisionRegistrationPlainAggregate>(cm =>
        {
            cm.AutoMap();
            cm.UnmapMember(a => a.Revision);
        });
        var builder = new MongoBuilder(new ServiceCollection());

        var act = () => builder.WithEventStore<RevisionRegistrationPlainAggregate>(es => es.WithEvent<RevisionRegistrationPlainEvent>());

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*RevisionRegistrationPlainAggregate*registered before the event store*Revision member*");
    }

    [Test]
    public void WithEventStore_PreRegisteredAutoMappedAggregateMap_Succeeds()
    {
        BsonClassMap.RegisterClassMap<RevisionRegistrationAutoMappedAggregate>(cm => cm.AutoMap());
        var builder = new MongoBuilder(new ServiceCollection());

        var act = () => builder.WithEventStore<RevisionRegistrationAutoMappedAggregate>(es => es.WithEvent<RevisionRegistrationAutoMappedEvent>());

        act.Should().NotThrow();
    }

    [Test]
    public void WithEventStore_PreRegisteredBaseMapWithoutRevision_ThrowsInvalidOperationException()
    {
        BsonClassMap.RegisterClassMap<Event<RevisionRegistrationAggregate>>(cm =>
        {
            cm.AutoMap();
            cm.UnmapMember(e => e.Revision);
        });
        var builder = new MongoBuilder(new ServiceCollection());

        var act = () => builder.WithEventStore<RevisionRegistrationAggregate>(es => es.WithEvent<RevisionRegistrationEvent>());

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*RevisionRegistrationAggregate*registered before the event store*Revision member*");
    }
}

public sealed class RevisionRegistrationAggregate : Aggregate;

public sealed class RevisionRegistrationEvent : Event<RevisionRegistrationAggregate>
{
    public override void Execute(RevisionRegistrationAggregate aggregate) { }
}

public sealed class RevisionRegistrationPlainAggregate : IAggregate
{
    public DateTime CreatedUtc { get; set; }
    public Guid Id { get; set; }
    public Int64 Revision { get; set; }
    public Int64 Version { get; set; }
}

public sealed class RevisionRegistrationPlainEvent : Event<RevisionRegistrationPlainAggregate>
{
    public override void Execute(RevisionRegistrationPlainAggregate aggregate) { }
}

public sealed class RevisionRegistrationAutoMappedAggregate : IAggregate
{
    public DateTime CreatedUtc { get; set; }
    public Guid Id { get; set; }
    public Int64 Revision { get; set; }
    public Int64 Version { get; set; }
}

public sealed class RevisionRegistrationAutoMappedEvent : Event<RevisionRegistrationAutoMappedAggregate>
{
    public override void Execute(RevisionRegistrationAutoMappedAggregate aggregate) { }
}
