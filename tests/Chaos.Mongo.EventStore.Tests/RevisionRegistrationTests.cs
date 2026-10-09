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
