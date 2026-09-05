// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Migrations;

using MongoDB.Driver;

/// <summary>
/// Represents a database migration that can be applied to a MongoDB database.
/// </summary>
/// <remarks>
///     <para>
///     Migrations are executed in order based on their <see cref="Id"/> using ordinal string comparison.
///     It is recommended to use timestamp-based prefixes for IDs (e.g., "20250110_InitialSchema").
///     </para>
///     <para>
///     Migrations must be idempotent - they should be safe to run multiple times without causing errors or data
///     corruption.
///     This is critical because migrations may be partially applied before a failure occurs.
///     </para>
///     <para>
///     Register migrations using <see cref="MongoBuilder.WithMigration{T}"/> or
///     <see cref="MongoBuilder.WithMigrationAutoDiscovery"/>.
///     </para>
/// </remarks>
public interface IMongoMigration
{
    /// <summary>
    /// Gets an optional human-readable description of what this migration does.
    /// </summary>
    /// <remarks>
    /// This description is stored in the migration history and can be useful for debugging and auditing.
    /// </remarks>
    String? Description => null;

    /// <summary>
    /// Gets the unique identifier for this migration.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     This ID is used to determine:
    ///     <list type="bullet">
    ///         <item>
    ///             <description>The execution order (ordinal string comparison)</description>
    ///         </item>
    ///         <item>
    ///             <description>Whether the migration has already been applied</description>
    ///         </item>
    ///         <item>
    ///             <description>The migration's identity in the history collection</description>
    ///         </item>
    ///     </list>
    ///     </para>
    ///     <para>
    ///     Recommended format: "YYYYMMDDXX_DescriptiveName" (e.g., "2025011001_AddUserIndexes")
    ///     </para>
    /// </remarks>
    String Id { get; }

    /// <summary>
    /// Applies the migration to the database.
    /// </summary>
    /// <param name="mongoHelper">Helper for accessing MongoDB collections and operations.</param>
    /// <param name="session">
    /// Optional transaction session. If provided, all operations should use this session for
    /// transactional consistency.
    /// </param>
    /// <param name="cancellationToken">Cancellation token to abort the migration.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <remarks>
    ///     <para>
    ///     If <paramref name="session"/> is not null, the migration is running within a transaction.
    ///     All database operations should pass the session to maintain ACID guarantees.
    ///     </para>
    ///     <para>
    ///     If an exception is thrown, the transaction (if any) will be aborted and the migration
    ///     will not be recorded in the history collection.
    ///     </para>
    /// </remarks>
    /// <exception cref="OperationCanceledException">
    /// Thrown when the operation is cancelled via
    /// <paramref name="cancellationToken"/>.
    /// </exception>
    Task ApplyAsync(IMongoHelper mongoHelper, IClientSessionHandle? session = null, CancellationToken cancellationToken = default);
}
