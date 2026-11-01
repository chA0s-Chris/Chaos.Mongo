// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Migrations;

/// <summary>
/// Describes the outcome of a <see cref="IMongoMigrationRunner.RunMigrationsAsync"/> call.
/// </summary>
public sealed class MongoMigrationResult
{
    /// <summary>
    /// Gets the IDs of the migrations that were applied during this run, in execution order.
    /// </summary>
    /// <remarks>
    /// Empty when <see cref="Skipped"/> is <see langword="true"/>, or when the lock was acquired but no pending
    /// migrations were found.
    /// </remarks>
    public required IReadOnlyList<String> AppliedMigrationIds { get; init; }

    /// <summary>
    /// Gets a value indicating whether the run was skipped because the migration lock could not be acquired
    /// within the configured wait timeout.
    /// </summary>
    /// <remarks>
    /// <see langword="true"/> only when the lock could not be acquired; it does not indicate a migration failure.
    /// </remarks>
    public required Boolean Skipped { get; init; }
}
