// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Migrations;

using MongoDB.Bson.Serialization.Attributes;

/// <summary>
/// Represents a record in the migration history collection tracking applied migrations.
/// </summary>
/// <remarks>
/// This document is stored in the collection specified by <see cref="MongoOptions.MigrationHistoryCollectionName"/>
/// (default: "_migrations"). It provides an audit trail of which migrations have been applied and when.
/// </remarks>
[BsonIgnoreExtraElements]
public sealed class MongoMigrationHistoryItem
{
    /// <summary>
    /// Gets the UTC date and time when the migration was successfully applied.
    /// </summary>
    public required DateTime AppliedUtc { get; init; }

    /// <summary>
    /// Gets the optional human-readable description of the migration from <see cref="IMongoMigration.Description"/>.
    /// </summary>
    [BsonIgnoreIfNull]
    public String? Description { get; init; }

    /// <summary>
    /// Gets the duration in milliseconds that the migration took to execute.
    /// </summary>
    /// <remarks>
    /// This includes the time to apply the migration but excludes transaction setup and history recording.
    /// </remarks>
    public required Int64 DurationMs { get; init; }

    /// <summary>
    /// Gets the unique migration identifier from <see cref="IMongoMigration.Id"/>.
    /// </summary>
    /// <remarks>
    /// This serves as the document ID in MongoDB and is used to determine if a migration has already been applied.
    /// </remarks>
    [BsonId]
    public required String Id { get; init; }
}
