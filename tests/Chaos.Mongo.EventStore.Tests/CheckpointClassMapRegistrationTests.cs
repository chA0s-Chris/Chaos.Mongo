// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson.Serialization;
using NUnit.Framework;

/// <summary>
/// Class-map registration is process-global and irreversible, so every test uses aggregate and event types that no
/// other test registers.
/// </summary>
public class CheckpointClassMapRegistrationTests
{
    [Test]
    public void WithEventStore_ConsumerCheckpointClassMapMappingChainHash_ThrowsUnderProtection()
    {
        BsonClassMap.RegisterClassMap<CheckpointDocument<ChainHashCheckpointAggregate>>(cm =>
        {
            cm.AutoMap();
            cm.MapMember(c => c.ChainHash).SetElementName(CheckpointDocument<ChainHashCheckpointAggregate>.ChainHashElementName);
        });

        var act = () => new MongoBuilder(new ServiceCollection()).WithEventStore<ChainHashCheckpointAggregate>(es => es.WithEvent<ChainHashCheckpointEvent>()
                                                                                                                       .WithIntegrityProtection());

        act.Should().Throw<InvalidOperationException>().WithMessage("*registered before the event store*");
    }

    [Test]
    public void WithEventStore_ConsumerCheckpointClassMapUnderProtection_ThrowsAndRegistersNothing()
    {
        BsonClassMap.RegisterClassMap<CheckpointDocument<ConsumerCheckpointAggregate>>();

        var act = () => new MongoBuilder(new ServiceCollection()).WithEventStore<ConsumerCheckpointAggregate>(es => es.WithEvent<ConsumerCheckpointEvent>()
                                                                                                                      .WithIntegrityProtection());

        act.Should().Throw<InvalidOperationException>().WithMessage("*CheckpointDocument*registered before the event store*");
        BsonClassMap.IsClassMapRegistered(typeof(ConsumerCheckpointAggregate)).Should().BeFalse();
        BsonClassMap.IsClassMapRegistered(typeof(ConsumerCheckpointEvent)).Should().BeFalse();
    }

    [Test]
    public void WithEventStore_ConsumerCheckpointClassMapWithoutProtection_IsAccepted()
    {
        BsonClassMap.RegisterClassMap<CheckpointDocument<UnprotectedCheckpointAggregate>>();

        var act = () => new MongoBuilder(new ServiceCollection()).WithEventStore<UnprotectedCheckpointAggregate>(es => es.WithEvent<UnprotectedCheckpointEvent>());

        act.Should().NotThrow();
    }

    [Test]
    public void WithEventStore_OwnCheckpointClassMapOfEarlierRegistration_IsReusedUnderProtection()
    {
        new MongoBuilder(new ServiceCollection()).WithEventStore<ReusedCheckpointAggregate>(es => es.WithEvent<ReusedCheckpointEvent>());

        var act = () => new MongoBuilder(new ServiceCollection()).WithEventStore<ReusedCheckpointAggregate>(es => es.WithEvent<ReusedCheckpointEvent>()
                                                                                                                    .WithIntegrityProtection());

        act.Should().NotThrow();
        BsonClassMap.LookupClassMap(typeof(CheckpointDocument<ReusedCheckpointAggregate>)).AllMemberMaps
                    .Should().Contain(m => m.ElementName == CheckpointDocument<ReusedCheckpointAggregate>.ChainHashElementName);
    }
}

public sealed class ConsumerCheckpointAggregate : Aggregate;

public sealed class ConsumerCheckpointEvent : Event<ConsumerCheckpointAggregate>
{
    public override void Execute(ConsumerCheckpointAggregate aggregate) { }
}

public sealed class UnprotectedCheckpointAggregate : Aggregate;

public sealed class UnprotectedCheckpointEvent : Event<UnprotectedCheckpointAggregate>
{
    public override void Execute(UnprotectedCheckpointAggregate aggregate) { }
}

public sealed class ReusedCheckpointAggregate : Aggregate;

public sealed class ReusedCheckpointEvent : Event<ReusedCheckpointAggregate>
{
    public override void Execute(ReusedCheckpointAggregate aggregate) { }
}

public sealed class ChainHashCheckpointAggregate : Aggregate;

public sealed class ChainHashCheckpointEvent : Event<ChainHashCheckpointAggregate>
{
    public override void Execute(ChainHashCheckpointAggregate aggregate) { }
}
