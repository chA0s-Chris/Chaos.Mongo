// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox.Tests;

using FluentAssertions;
using MongoDB.Bson.Serialization;
using NUnit.Framework;

public class OutboxSerializationSetupTests
{
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
