// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests;

using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using NUnit.Framework;

public class MongoLockDocumentTests
{
    [Test]
    public void Deserialize_DocumentWithUnrecognizedElement_DeserializesSuccessfully()
    {
        // Arrange
        var leaseUntilUtc = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var document = new BsonDocument
        {
            { "_id", "lock-name" },
            { "Holder", "holder-id" },
            { "LeaseUntilUtc", leaseUntilUtc },
            { "FutureField", "unrecognized" }
        };

        // Act
        var act = () => BsonSerializer.Deserialize<MongoLockDocument>(document);

        // Assert
        act.Should().NotThrow();
    }

    [Test]
    public void Deserialize_ReleasedShapeBsonDocument_RestoresValues()
    {
        // Arrange: the exact field names/types written by every shipped version of this type.
        var leaseUntilUtc = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var document = new BsonDocument
        {
            { "_id", "lock-name" },
            { "Holder", "holder-id" },
            { "LeaseUntilUtc", leaseUntilUtc }
        };

        // Act
        var result = BsonSerializer.Deserialize<MongoLockDocument>(document);

        // Assert
        result.Id.Should().Be("lock-name");
        result.Holder.Should().Be("holder-id");
        result.LeaseUntilUtc.Should().Be(leaseUntilUtc);
    }
}
