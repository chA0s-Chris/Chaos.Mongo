// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity;

using Chaos.Mongo.EventStore.Integrity;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using NUnit.Framework;

/// <remarks>
/// Class-map registration is process-global and irreversible, so every test uses dedicated aggregate and event types
/// that no other test registers. Each test deserializes an actual document carrying an extra, unrecognized element
/// through <see cref="BsonSerializer"/>, rather than only asserting the <c>IgnoreExtraElements</c> flag, so a
/// regression in the serializer itself, not only in the class map configuration, would be caught.
/// </remarks>
public class ExtraElementToleranceTests
{
    [Test]
    public void AggregateReadModel_DocumentWithExtraElement_StillDeserializes()
    {
        new MongoBuilder(new ServiceCollection()).WithEventStore<ExtraElementAggregate>(es => es.WithEvent<ExtraElementEvent>());
        var aggregateId = Guid.NewGuid();
        var createdUtc = new DateTime(2025, 2, 1, 3, 4, 5, 678, DateTimeKind.Utc);
        var document = new ExtraElementAggregate
        {
            Id = aggregateId,
            Version = 3,
            Revision = 3,
            CreatedUtc = createdUtc
        }.ToBsonDocument();
        document["UnknownField"] = "surprise";

        var deserialized = BsonSerializer.Deserialize<ExtraElementAggregate>(document);

        deserialized.Id.Should().Be(aggregateId);
        deserialized.Version.Should().Be(3);
        deserialized.Revision.Should().Be(3);
        deserialized.CreatedUtc.Should().Be(createdUtc);
    }

    [Test]
    public void CheckpointDocument_DocumentWithExtraElement_StillDeserializes()
    {
        new MongoBuilder(new ServiceCollection()).WithEventStore<ExtraElementCheckpointAggregate>(es => es.WithEvent<ExtraElementCheckpointEvent>()
                                                                                                          .WithCheckpoints(1));
        var aggregateId = Guid.NewGuid();
        var stateCreatedUtc = new DateTime(2025, 2, 2, 4, 5, 6, 789, DateTimeKind.Utc);
        var document = new CheckpointDocument<ExtraElementCheckpointAggregate>
        {
            Id = new CheckpointId(aggregateId, 5),
            Revision = 5,
            State = new ExtraElementCheckpointAggregate
            {
                Id = aggregateId,
                Version = 5,
                Revision = 4,
                CreatedUtc = stateCreatedUtc
            }
        }.ToBsonDocument();
        document["UnknownField"] = "surprise";

        var deserialized = BsonSerializer.Deserialize<CheckpointDocument<ExtraElementCheckpointAggregate>>(document);

        deserialized.Id.AggregateId.Should().Be(aggregateId);
        deserialized.Id.Version.Should().Be(5);
        deserialized.Revision.Should().Be(5);
        deserialized.State.Id.Should().Be(aggregateId);
        deserialized.State.Version.Should().Be(5);
        deserialized.State.Revision.Should().Be(4);
        deserialized.State.CreatedUtc.Should().Be(stateCreatedUtc);
    }

    [Test]
    public void DirectAggregateReadModel_DocumentWithExtraElement_StillDeserializes()
    {
        new MongoBuilder(new ServiceCollection()).WithEventStore<ExtraElementDirectAggregate>(es => es.WithEvent<ExtraElementDirectEvent>());
        var aggregateId = Guid.NewGuid();
        var createdUtc = new DateTime(2025, 2, 3, 5, 6, 7, 890, DateTimeKind.Utc);
        var document = new ExtraElementDirectAggregate
        {
            Id = aggregateId,
            Version = 2,
            Revision = 2,
            CreatedUtc = createdUtc
        }.ToBsonDocument();
        document["UnknownField"] = "surprise";

        var deserialized = BsonSerializer.Deserialize<ExtraElementDirectAggregate>(document);

        deserialized.Id.Should().Be(aggregateId);
        deserialized.Version.Should().Be(2);
        deserialized.Revision.Should().Be(2);
        deserialized.CreatedUtc.Should().Be(createdUtc);
    }

