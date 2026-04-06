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
        var options = new OutboxOptions
        {
            MessageTypes =
            {
                [typeof(TestPayload)] = "TestPayload"
            }
        };

        OutboxSerializationSetup.RegisterClassMaps(options);

        var act = () => OutboxSerializationSetup.RegisterClassMaps(options);

        act.Should().NotThrow();
    }

    [Test]
    public void RegisterClassMaps_RegistersOutboxMessageClassMap()
    {
        var options = new OutboxOptions
        {
            MessageTypes =
            {
                [typeof(TestPayload)] = "TestPayload"
            }
        };

        OutboxSerializationSetup.RegisterClassMaps(options);

        BsonClassMap.IsClassMapRegistered(typeof(OutboxMessage)).Should().BeTrue();
    }

    [Test]
    public void RegisterClassMaps_RegistersPayloadTypeClassMaps()
    {
        var options = new OutboxOptions
        {
            MessageTypes =
            {
                [typeof(TestPayload)] = "TestPayload",
                [typeof(AnotherTestPayload)] = "AnotherPayload"
            }
        };

        OutboxSerializationSetup.RegisterClassMaps(options);

        BsonClassMap.IsClassMapRegistered(typeof(TestPayload)).Should().BeTrue();
        BsonClassMap.IsClassMapRegistered(typeof(AnotherTestPayload)).Should().BeTrue();
    }
}
