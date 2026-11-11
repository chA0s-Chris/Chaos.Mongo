// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests.Queues;

using Chaos.Mongo.Queues;
using Chaos.Mongo.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
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
        // The field is still named "RetryCount" here: this is the pre-rename, pre-upgrade on-disk shape, which has
        // no "AttemptCount" element at all.
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
        result.AttemptCount.Should().Be(0);
        result.ClosedUtc.Should().BeNull();
        result.LockedUtc.Should().BeNull();
        result.DeferredUtc.Should().BeNull();
        result.Payload.Name.Should().Be("payload-value");
    }

    [Test]
    public void Deserialize_ReleasedShapeBsonDocument_RestoresValuesAndDoesNotCarryOverTheLegacyRetryCount()
    {
        // Arrange: a pre-rename, pre-upgrade document still has a "RetryCount" element, not "AttemptCount". Per
        // the migration decision, any attempts made before the upgrade are not carried over: the legacy element
        // becomes an ignored extra element, and AttemptCount starts fresh at its default of 0.
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
        result.AttemptCount.Should().Be(0, "the legacy RetryCount element must not be carried over as AttemptCount");
        result.Payload.Name.Should().Be("payload-value");
        result.PayloadType.Should().Be(typeof(TestPayload).FullName);
    }

    [Test]
    [Explicit("Runs only via process isolation from " + nameof(ToBsonDocument_CamelCaseConventionRegisteredFirst_StillPinsElementNamesToMemberNames) +
              "; registers process-global convention and class-map state.")]
    public void ToBsonDocument_CamelCaseConventionRegisteredFirst_IsolatedCase()
    {
        // Arrange: register a global camelCase convention for both MongoQueueItem and MongoQueueItem<TestPayload>
        // before anything in this fresh process has used either type, then exercise the library's normal
        // WithQueue registration path.
        ConventionRegistry.Register(
            nameof(ToBsonDocument_CamelCaseConventionRegisteredFirst_IsolatedCase),
            new ConventionPack
            {
                new CamelCaseElementNameConvention()
            },
            type => type == typeof(MongoQueueItem) || type == typeof(MongoQueueItem<TestPayload>));

        var builder = new MongoQueueBuilder<TestPayload>(new ServiceCollection());
        builder.WithPayloadHandler<TestPayloadHandler>();
        builder.RegisterQueue();

        var item = new MongoQueueItem<TestPayload>
        {
            Id = ObjectId.GenerateNewId(),
            CreatedUtc = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc),
            ClosedUtc = new DateTime(2025, 6, 1, 12, 5, 0, DateTimeKind.Utc),
            LockedUtc = new DateTime(2025, 6, 1, 12, 1, 0, DateTimeKind.Utc),
            DeferredUtc = new DateTime(2025, 6, 1, 12, 2, 0, DateTimeKind.Utc),
            Payload = new TestPayload
            {
                Name = "payload-value"
            }
        };

        // Act
        var bsonDocument = item.ToBsonDocument();

        // Assert
        bsonDocument.Names.Should().Contain([
            nameof(MongoQueueItem.ClosedUtc),
            nameof(MongoQueueItem.CreatedUtc),
            nameof(MongoQueueItem.IsClosed),
            nameof(MongoQueueItem.IsLocked),
            nameof(MongoQueueItem.IsTerminal),
            nameof(MongoQueueItem.LockedUtc),
            nameof(MongoQueueItem.DeferredUtc),
            nameof(MongoQueueItem.AttemptCount),
            nameof(MongoQueueItem<TestPayload>.Payload),
            nameof(MongoQueueItem<TestPayload>.PayloadType)
        ]);
    }

    [Test]
    public void ToBsonDocument_CamelCaseConventionRegisteredFirst_StillPinsElementNamesToMemberNames()
    {
        // Arrange & Act: the inner case runs in its own freshly started process (see
        // IsolatedConventionTestProcess), because BsonClassMap/ConventionRegistry registration is process-global
        // and irreversible, and every other test in this shared process may already have registered
        // MongoQueueItem's and MongoQueueItem<TestPayload>'s class maps under the default convention.
        var result = IsolatedConventionTestProcess.Run(
            $"{typeof(MongoQueueItemTests).FullName}.{nameof(ToBsonDocument_CamelCaseConventionRegisteredFirst_IsolatedCase)}");

        // Assert
        result.ExitCode.Should().Be(0, "the isolated camelCase case must pass; output:\n{0}\n{1}", result.StandardOutput, result.StandardError);
    }

    [Test]
    [Explicit("Runs only via process isolation from " + nameof(ToBsonDocument_DefaultConvention_PinsElementNamesToMemberNames) +
              "; registers process-global class-map state.")]
    public void ToBsonDocument_DefaultConvention_IsolatedCase()
    {
        // Arrange: explicitly exercise the library's normal registration path, the same way the camelCase
        // isolated case does, before anything in this fresh process has used MongoQueueItem or
        // MongoQueueItem<TestPayload>, so this proves the pinned names independently of whichever class map an
        // earlier test in a shared process may have already registered.
        var builder = new MongoQueueBuilder<TestPayload>(new ServiceCollection());
        builder.WithPayloadHandler<TestPayloadHandler>();
        builder.RegisterQueue();

        var item = new MongoQueueItem<TestPayload>
        {
            Id = ObjectId.GenerateNewId(),
            CreatedUtc = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc),
            ClosedUtc = new DateTime(2025, 6, 1, 12, 5, 0, DateTimeKind.Utc),
            LockedUtc = new DateTime(2025, 6, 1, 12, 1, 0, DateTimeKind.Utc),
            DeferredUtc = new DateTime(2025, 6, 1, 12, 2, 0, DateTimeKind.Utc),
            Payload = new TestPayload
            {
                Name = "payload-value"
            }
        };

        // Act
        var bsonDocument = item.ToBsonDocument();

        // Assert
        bsonDocument.Names.Should().Contain([
            nameof(MongoQueueItem.ClosedUtc),
            nameof(MongoQueueItem.CreatedUtc),
            nameof(MongoQueueItem.IsClosed),
            nameof(MongoQueueItem.IsLocked),
            nameof(MongoQueueItem.IsTerminal),
            nameof(MongoQueueItem.LockedUtc),
            nameof(MongoQueueItem.DeferredUtc),
            nameof(MongoQueueItem.AttemptCount),
            nameof(MongoQueueItem<TestPayload>.Payload),
            nameof(MongoQueueItem<TestPayload>.PayloadType)
        ]);
    }

    [Test]
    public void ToBsonDocument_DefaultConvention_PinsElementNamesToMemberNames()
    {
        // Arrange & Act: the inner case runs in its own freshly started process (see
        // IsolatedConventionTestProcess), because BsonClassMap/ConventionRegistry registration is process-global
        // and irreversible, and every other test in this shared process may already have registered
        // MongoQueueItem's and MongoQueueItem<TestPayload>'s class maps under the default convention.
        var result = IsolatedConventionTestProcess.Run(
            $"{typeof(MongoQueueItemTests).FullName}.{nameof(ToBsonDocument_DefaultConvention_IsolatedCase)}");

        // Assert
        result.ExitCode.Should().Be(0, "the isolated default-convention case must pass; output:\n{0}\n{1}", result.StandardOutput, result.StandardError);
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

    public class TestPayloadHandler : IMongoQueuePayloadHandler<TestPayload>
    {
        public Task HandlePayloadAsync(TestPayload payload, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