    [Test]
    public void EventClassMap_DocumentWithExtraElement_StillDeserializes()
    {
        new MongoBuilder(new ServiceCollection()).WithEventStore<ExtraElementEventMapAggregate>(es => es.WithEvent<ExtraElementEventMapEvent>());
        var aggregateId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var createdUtc = new DateTime(2025, 2, 4, 6, 7, 8, 901, DateTimeKind.Utc);
        var document = new ExtraElementEventMapEvent
        {
            Id = eventId,
            AggregateId = aggregateId,
            AggregateType = "ExtraElementEventMapAggregate",
            Version = 1,
            Revision = 2,
            CreatedUtc = createdUtc
        }.ToBsonDocument();
        document["UnknownField"] = "surprise";

        var deserialized = BsonSerializer.Deserialize<Event<ExtraElementEventMapAggregate>>(document);

        deserialized.AggregateId.Should().Be(aggregateId);
        deserialized.AggregateType.Should().Be("ExtraElementEventMapAggregate");
        deserialized.Version.Should().Be(1);
        deserialized.Revision.Should().Be(2);
        deserialized.Id.Should().Be(eventId);
        deserialized.CreatedUtc.Should().Be(createdUtc);
    }

    [Test]
    public void EventIntegrityClassMap_DocumentWithExtraElement_StillDeserializes()
    {
        new MongoBuilder(new ServiceCollection()).WithEventStore<ExtraElementIntegrityAggregate>(es => es.WithEvent<ExtraElementIntegrityEvent>());
        var previousHash = Enumerable.Range(0, 32).Select(i => (Byte)i).ToArray();
        var sealedIntegrity = EventIntegrityChain.Seal([1, 2, 3], previousHash, IntegritySealMode.Append);
        var document = sealedIntegrity.ToBsonDocument();
        document["UnknownField"] = "surprise";

        var deserialized = BsonSerializer.Deserialize<EventIntegrity>(document);

        deserialized.SealMode.Should().Be(IntegritySealMode.Append);
        deserialized.Algorithm.Should().Be(sealedIntegrity.Algorithm);
        deserialized.FormatVersion.Should().Be(sealedIntegrity.FormatVersion);
        deserialized.Hash.Should().Equal(sealedIntegrity.Hash);
        deserialized.PreviousHash.Should().Equal(previousHash);
        deserialized.PersonalData.Should().BeNull();
    }

    [Test]
    public void IntegritySealingStateClassMap_DocumentWithExtraElement_StillDeserializes()
    {
        new MongoBuilder(new ServiceCollection()).WithEventStore<ExtraElementSealingStateAggregate>(es => es.WithEvent<ExtraElementSealingStateEvent>());
        var cursor = Guid.NewGuid();
        var lastSealedStream = Guid.NewGuid();
        var passStartedUtc = new DateTime(2025, 2, 5, 7, 8, 9, 12, DateTimeKind.Utc);
        var passCompletedUtc = new DateTime(2025, 2, 5, 7, 9, 10, 34, DateTimeKind.Utc);
        var state = new IntegritySealingState
        {
            Cursor = cursor,
            LastSealedStream = lastSealedStream,
            PassStartedUtc = passStartedUtc,
            PassCompletedUtc = passCompletedUtc,
            StreamsChecked = 7,
            StreamsSealed = 5,
            StreamsFailed = 2,
            EventsSealed = 11
        };
        var document = state.ToBsonDocument();
        document["UnknownField"] = "surprise";

        var deserialized = BsonSerializer.Deserialize<IntegritySealingState>(document);

        deserialized.Id.Should().Be(state.Id);
        deserialized.Cursor.Should().Be(cursor);
        deserialized.LastSealedStream.Should().Be(lastSealedStream);
        deserialized.PassStartedUtc.Should().Be(passStartedUtc);
        deserialized.PassCompletedUtc.Should().Be(passCompletedUtc);
        deserialized.StreamsChecked.Should().Be(7);
        deserialized.StreamsSealed.Should().Be(5);
        deserialized.StreamsFailed.Should().Be(2);
        deserialized.EventsSealed.Should().Be(11);
    }

