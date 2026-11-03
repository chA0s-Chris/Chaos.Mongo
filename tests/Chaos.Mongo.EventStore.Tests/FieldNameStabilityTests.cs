// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests;

using Chaos.Mongo.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Conventions;
using NUnit.Framework;

/// <summary>
/// Verifies that <see cref="Aggregate"/>, <see cref="Event{TAggregate}"/>, <see cref="CheckpointDocument{TAggregate}"/>,
/// and <see cref="CheckpointId"/> serialize to the same BSON element names whether or not a global camelCase
/// convention pack is registered.
/// </summary>
public class FieldNameStabilityTests
{
    [Test]
    [Explicit("Runs only via process isolation from " + nameof(ToBsonDocument_CamelCaseConventionRegisteredFirst_StillPinsElementNamesToMemberNames) +
              "; registers process-global convention and class-map state.")]
    public void ToBsonDocument_CamelCaseConventionRegisteredFirst_IsolatedCase()
    {
        // Arrange: register a global camelCase convention for the aggregate, event, checkpoint document, and
        // checkpoint id types before anything in this fresh process has used any of them, then exercise the
        // library's normal WithEventStore registration path.
        ConventionRegistry.Register(
            nameof(ToBsonDocument_CamelCaseConventionRegisteredFirst_IsolatedCase),
            new ConventionPack
            {
                new CamelCaseElementNameConvention()
            },
            // Aggregate itself must be included: CreatedUtc, Revision, and Version are declared and pinned on
            // Aggregate's own class map, not on the derived FieldStabilityAggregate, so a predicate that only
            // covers the derived type would never actually exercise their pinning.
            type => type == typeof(Aggregate) ||
                    type == typeof(FieldStabilityAggregate) ||
                    type == typeof(Event<FieldStabilityAggregate>) ||
                    type == typeof(CheckpointDocument<FieldStabilityAggregate>) ||
                    type == typeof(CheckpointId));

        new MongoBuilder(new ServiceCollection()).WithEventStore<FieldStabilityAggregate>(es => es.WithEvent<FieldStabilityEvent>());

        AssertPinnedElementNames<FieldStabilityAggregate, FieldStabilityEvent>();
    }

    [Test]
    public void ToBsonDocument_CamelCaseConventionRegisteredFirst_StillPinsElementNamesToMemberNames()
    {
        // Arrange & Act: the inner case runs in its own freshly started process (see
        // IsolatedConventionTestProcess), because BsonClassMap/ConventionRegistry registration is process-global
        // and irreversible, and every other test in this shared process may already have registered these
        // types' class maps under the default convention.
        var result = IsolatedConventionTestProcess.Run(
            $"{typeof(FieldNameStabilityTests).FullName}.{nameof(ToBsonDocument_CamelCaseConventionRegisteredFirst_IsolatedCase)}");

        // Assert
        result.ExitCode.Should().Be(0, "the isolated camelCase case must pass; output:\n{0}\n{1}", result.StandardOutput, result.StandardError);
    }

    [Test]
    [Explicit("Runs only via process isolation from " + nameof(ToBsonDocument_DefaultConvention_PinsElementNamesToMemberNames) +
              "; registers process-global class-map state.")]
    public void ToBsonDocument_DefaultConvention_IsolatedCase()
    {
        // Arrange & Act: exercise the library's normal WithEventStore registration path before anything in this
        // fresh process has used these types, the same as the camelCase isolated case, but without registering
        // any convention, so this proves the pinned names independently of whichever class map an earlier test
        // in a shared process may have already registered.
        new MongoBuilder(new ServiceCollection()).WithEventStore<DefaultConventionAggregate>(es => es.WithEvent<DefaultConventionEvent>());

        AssertPinnedElementNames<DefaultConventionAggregate, DefaultConventionEvent>();
    }

    [Test]
    public void ToBsonDocument_DefaultConvention_PinsElementNamesToMemberNames()
    {
        // Arrange & Act: the inner case runs in its own freshly started process (see
        // IsolatedConventionTestProcess), because BsonClassMap/ConventionRegistry registration is process-global
        // and irreversible, and every other test in this shared process may already have registered these
        // types' class maps under the default convention.
        var result = IsolatedConventionTestProcess.Run(
            $"{typeof(FieldNameStabilityTests).FullName}.{nameof(ToBsonDocument_DefaultConvention_IsolatedCase)}");

        // Assert
        result.ExitCode.Should().Be(0, "the isolated default-convention case must pass; output:\n{0}\n{1}", result.StandardOutput, result.StandardError);
    }

    private static void AssertPinnedElementNames<TAggregate, TEvent>()
        where TAggregate : Aggregate, new()
        where TEvent : Event<TAggregate>, new()
    {
        var aggregate = new TAggregate
        {
            Id = Guid.NewGuid(),
            CreatedUtc = DateTime.UtcNow,
            Revision = 1,
            Version = 1
        };
        var aggregateDocument = aggregate.ToBsonDocument();
        aggregateDocument.Names.Should().Contain([nameof(Aggregate.CreatedUtc), nameof(Aggregate.Revision), nameof(Aggregate.Version)]);

        var @event = new TEvent
        {
            Id = Guid.NewGuid(),
            AggregateId = aggregate.Id,
            AggregateType = typeof(TAggregate).Name,
            CreatedUtc = DateTime.UtcNow,
            Revision = 1,
            Version = 1
        };
        var eventDocument = @event.ToBsonDocument(typeof(Event<TAggregate>));
        eventDocument.Names.Should().Contain([
            nameof(Event<TAggregate>.AggregateId),
            nameof(Event<TAggregate>.AggregateType),
            nameof(Event<TAggregate>.CreatedUtc),
            nameof(Event<TAggregate>.Revision),
            nameof(Event<TAggregate>.Version)
        ]);

        var checkpointId = new CheckpointId(aggregate.Id, 1);
        var checkpointIdDocument = checkpointId.ToBsonDocument();
        checkpointIdDocument.Names.Should().Contain([nameof(CheckpointId.AggregateId), nameof(CheckpointId.Version)]);

        var checkpoint = new CheckpointDocument<TAggregate>
        {
            Id = checkpointId,
            Revision = 1,
            State = aggregate
        };
        var checkpointDocument = checkpoint.ToBsonDocument();
        checkpointDocument.Names.Should().Contain([nameof(CheckpointDocument<TAggregate>.Revision), nameof(CheckpointDocument<TAggregate>.State)]);
    }
}

public sealed class FieldStabilityAggregate : Aggregate;

public sealed class FieldStabilityEvent : Event<FieldStabilityAggregate>
{
    public override void Execute(FieldStabilityAggregate aggregate) { }
}

public sealed class DefaultConventionAggregate : Aggregate;

public sealed class DefaultConventionEvent : Event<DefaultConventionAggregate>
{
    public override void Execute(DefaultConventionAggregate aggregate) { }
}
