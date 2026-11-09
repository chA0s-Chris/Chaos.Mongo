// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox.Tests;

using Chaos.Mongo.TestSupport;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using NUnit.Framework;

public class OutboxSerializationSetupTests
{
    [Test]
    public void OutboxMessage_DocumentWithUnknownExtraElement_DeserializesSuccessfully()
    {
        var messageTypes = new Dictionary<Type, String>
        {
            [typeof(TestPayload)] = "TestPayload"
        };
        OutboxSerializationSetup.RegisterClassMaps(messageTypes);

        var id = ObjectId.GenerateNewId();
        var createdUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var document = new BsonDocument
        {
            ["_id"] = id,
            ["Type"] = "TestPayload",
            ["Payload"] = new BsonDocument
            {
                ["Name"] = "Order",
                ["Value"] = 1
            },
            ["CreatedUtc"] = createdUtc,
            ["State"] = (Int32)OutboxMessageState.Pending,
            ["IsLocked"] = false,
            // "RetryCount" is the pre-rename, pre-upgrade field name: it is now an ignored extra element, not
            // carried over into AttemptCount.
            ["RetryCount"] = 0,
            ["UnrecognizedField"] = "a field no version of OutboxMessage declares"
        };

        var message = BsonSerializer.Deserialize<OutboxMessage>(document);

        message.Id.Should().Be(id);
        message.Type.Should().Be("TestPayload");
        message.CreatedUtc.Should().Be(createdUtc);
        message.State.Should().Be(OutboxMessageState.Pending);
        message.AttemptCount.Should().Be(0, "the legacy RetryCount element must not be carried over as AttemptCount");
        message.DeserializePayload<TestPayload>().Name.Should().Be("Order");
    }

    [Test]
    public void RegisterClassMaps_CalledTwice_DoesNotThrow()
    {
        var messageTypes = new Dictionary<Type, String>
        {
            [typeof(TestPayload)] = "TestPayload"
        };

        OutboxSerializationSetup.RegisterClassMaps(messageTypes);

        var act = () => OutboxSerializationSetup.RegisterClassMaps(messageTypes);

        act.Should().NotThrow();
    }

    [Test]
    public void RegisterClassMaps_RegistersOutboxMessageClassMap()
    {
        var messageTypes = new Dictionary<Type, String>
        {
            [typeof(TestPayload)] = "TestPayload"
        };

        OutboxSerializationSetup.RegisterClassMaps(messageTypes);

        BsonClassMap.IsClassMapRegistered(typeof(OutboxMessage)).Should().BeTrue();
    }

    [Test]
    public void RegisterClassMaps_RegistersPayloadTypeClassMaps()
    {
        var messageTypes = new Dictionary<Type, String>
        {
            [typeof(TestPayload)] = "TestPayload",
            [typeof(AnotherTestPayload)] = "AnotherPayload"
        };

        OutboxSerializationSetup.RegisterClassMaps(messageTypes);

        BsonClassMap.IsClassMapRegistered(typeof(TestPayload)).Should().BeTrue();
        BsonClassMap.IsClassMapRegistered(typeof(AnotherTestPayload)).Should().BeTrue();
    }

    [Test]
    [Explicit("Runs only via process isolation from " + nameof(ToBsonDocument_CamelCaseConventionRegisteredFirst_StillPinsElementNamesToMemberNames) +
              "; registers process-global convention and class-map state.")]
    public void ToBsonDocument_CamelCaseConventionRegisteredFirst_IsolatedCase()
    {
        // Arrange: register a global camelCase convention for OutboxMessage before anything in this fresh
        // process has used the type, then exercise the library's normal registration path.
        ConventionRegistry.Register(
            nameof(ToBsonDocument_CamelCaseConventionRegisteredFirst_IsolatedCase),
            new ConventionPack
            {
                new CamelCaseElementNameConvention()
            },
            type => type == typeof(OutboxMessage));
        OutboxSerializationSetup.RegisterClassMaps(new Dictionary<Type, String>
        {
            [typeof(TestPayload)] = "TestPayload"
        });

        var message = CreateFullyPopulatedMessage();

        // Act
        var bsonDocument = message.ToBsonDocument();

        // Assert
        AssertPinnedElementNames(bsonDocument);
    }

