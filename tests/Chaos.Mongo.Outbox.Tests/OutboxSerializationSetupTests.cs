// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox.Tests;

using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
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
            ["RetryCount"] = 0,
            ["UnrecognizedField"] = "a field no version of OutboxMessage declares"
        };

        var message = BsonSerializer.Deserialize<OutboxMessage>(document);

        message.Id.Should().Be(id);
        message.Type.Should().Be("TestPayload");
        message.CreatedUtc.Should().Be(createdUtc);
        message.State.Should().Be(OutboxMessageState.Pending);
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
}