    [Test]
    public void PersonalDataCommitmentClassMap_DocumentWithExtraElement_StillDeserializes()
    {
        new MongoBuilder(new ServiceCollection()).WithEventStore<ExtraElementCommitmentAggregate>(es => es.WithEvent<ExtraElementCommitmentEvent>());
        var salt = Enumerable.Range(0, 32).Select(i => (Byte)i).ToArray();
        var commitment = Enumerable.Range(32, 32).Select(i => (Byte)i).ToArray();
        var document = new PersonalDataCommitment
        {
            Path = "Name",
            Salt = salt,
            Commitment = commitment
        }.ToBsonDocument();
        document["UnknownField"] = "surprise";

        var deserialized = BsonSerializer.Deserialize<PersonalDataCommitment>(document);

        deserialized.Path.Should().Be("Name");
        deserialized.Salt.Should().Equal(salt);
        deserialized.Commitment.Should().Equal(commitment);
    }

    [Test]
    public void PersonalDataErasureEntryClassMap_DocumentWithExtraElement_StillDeserializes()
    {
        new MongoBuilder(new ServiceCollection()).WithEventStore<ExtraElementErasureAggregate>(es => es.WithEvent<ExtraElementErasureEvent>());
        var document = new PersonalDataErasureEntry
        {
            Version = 4,
            Paths = ["Name"]
        }.ToBsonDocument();
        document["UnknownField"] = "surprise";

        var deserialized = BsonSerializer.Deserialize<PersonalDataErasureEntry>(document);

        deserialized.Version.Should().Be(4);
        deserialized.Paths.Should().Equal("Name");
    }
}

public sealed class ExtraElementAggregate : Aggregate;

public sealed class ExtraElementEvent : Event<ExtraElementAggregate>
{
    public override void Execute(ExtraElementAggregate aggregate) { }
}

public sealed class ExtraElementCheckpointAggregate : Aggregate;

public sealed class ExtraElementCheckpointEvent : Event<ExtraElementCheckpointAggregate>
{
    public override void Execute(ExtraElementCheckpointAggregate aggregate) { }
}

public sealed class ExtraElementDirectAggregate : IAggregate
{
    public DateTime CreatedUtc { get; set; }
    public Guid Id { get; set; }
    public Int64 Revision { get; set; }
    public Int64 Version { get; set; }
}

public sealed class ExtraElementDirectEvent : Event<ExtraElementDirectAggregate>
{
    public override void Execute(ExtraElementDirectAggregate aggregate) { }
}

public sealed class ExtraElementEventMapAggregate : Aggregate;

public sealed class ExtraElementEventMapEvent : Event<ExtraElementEventMapAggregate>
{
    public override void Execute(ExtraElementEventMapAggregate aggregate) { }
}

public sealed class ExtraElementIntegrityAggregate : Aggregate;

public sealed class ExtraElementIntegrityEvent : Event<ExtraElementIntegrityAggregate>
{
    public override void Execute(ExtraElementIntegrityAggregate aggregate) { }
}

public sealed class ExtraElementSealingStateAggregate : Aggregate;

public sealed class ExtraElementSealingStateEvent : Event<ExtraElementSealingStateAggregate>
{
    public override void Execute(ExtraElementSealingStateAggregate aggregate) { }
}

public sealed class ExtraElementCommitmentAggregate : Aggregate;

public sealed class ExtraElementCommitmentEvent : Event<ExtraElementCommitmentAggregate>
{
    public override void Execute(ExtraElementCommitmentAggregate aggregate) { }
}

public sealed class ExtraElementErasureAggregate : Aggregate;

public sealed class ExtraElementErasureEvent : Event<ExtraElementErasureAggregate>
{
    public override void Execute(ExtraElementErasureAggregate aggregate) { }
}
