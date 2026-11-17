// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox.Tests;

using FluentAssertions;
using MongoDB.Bson;
using NUnit.Framework;

public class OutboxMessageTests
{
    [Test]
    public void Create_AllValuesGiven_SetsThem()
    {
        var payload = new BsonDocument("Name", "Order");
        var id = ObjectId.GenerateNewId();
        var createdUtc = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

        var message = OutboxMessage.Create("OrderCreated", payload, "corr-1", id, createdUtc, attemptCount: 3);

        message.Type.Should().Be("OrderCreated");
        message.Payload.Should().BeSameAs(payload);
        message.CorrelationId.Should().Be("corr-1");
        message.Id.Should().Be(id);
        message.CreatedUtc.Should().Be(createdUtc);
        message.AttemptCount.Should().Be(3);
        message.IsLocked.Should().BeTrue();
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void Create_AttemptCountBelowOne_ThrowsArgumentOutOfRangeException(Int64 attemptCount)
    {
        var act = () => OutboxMessage.Create("OrderCreated", [], attemptCount: attemptCount);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("attemptCount");
    }

    [TestCase("")]
    [TestCase("   ")]
    public void Create_EmptyOrWhitespaceType_ThrowsArgumentException(String type)
    {
        var act = () => OutboxMessage.Create(type, []);

        act.Should().Throw<ArgumentException>().WithParameterName("type");
    }

    [TestCase(DateTimeKind.Local)]
    [TestCase(DateTimeKind.Unspecified)]
    public void Create_NonUtcCreationTime_ThrowsArgumentException(DateTimeKind kind)
    {
        var createdUtc = new DateTime(2026, 3, 4, 5, 6, 7, kind);

        var act = () => OutboxMessage.Create("OrderCreated", [], createdUtc: createdUtc);

        act.Should().Throw<ArgumentException>().WithParameterName("createdUtc");
    }

    [Test]
    public void Create_NullPayload_ThrowsArgumentNullException()
    {
        var act = () => OutboxMessage.Create("OrderCreated", null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("payload");
    }

    [Test]
    public void Create_NullType_ThrowsArgumentNullException()
    {
        var act = () => OutboxMessage.Create(null!, []);

        act.Should().Throw<ArgumentNullException>().WithParameterName("type");
    }

    [Test]
    public void Create_OnlyTypeAndPayload_ReturnsAMessageClaimedForItsFirstDeliveryAttempt()
    {
        var before = DateTime.UtcNow;

        var message = OutboxMessage.Create("OrderCreated", []);
        var other = OutboxMessage.Create("OrderCreated", []);

        message.Id.Should().NotBe(ObjectId.Empty);
        message.CreatedUtc.Kind.Should().Be(DateTimeKind.Utc);
        message.CreatedUtc.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTime.UtcNow);
        message.AttemptCount.Should().Be(1);
        message.CorrelationId.Should().BeNull();
        message.State.Should().Be(OutboxMessageState.Pending);
        message.IsLocked.Should().BeTrue();
        message.LockId.Should().NotBeNullOrWhiteSpace().And.NotBe(other.LockId);
        message.LockedUtc.Should().NotBeNull();
        message.LockedUtc!.Value.Kind.Should().Be(DateTimeKind.Utc);
        message.LockedUtc.Value.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTime.UtcNow);
        message.NextAttemptUtc.Should().BeNull();
        message.ProcessedUtc.Should().BeNull();
        message.FailedUtc.Should().BeNull();
        message.Error.Should().BeNull();
    }

    [Test]
    public async Task Create_SerializedPayload_ReachesAPublisherAndDeserializesToAnEqualValue()
    {
        var payload = new OutboxMessageTestPayload("Record", 42);
        var publisher = new TestOutboxPublisher();

        await publisher.PublishAsync(OutboxMessage.Create("RecordPayload", payload.ToBsonDocument()));

        publisher.PublishedMessages.Should().ContainSingle()
                 .Which.DeserializePayload<OutboxMessageTestPayload>().Should().Be(payload);
    }
}

/// <summary>
/// A payload that only <see cref="OutboxMessageTests"/> serializes, so its process-global class map is not registered
/// by another fixture at the same time.
/// </summary>
public sealed record OutboxMessageTestPayload(String Name, Int32 Value);