    [Test]
    public void ToBsonDocument_CamelCaseConventionRegisteredFirst_StillPinsElementNamesToMemberNames()
    {
        // Arrange & Act: the inner case runs in its own freshly started process (see
        // IsolatedConventionTestProcess), because BsonClassMap/ConventionRegistry registration is process-global
        // and irreversible, and every other test in this shared process may already have registered
        // OutboxMessage's class map under the default convention.
        var result = IsolatedConventionTestProcess.Run(
            $"{typeof(OutboxSerializationSetupTests).FullName}.{nameof(ToBsonDocument_CamelCaseConventionRegisteredFirst_IsolatedCase)}");

        // Assert
        result.ExitCode.Should().Be(0, "the isolated camelCase case must pass; output:\n{0}\n{1}", result.StandardOutput, result.StandardError);
    }

    [Test]
    [Explicit("Runs only via process isolation from " + nameof(ToBsonDocument_DefaultConvention_PinsElementNamesToMemberNames) +
              "; registers process-global class-map state.")]
    public void ToBsonDocument_DefaultConvention_IsolatedCase()
    {
        // Arrange: exercise the library's normal registration path before anything in this fresh process has
        // used OutboxMessage, the same as the camelCase isolated case, but without registering any convention,
        // so this proves the pinned names independently of whichever class map an earlier test in a shared
        // process may have already registered.
        OutboxSerializationSetup.RegisterClassMaps(new Dictionary<Type, String>
        {
            [typeof(TestPayload)] = "TestPayload"
        });
        var message = CreateFullyPopulatedMessage();

        // Act
        var bsonDocument = message.ToBsonDocument();

        // Assert
        AssertPinnedElementNames(bsonDocument);
    }

    [Test]
    public void ToBsonDocument_DefaultConvention_PinsElementNamesToMemberNames()
    {
        // Arrange & Act: the inner case runs in its own freshly started process (see
        // IsolatedConventionTestProcess), because BsonClassMap/ConventionRegistry registration is process-global
        // and irreversible, and every other test in this shared process may already have registered
        // OutboxMessage's class map under the default convention.
        var result = IsolatedConventionTestProcess.Run(
            $"{typeof(OutboxSerializationSetupTests).FullName}.{nameof(ToBsonDocument_DefaultConvention_IsolatedCase)}");

        // Assert
        result.ExitCode.Should().Be(0, "the isolated default-convention case must pass; output:\n{0}\n{1}", result.StandardOutput, result.StandardError);
    }

    private static void AssertPinnedElementNames(BsonDocument bsonDocument)
        => bsonDocument.Names.Should().Contain([
            nameof(OutboxMessage.AttemptCount),
            nameof(OutboxMessage.CorrelationId),
            nameof(OutboxMessage.CreatedUtc),
            nameof(OutboxMessage.Error),
            nameof(OutboxMessage.FailedUtc),
            nameof(OutboxMessage.IsLocked),
            nameof(OutboxMessage.LockedUtc),
            nameof(OutboxMessage.LockId),
            nameof(OutboxMessage.NextAttemptUtc),
            nameof(OutboxMessage.Payload),
            nameof(OutboxMessage.ProcessedUtc),
            nameof(OutboxMessage.State),
            nameof(OutboxMessage.Type)
        ]);

    private static OutboxMessage CreateFullyPopulatedMessage()
        => new()
        {
            Id = ObjectId.GenerateNewId(),
            Type = "TestPayload",
            Payload = new BsonDocument("Name", "payload-value"),
            State = OutboxMessageState.Failed,
            CorrelationId = "correlation-id",
            CreatedUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Error = "boom",
            FailedUtc = new DateTime(2025, 1, 1, 1, 0, 0, DateTimeKind.Utc),
            IsLocked = true,
            LockedUtc = new DateTime(2025, 1, 1, 0, 30, 0, DateTimeKind.Utc),
            LockId = "lock-id",
            NextAttemptUtc = new DateTime(2025, 1, 1, 2, 0, 0, DateTimeKind.Utc),
            ProcessedUtc = new DateTime(2025, 1, 1, 0, 45, 0, DateTimeKind.Utc),
            AttemptCount = 2
        };
}
