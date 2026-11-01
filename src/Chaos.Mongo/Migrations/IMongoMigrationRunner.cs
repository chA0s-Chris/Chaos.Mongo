// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Migrations;

/// <summary>
/// Executes pending MongoDB migrations in order.
/// </summary>
/// <remarks>
/// This runner is typically invoked automatically during application startup via <see cref="MongoHostedService"/>
/// when <see cref="MongoOptions.ApplyMigrationsOnStartup"/> is enabled.
/// </remarks>
public interface IMongoMigrationRunner
{
    /// <summary>
    /// Executes all pending migrations that have not yet been applied.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to abort migration execution.</param>
    /// <returns>
    /// A <see cref="MongoMigrationResult"/> describing whether the run was skipped and which migrations were
    /// applied.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///     This method will:
    ///     <list type="number">
    ///         <item>
    ///             <description>
    ///             Wait to acquire a distributed lock, bounded by <see cref="MongoOptions.MigrationLockWaitTimeout"/>
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>Query the migration history collection to find applied migrations</description>
    ///         </item>
    ///         <item>
    ///             <description>Execute pending migrations in order, renewing the lock lease while they run</description>
    ///         </item>
    ///         <item>
    ///             <description>Record timing and metadata for each successful migration</description>
    ///         </item>
    ///     </list>
    ///     </para>
    ///     <para>
    ///     When the lock cannot be acquired within <see cref="MongoOptions.MigrationLockWaitTimeout"/>, this method
    ///     returns a result with <see cref="MongoMigrationResult.Skipped"/> set to <see langword="true"/> instead of
    ///     throwing. Callers that invoke this method directly are responsible for inspecting
    ///     <see cref="MongoMigrationResult.Skipped"/> and deciding whether to proceed, retry, or fail; unlike
    ///     <see cref="MongoHostedService"/>, this method does not fail hosted startup automatically.
    ///     </para>
    ///     <para>
    ///     If a migration fails, or the lock's lease cannot be renewed while a migration run is in progress, execution
    ///     is cancelled and the exception is propagated. A migration that observes its cancellation token stops
    ///     promptly; one that does not cannot be forcibly interrupted, and any non-transactional effects it already
    ///     performed cannot be rolled back. Previously applied migrations in the same run remain applied.
    ///     </para>
    /// </remarks>
    /// <exception cref="OperationCanceledException">
    /// Thrown when cancelled via <paramref name="cancellationToken"/>, or when the migration lock's lease could not
    /// be renewed while a migration run was in progress.
    /// </exception>
    Task<MongoMigrationResult> RunMigrationsAsync(CancellationToken cancellationToken = default);
}
