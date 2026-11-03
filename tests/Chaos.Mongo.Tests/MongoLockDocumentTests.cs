// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests;

using Chaos.Mongo.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using NUnit.Framework;

public class MongoLockDocumentTests
{
    [Test]
    public void Deserialize_DocumentWithAcquisitionToken_RestoresValue()
    {
        // Arrange: the shape written by this and every later version of this type.
        var leaseUntilUtc = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var document = new BsonDocument
        {
            { "_id", "lock-name" },
            { "Holder", "holder-id" },
            { "LeaseUntilUtc", leaseUntilUtc },
            { "AcquisitionToken", "token-value" }
        };

        // Act
        var result = BsonSerializer.Deserialize<MongoLockDocument>(document);

        // Assert
        result.Id.Should().Be("lock-name");
        result.Holder.Should().Be("holder-id");
        result.LeaseUntilUtc.Should().Be(leaseUntilUtc);
        result.AcquisitionToken.Should().Be("token-value");
    }

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
        // Arrange: the exact field names/types written before AcquisitionToken existed.
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
        result.AcquisitionToken.Should().BeNull();
    }

    [Test]
    [Explicit("Runs only via process isolation from " + nameof(ToBsonDocument_CamelCaseConventionRegisteredFirst_StillPinsElementNamesToMemberNames) +
              "; registers process-global convention and class-map state.")]
    public void ToBsonDocument_CamelCaseConventionRegisteredFirst_IsolatedCase()
    {
        // Arrange: register a global camelCase convention for MongoLockDocument before anything in this fresh
        // process has used the type, then exercise the library's normal AddMongo registration path.
        ConventionRegistry.Register(
            nameof(ToBsonDocument_CamelCaseConventionRegisteredFirst_IsolatedCase),
            new ConventionPack
            {
                new CamelCaseElementNameConvention()
            },
            type => type == typeof(MongoLockDocument));
        new ServiceCollection().AddMongo();

        var document = new MongoLockDocument
        {
            Id = "lock-name",
            Holder = "holder-id",
            LeaseUntilUtc = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc),
            AcquisitionToken = "token-value"
        };

        // Act
        var bsonDocument = document.ToBsonDocument();

        // Assert
        bsonDocument.Names.Should().Contain([
            nameof(MongoLockDocument.AcquisitionToken),
            nameof(MongoLockDocument.Holder),
            nameof(MongoLockDocument.LeaseUntilUtc)
        ]);
    }

    [Test]
    public void ToBsonDocument_CamelCaseConventionRegisteredFirst_StillPinsElementNamesToMemberNames()
    {
        // Arrange & Act: the inner case runs in its own freshly started process (see
        // IsolatedConventionTestProcess), because BsonClassMap/ConventionRegistry registration is process-global
        // and irreversible, and every other test in this shared process may already have registered
        // MongoLockDocument's class map under the default convention.
        var result = IsolatedConventionTestProcess.Run(
            $"{typeof(MongoLockDocumentTests).FullName}.{nameof(ToBsonDocument_CamelCaseConventionRegisteredFirst_IsolatedCase)}");

        // Assert
        result.ExitCode.Should().Be(0, "the isolated camelCase case must pass; output:\n{0}\n{1}", result.StandardOutput, result.StandardError);
    }

    [Test]
    [Explicit("Runs only via process isolation from " + nameof(ToBsonDocument_DefaultConvention_PinsElementNamesToMemberNames) +
              "; registers process-global class-map state.")]
    public void ToBsonDocument_DefaultConvention_IsolatedCase()
    {
        // Arrange: exercise the library's normal AddMongo registration path before anything in this fresh
        // process has used MongoLockDocument, the same as the camelCase isolated case, but without registering
        // any convention, so this proves the pinned names independently of whichever class map an earlier test
        // in a shared process may have already registered.
        new ServiceCollection().AddMongo();

        var document = new MongoLockDocument
        {
            Id = "lock-name",
            Holder = "holder-id",
            LeaseUntilUtc = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc),
            AcquisitionToken = "token-value"
        };

        // Act
        var bsonDocument = document.ToBsonDocument();

        // Assert
        bsonDocument.Names.Should().Contain([
            nameof(MongoLockDocument.AcquisitionToken),
            nameof(MongoLockDocument.Holder),
            nameof(MongoLockDocument.LeaseUntilUtc)
        ]);
    }

    [Test]
    public void ToBsonDocument_DefaultConvention_PinsElementNamesToMemberNames()
    {
        // Arrange & Act: the inner case runs in its own freshly started process (see
        // IsolatedConventionTestProcess), because BsonClassMap/ConventionRegistry registration is process-global
        // and irreversible, and every other test in this shared process may already have registered
        // MongoLockDocument's class map under the default convention.
        var result = IsolatedConventionTestProcess.Run(
            $"{typeof(MongoLockDocumentTests).FullName}.{nameof(ToBsonDocument_DefaultConvention_IsolatedCase)}");

        // Assert
        result.ExitCode.Should().Be(0, "the isolated default-convention case must pass; output:\n{0}\n{1}", result.StandardOutput, result.StandardError);
    }
}
