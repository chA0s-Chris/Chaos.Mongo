// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity;

using Chaos.Mongo.EventStore.Integrity;
using FluentAssertions;
using MongoDB.Bson;
using NUnit.Framework;

public class StoredAggregateIdTests
{
    [Test]
    public void TryRead_NonUuidBinarySubtype_ReturnsFalse()
    {
        var value = new BsonBinaryData(Guid.NewGuid().ToByteArray(), BsonBinarySubType.Binary);

        var result = StoredAggregateId.TryRead(value, out var aggregateId);

        result.Should().BeFalse();
        aggregateId.Should().Be(Guid.Empty);
    }

    [Test]
    public void TryRead_NotBinaryData_ReturnsFalse()
    {
        var result = StoredAggregateId.TryRead(new BsonString("not-a-guid"), out var aggregateId);

        result.Should().BeFalse();
        aggregateId.Should().Be(Guid.Empty);
    }

    [Test]
    public void TryRead_UuidStandardBinary_ReturnsTheAggregateId()
    {
        var expected = Guid.NewGuid();
        var value = new BsonBinaryData(expected, GuidRepresentation.Standard);

        var result = StoredAggregateId.TryRead(value, out var aggregateId);

        result.Should().BeTrue();
        aggregateId.Should().Be(expected);
    }
}
