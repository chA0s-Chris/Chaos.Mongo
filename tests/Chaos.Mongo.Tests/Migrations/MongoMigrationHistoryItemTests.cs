// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests.Migrations;

using Chaos.Mongo.Migrations;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using NUnit.Framework;

public class MongoMigrationHistoryItemTests
{
    [Test]
    public void Deserialize_DocumentWithUnrecognizedElement_DeserializesSuccessfully()
    {
        // Arrange
        var appliedUtc = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var document = new BsonDocument
        {
            { "_id", "20250601_InitialSchema" },
            { "AppliedUtc", appliedUtc },
            { "DurationMs", 42L },
            { "Description", "Initial schema" },
            { "FutureField", "unrecognized" }
        };

        // Act
        var act = () => BsonSerializer.Deserialize<MongoMigrationHistoryItem>(document);

        // Assert
        act.Should().NotThrow();
    }

    [Test]
    public void Deserialize_ReleasedShapeBsonDocumentWithoutDescription_RestoresValuesAndDefaultsDescription()
    {
        // Arrange: the exact field names/types written by every shipped version of this type, omitting the
        // optional Description element exactly as a 0.x writer without a description would.
        var appliedUtc = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var document = new BsonDocument
        {
            { "_id", "20250601_InitialSchema" },
            { "AppliedUtc", appliedUtc },
            { "DurationMs", 42L }
        };

        // Act
        var result = BsonSerializer.Deserialize<MongoMigrationHistoryItem>(document);

        // Assert
        result.Id.Should().Be("20250601_InitialSchema");
        result.AppliedUtc.Should().Be(appliedUtc);
        result.DurationMs.Should().Be(42L);
        result.Description.Should().BeNull();
    }

    [Test]
    public void Deserialize_ReleasedShapeBsonDocument_RestoresValues()
    {
        // Arrange
        var appliedUtc = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var document = new BsonDocument
        {
            { "_id", "20250601_InitialSchema" },
            { "AppliedUtc", appliedUtc },
            { "DurationMs", 42L },
            { "Description", "Initial schema" }
        };

        // Act
        var result = BsonSerializer.Deserialize<MongoMigrationHistoryItem>(document);

        // Assert
        result.Id.Should().Be("20250601_InitialSchema");
        result.AppliedUtc.Should().Be(appliedUtc);
        result.DurationMs.Should().Be(42L);
        result.Description.Should().Be("Initial schema");
    }
}
