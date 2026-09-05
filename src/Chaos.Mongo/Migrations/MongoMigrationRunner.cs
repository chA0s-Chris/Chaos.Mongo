// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Migrations;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using System.Collections.Immutable;
using System.Diagnostics;

/// <summary>
/// Default implementation of <see cref="IMongoMigrationRunner"/> that executes pending migrations with distributed
/// locking.
/// </summary>
/// <remarks>
/// This runner ensures safe migration execution in distributed systems by:
/// <list type="bullet">
///     <item>
///         <description>Using distributed locks to prevent concurrent execution</description>
///     </item>
///     <item>
///         <description>Validating lock expiration before and after each migration</description>
///     </item>
///     <item>
///         <description>Optionally wrapping migrations in transactions when available</description>
///     </item>
///     <item>
///         <description>Recording execution history with timing information</description>
///     </item>
/// </list>
/// </remarks>
public class MongoMigrationRunner : IMongoMigrationRunner
{
    private readonly ILogger _logger;
    private readonly IMongoHelper _mongoHelper;
    private readonly MongoOptions _options;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="MongoMigrationRunner"/> class.
    /// </summary>
    /// <param name="migrations">The collection of all registered migrations to execute.</param>
    /// <param name="mongoHelper">The MongoDB helper for database operations.</param>
    /// <param name="options">The MongoDB configuration options.</param>
    /// <param name="logger">The logger for diagnostic messages.</param>
    /// <param name="timeProvider">The time provider for timestamp generation.</param>
    /// <exception cref="ArgumentNullException">Thrown when any parameter is null.</exception>
    /// <remarks>
    /// Migrations are automatically sorted by their <see cref="IMongoMigration.Id"/> using ordinal string comparison.
    /// </remarks>
    public MongoMigrationRunner(IEnumerable<IMongoMigration> migrations,
                                IMongoHelper mongoHelper,
                                IOptions<MongoOptions> options,
                                ILogger<MongoMigrationRunner> logger,
                                TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        ArgumentNullException.ThrowIfNull(mongoHelper);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _mongoHelper = mongoHelper;
        _logger = logger;
        _options = options.Value;
        _timeProvider = timeProvider;
        Migrations = [.. migrations.OrderBy(x => x.Id, StringComparer.Ordinal)];
    }

    internal ImmutableArray<IMongoMigration> Migrations { get; }

    private async Task AbortTransactionIfActiveAsync(IClientSessionHandle? session, CancellationToken cancellationToken)
    {
        if (session?.IsInTransaction == true)
        {
            try
            {
                await session.AbortTransactionAsync(cancellationToken);
            }
            catch
            {
                // ignore
            }
        }
    }

    /// <inheritdoc/>
    public async Task RunMigrationsAsync(CancellationToken cancellationToken = default)
    {
        var historyCollection = _mongoHelper.Database.GetCollection<MongoMigrationHistoryItem>(_options.MigrationHistoryCollectionName);

        await using var mongoLock = await _mongoHelper.TryAcquireLockAsync(_options.MigrationsLockName, _options.MigrationLockLeaseTime, cancellationToken);
        if (mongoLock is null)
        {
            _logger.LogWarning("Migration lock {LockName} is already held by another process; skipping migrations", _options.MigrationsLockName);
            return;
        }

        var appliedMigrationIds = (await historyCollection.Find(FilterDefinition<MongoMigrationHistoryItem>.Empty)
                                                          .Project(x => x.Id)
                                                          .ToListAsync(cancellationToken)).ToImmutableHashSet();

        var pendingMigrations = Migrations.Where(x => !appliedMigrationIds.Contains(x.Id)).ToImmutableArray();
        if (pendingMigrations.Length == 0)
        {
            _logger.LogInformation("No pending MongoDB migrations found");
            return;
        }

        foreach (var migration in pendingMigrations)
        {
            IClientSessionHandle? session = null;
            mongoLock.EnsureValid();

            if (_options.UseTransactionsForMigrationsIfAvailable)
            {
                session = await _mongoHelper.TryStartTransactionAsync(cancellationToken: cancellationToken);
            }

            try
            {
                var stopWatch = Stopwatch.StartNew();
                await migration.ApplyAsync(_mongoHelper, session, cancellationToken);
                mongoLock.EnsureValid();

                var historyItem = new MongoMigrationHistoryItem
                {
                    Id = migration.Id,
                    AppliedUtc = _timeProvider.GetUtcNow().UtcDateTime,
                    DurationMs = stopWatch.ElapsedMilliseconds,
                    Description = migration.Description
                };

                if (session is null)
                {
                    await historyCollection.InsertOneAsync(historyItem, cancellationToken: cancellationToken);
                }
                else
                {
                    await historyCollection.InsertOneAsync(session, historyItem, cancellationToken: cancellationToken);
                    await session.CommitTransactionAsync(cancellationToken);
                }

                _logger.LogInformation("Successfully applied migration {MigrationId} ({Description}) in {DurationMs}ms",
                                       migration.Id,
                                       migration.Description ?? "No description",
                                       stopWatch.ElapsedMilliseconds);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to apply migration {MigrationId}", migration.Id);
                await AbortTransactionIfActiveAsync(session, cancellationToken);
                throw;
            }
            finally
            {
                session?.Dispose();
            }
        }
    }
}
