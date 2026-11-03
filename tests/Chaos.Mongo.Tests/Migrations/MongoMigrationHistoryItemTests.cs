// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests.Migrations;

using Chaos.Mongo.Migrations;
using Chaos.Mongo.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
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

    [Test]
    [Explicit("Runs only via process isolation from " + nameof(ToBsonDocument_CamelCaseConventionRegisteredFirst_StillPinsElementNamesToMemberNames) +
              "; registers process-global convention and class-map state.")]
    public void ToBsonDocument_CamelCaseConventionRegisteredFirst_IsolatedCase()
    {
        // Arrange: register a global camelCase convention for MongoMigrationHistoryItem before anything in this
        // fresh process has used the type, then exercise the library's normal AddMongo registration path.
        ConventionRegistry.Register(
            nameof(ToBsonDocument_CamelCaseConventionRegisteredFirst_IsolatedCase),
            new ConventionPack
            {
                new CamelCaseElementNameConvention()
            },
            type => type == typeof(MongoMigrationHistoryItem));
        new ServiceCollection().AddMongo();

        var item = new MongoMigrationHistoryItem
        {
            Id = "20250601_InitialSchema",
            AppliedUtc = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc),
            DurationMs = 42L,
            Description = "Initial schema"
        };

        // Act
        var bsonDocument = item.ToBsonDocument();

        // Assert
        bsonDocument.Names.Should().Contain([
            nameof(MongoMigrationHistoryItem.AppliedUtc),
            nameof(MongoMigrationHistoryItem.Description),
            nameof(MongoMigrationHistoryItem.DurationMs)
        ]);
    }

    [Test]
    public void ToBsonDocument_CamelCaseConventionRegisteredFirst_StillPinsElementNamesToMemberNames()
    {
        // Arrange & Act: the inner case runs in its own freshly started process (see
        // IsolatedConventionTestProcess), because BsonClassMap/ConventionRegistry registration is process-global
        // and irreversible, and every other test in this shared process may already have registered
        // MongoMigrationHistoryItem's class map under the default convention.
        var result = IsolatedConventionTestProcess.Run(
            $"{typeof(MongoMigrationHistoryItemTests).FullName}.{nameof(ToBsonDocument_CamelCaseConventionRegisteredFirst_IsolatedCase)}");

        // Assert
        result.ExitCode.Should().Be(0, "the isolated camelCase case must pass; output:\n{0}\n{1}", result.StandardOutput, result.StandardError);
    }

    [Test]
    [Explicit("Runs only via process isolation from " + nameof(ToBsonDocument_DefaultConvention_PinsElementNamesToMemberNames) +
              "; registers process-global class-map state.")]
    public void ToBsonDocument_DefaultConvention_IsolatedCase()
    {
        // Arrange: exercise the library's normal AddMongo registration path before anything in this fresh
        // process has used MongoMigrationHistoryItem, the same as the camelCase isolated case, but without
        // registering any convention, so this proves the pinned names independently of whichever class map an
        // earlier test in a shared process may have already registered.
        new ServiceCollection().AddMongo();

        var item = new MongoMigrationHistoryItem
        {
            Id = "20250601_InitialSchema",
            AppliedUtc = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc),
            DurationMs = 42L,
            Description = "Initial schema"
        };

        // Act
        var bsonDocument = item.ToBsonDocument();

        // Assert
        bsonDocument.Names.Should().Contain([
            nameof(MongoMigrationHistoryItem.AppliedUtc),
            nameof(MongoMigrationHistoryItem.Description),
            nameof(MongoMigrationHistoryItem.DurationMs)
        ]);
    }

    [Test]
    public void ToBsonDocument_DefaultConvention_PinsElementNamesToMemberNames()
    {
        // Arrange & Act: the inner case runs in its own freshly started process (see
        // IsolatedConventionTestProcess), because BsonClassMap/ConventionRegistry registration is process-global
        // and irreversible, and every other test in this shared process may already have registered
        // MongoMigrationHistoryItem's class map under the default convention.
        var result = IsolatedConventionTestProcess.Run(
            $"{typeof(MongoMigrationHistoryItemTests).FullName}.{nameof(ToBsonDocument_DefaultConvention_IsolatedCase)}");

        // Assert
        result.ExitCode.Should().Be(0, "the isolated default-convention case must pass; output:\n{0}\n{1}", result.StandardOutput, result.StandardError);
    }
}
