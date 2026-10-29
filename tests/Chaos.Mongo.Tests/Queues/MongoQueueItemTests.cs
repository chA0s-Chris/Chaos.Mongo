// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests.Queues;

using Chaos.Mongo.Queues;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using NUnit.Framework;

public class MongoQueueItemTests
{
    [Test]
    public void Deserialize_DocumentWithUnrecognizedElement_DeserializesSuccessfully()
    {
        // Arrange
        var document = CreateReleasedShapeDocument();
        document.Add("FutureField", "unrecognized");

        // Act
        var act = () => BsonSerializer.Deserialize<MongoQueueItem<TestPayload>>(document);

        // Assert
        act.Should().NotThrow();
    }

    [Test]
    public void Deserialize_ReleasedShapeBsonDocumentWithoutOptionalTimestamps_RestoresValuesAndDefaultsTimestamps()
    {
        // Arrange: the exact field names/types written by every shipped version of this type, omitting the
        // optional ClosedUtc/LockedUtc elements exactly as a 0.x writer for a never-locked, never-closed item would.
        var createdUtc = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var id = ObjectId.GenerateNewId();
        var document = new BsonDocument
        {
            { "_id", id },
            { "CreatedUtc", createdUtc },
            { "IsClosed", false },
            { "IsLocked", false },
            { "IsTerminal", false },
            { "RetryCount", 0 },
            { "Payload", new BsonDocument("Name", "payload-value") },
            { "PayloadType", typeof(TestPayload).FullName }
        };

        // Act
        var result = BsonSerializer.Deserialize<MongoQueueItem<TestPayload>>(document);

        // Assert
        result.Id.Should().Be(id);
        result.CreatedUtc.Should().Be(createdUtc);
        result.IsClosed.Should().BeFalse();
        result.IsLocked.Should().BeFalse();
        result.IsTerminal.Should().BeFalse();
        result.RetryCount.Should().Be(0);
        result.ClosedUtc.Should().BeNull();
        result.LockedUtc.Should().BeNull();
        result.Payload.Name.Should().Be("payload-value");
    }

    [Test]
    public void Deserialize_ReleasedShapeBsonDocument_RestoresValues()
    {
        // Arrange
        var document = CreateReleasedShapeDocument();

        // Act
        var result = BsonSerializer.Deserialize<MongoQueueItem<TestPayload>>(document);

        // Assert
        result.Id.Should().Be(document["_id"].AsObjectId);
        result.CreatedUtc.Should().Be(document["CreatedUtc"].ToUniversalTime());
        result.ClosedUtc.Should().Be(document["ClosedUtc"].ToUniversalTime());
        result.IsClosed.Should().BeTrue();
        result.IsLocked.Should().BeTrue();
        result.IsTerminal.Should().BeTrue();
        result.LockedUtc.Should().Be(document["LockedUtc"].ToUniversalTime());
        result.RetryCount.Should().Be(3);
        result.Payload.Name.Should().Be("payload-value");
        result.PayloadType.Should().Be(typeof(TestPayload).FullName);
    }

    private static BsonDocument CreateReleasedShapeDocument()
        => new()
        {
            { "_id", ObjectId.GenerateNewId() },
            { "CreatedUtc", new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc) },
            { "ClosedUtc", new DateTime(2025, 6, 1, 12, 5, 0, DateTimeKind.Utc) },
            { "IsClosed", true },
            { "IsLocked", true },
            { "IsTerminal", true },
            { "LockedUtc", new DateTime(2025, 6, 1, 12, 1, 0, DateTimeKind.Utc) },
            { "RetryCount", 3 },
            { "Payload", new BsonDocument("Name", "payload-value") },
            { "PayloadType", typeof(TestPayload).FullName }
        };

    public class TestPayload
    {
        public String? Name { get; set; }
    }
}
