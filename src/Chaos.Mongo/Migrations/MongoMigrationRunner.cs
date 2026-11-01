// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Migrations;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.ExceptionServices;

/// <summary>
/// Default implementation of <see cref="IMongoMigrationRunner"/> that executes pending migrations with distributed
/// locking.
/// </summary>
/// <remarks>
/// This runner ensures safe migration execution in distributed systems by:
/// <list type="bullet">
///     <item>
///         <description>Using a distributed lock to prevent concurrent execution, waiting for it by default</description>
///     </item>
///     <item>
///         <description>
///         Renewing the lock's lease while migrations run, and stopping in-progress work if renewal is
///         refused
///         </description>
///     </item>
///     <item>
///         <description>Optionally wrapping migrations in transactions when available</description>
///     </item>
///     <item>
///         <description>Recording execution history with timing information</description>
///     </item>
/// </list>
/// </remarks>
internal class MongoMigrationRunner : IMongoMigrationRunner
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
    /// <exception cref="ArgumentException">Thrown when two or more migrations share the same <see cref="IMongoMigration.Id"/>.</exception>
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

        var materializedMigrations = migrations.ToImmutableArray();
        var duplicateIds = materializedMigrations.GroupBy(x => x.Id, StringComparer.Ordinal)
                                                 .Where(group => group.Count() > 1)
                                                 .Select(group => group.Key)
                                                 .ToImmutableArray();

        if (duplicateIds.Length > 0)
        {
            throw new ArgumentException(
                $"Migration IDs must be unique, but the following ID(s) are registered more than once: {String.Join(", ", duplicateIds)}.",
                nameof(migrations));
        }

        Migrations = [.. materializedMigrations.OrderBy(x => x.Id, StringComparer.Ordinal)];
    }

    internal ImmutableArray<IMongoMigration> Migrations { get; }

    private static async Task AbortTransactionIfActiveAsync(IClientSessionHandle? session, CancellationToken cancellationToken)
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

    /// <summary>
    /// Waits to acquire the migration lock, bounded by <see cref="MongoOptions.MigrationLockWaitTimeout"/>.
    /// </summary>
    /// <returns>The acquired lock, or <see langword="null"/> when the wait timed out.</returns>
    private async Task<IMongoLock?> AcquireMigrationLockAsync(CancellationToken cancellationToken)
    {
        var waitTimeout = _options.MigrationLockWaitTimeout;

        if (waitTimeout == TimeSpan.Zero)
        {
            return await _mongoHelper.TryAcquireLockAsync(_options.MigrationsLockName, _options.MigrationLockLeaseTime, cancellationToken);
        }

        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (waitTimeout is { } timeout)
        {
            waitCts.CancelAfter(timeout);
        }

        try
        {
            return await _mongoHelper.AcquireLockAsync(_options.MigrationsLockName,
                                                       _options.MigrationLockLeaseTime,
                                                       cancellationToken: waitCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The wait timeout elapsed rather than the caller's own token; report a skipped run instead of throwing.
            return null;
        }
    }

    private async Task ApplyMigrationAsync(IMongoMigration migration,
                                           IMongoCollection<MongoMigrationHistoryItem> historyCollection,
                                           IMongoLock mongoLock,
                                           CancellationTokenSource lockLostCts,
                                           CancellationToken protectedCancellationToken,
                                           CancellationToken cancellationToken)
    {
        IClientSessionHandle? session = null;

        if (_options.UseTransactionsForMigrationsIfAvailable)
        {
            session = await _mongoHelper.TryStartTransactionAsync(cancellationToken: protectedCancellationToken);
        }

        try
        {
            var stopWatch = Stopwatch.StartNew();
            await migration.ApplyAsync(_mongoHelper, session, protectedCancellationToken);

            // The lease may have expired locally while ApplyAsync ran, without the renewal loop having observed
            // and cancelled it yet (for example, a slow extension still in flight). Re-check before recording the
            // migration as applied so a lock we no longer provably hold cannot commit its history.
            await EnsureLockStillValidAsync(mongoLock, lockLostCts, protectedCancellationToken);

            var historyItem = new MongoMigrationHistoryItem
            {
                Id = migration.Id,
                AppliedUtc = _timeProvider.GetUtcNow().UtcDateTime,
                DurationMs = stopWatch.ElapsedMilliseconds,
                Description = migration.Description
            };

            if (session is null)
            {
                await historyCollection.InsertOneAsync(historyItem, cancellationToken: protectedCancellationToken);
            }
            else
            {
                await historyCollection.InsertOneAsync(session, historyItem, cancellationToken: protectedCancellationToken);
                await session.CommitTransactionAsync(protectedCancellationToken);
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

    /// <summary>
    /// Guards a protected-work boundary against a lease that has already expired locally but whose loss the
    /// renewal loop has not yet observed and signalled through <paramref name="lockLostCts"/>.
    /// </summary>
    /// <remarks>
    /// Checking <see cref="IMongoLock.IsValid"/> is a local, synchronous comparison against the lease the lock
    /// was last known to hold, so it catches an already-expired lease (for example, one returned by a slow
    /// acquisition that outlived its own lease, or one whose in-flight renewal has not yet completed) without
    /// waiting for a round trip. This does not add fencing: a migration that ignores
    /// <paramref name="protectedCancellationToken"/> can still complete its own non-transactional effects, per
    /// the documented cooperative-cancellation limitation.
    /// </remarks>
    private async Task EnsureLockStillValidAsync(IMongoLock mongoLock, CancellationTokenSource lockLostCts, CancellationToken protectedCancellationToken)
    {
        if (mongoLock.IsValid)
        {
            return;
        }

        _logger.LogWarning("Migration lock {LockName} is no longer valid; stopping in-progress migration work", _options.MigrationsLockName);

        if (!lockLostCts.IsCancellationRequested)
        {
            await lockLostCts.CancelAsync();
        }

        protectedCancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Renews <paramref name="mongoLock"/>'s lease for the lifetime of the whole migration run.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     Each renewal waits half of the lease's remaining lifetime rather than half of the configured lease, because
    ///     a lease's expiry is calculated before its database round trip: a slow acquisition or extension has already
    ///     consumed part of the lease by the time this loop sees it.
    ///     </para>
    ///     <para>
    ///     A refused extension, an already-expired lease (which the database would only refuse), or any exception raised
    ///     before <paramref name="stopToken"/> is cancelled, cancels <paramref name="lockLostCts"/> so in-progress
    ///     protected work observes the loss. Exceptions then propagate, so <see cref="RunMigrationsAsync"/> can surface
    ///     them when no migration exception already takes precedence.
    ///     </para>
    ///     <para>
    ///     <paramref name="stopToken"/> only cancels the wait between renewals. An extension already in flight keeps
    ///     running, because cancelling it could leave the database holding an expiry this lock never learned about, so
    ///     the release would match nothing and the lock would stay held until that lease expired. See
    ///     <see cref="TryExtendLeaseAsync"/> for what still bounds that extension. Once the protected work is done an
    ///     extension failure can no longer affect it, so it is logged rather than failing the run.
    ///     </para>
    /// </remarks>
    private async Task RunLeaseRenewalLoopAsync(IMongoLock mongoLock,
                                                CancellationTokenSource lockLostCts,
                                                CancellationToken stopToken,
                                                CancellationToken extensionAbortToken)
    {
        try
        {
            while (true)
            {
                var remainingLease = mongoLock.ValidUntilUtc - _timeProvider.GetUtcNow().UtcDateTime;
                if (remainingLease > TimeSpan.Zero)
                {
                    await Task.Delay(remainingLease / 2, _timeProvider, stopToken);

                    if (await TryExtendLeaseAsync(mongoLock, extensionAbortToken))
                    {
                        continue;
                    }
                }

                _logger.LogWarning("Migration lock {LockName} could not be renewed; stopping in-progress migration work",
                                   _options.MigrationsLockName);
                await lockLostCts.CancelAsync();
                return;
            }
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
        {
            // Normal shutdown requested by RunMigrationsAsync once the migration loop finished.
        }
        catch (Exception ex) when (stopToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Migration lock {LockName} lease renewal failed after migration work finished; the lock may stay held until its lease expires",
                               _options.MigrationsLockName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Migration lock {LockName} lease renewal failed unexpectedly; stopping in-progress migration work",
                             _options.MigrationsLockName);
            await lockLostCts.CancelAsync();
            throw;
        }
    }

    /// <summary>
    /// Extends <paramref name="mongoLock"/>'s lease, giving up once the lease being extended has expired.
    /// </summary>
    /// <remarks>
    /// Past that expiry the database would refuse the extension anyway, so this bound stops a stalled extension from
    /// hiding a lost lock from in-progress work or holding up the end of the run indefinitely.
    /// </remarks>
    /// <returns><see langword="true"/> when the lease was extended; otherwise, <see langword="false"/>.</returns>
    private async Task<Boolean> TryExtendLeaseAsync(IMongoLock mongoLock, CancellationToken extensionAbortToken)
    {
        var remainingLease = mongoLock.ValidUntilUtc - _timeProvider.GetUtcNow().UtcDateTime;
        if (remainingLease <= TimeSpan.Zero)
        {
            return false;
        }

        using var leaseExpiryCts = new CancellationTokenSource(remainingLease, _timeProvider);
        using var extensionCts = CancellationTokenSource.CreateLinkedTokenSource(extensionAbortToken, leaseExpiryCts.Token);
        return await mongoLock.TryExtendAsync(cancellationToken: extensionCts.Token);
    }

    /// <inheritdoc/>
    public async Task<MongoMigrationResult> RunMigrationsAsync(CancellationToken cancellationToken = default)
    {
        var historyCollection = _mongoHelper.Database.GetCollection<MongoMigrationHistoryItem>(_options.MigrationHistoryCollectionName);

        var mongoLock = await AcquireMigrationLockAsync(cancellationToken);
        if (mongoLock is null)
        {
            _logger.LogWarning("Timed out waiting for migration lock {LockName}; skipping migrations", _options.MigrationsLockName);
            return new MongoMigrationResult
            {
                Skipped = true,
                AppliedMigrationIds = []
            };
        }

        await using (mongoLock)
        {
            // One renewal loop spans the whole run, starting immediately after acquiring the lock so it covers
            // the history read below, the no-pending early return, every migration, and transaction cleanup -
            // not just the migration loop. lockLostCts is cancelled only when renewal is refused or fails
            // unexpectedly, and that cancellation flows into all of that protected work via protectedCts below.
            using var lockLostCts = new CancellationTokenSource();
            using var renewalStopCts = new CancellationTokenSource();
            using var extensionAbortCts = new CancellationTokenSource();
            using var protectedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lockLostCts.Token);
            var protectedCancellationToken = protectedCts.Token;

            var renewalTask = RunLeaseRenewalLoopAsync(mongoLock, lockLostCts, renewalStopCts.Token, extensionAbortCts.Token);

            var appliedIds = new List<String>();
            Exception? migrationException = null;
            try
            {
                // Reject an already-expired lease (for example, one returned by a slow acquisition that outlived its
                // own lease) before starting any protected work, rather than relying solely on the renewal loop's next tick.
                await EnsureLockStillValidAsync(mongoLock, lockLostCts, protectedCancellationToken);

                var appliedMigrationIds = (await historyCollection.Find(FilterDefinition<MongoMigrationHistoryItem>.Empty)
                                                                  .Project(x => x.Id)
                                                                  .ToListAsync(protectedCancellationToken)).ToImmutableHashSet();

                var pendingMigrations = Migrations.Where(x => !appliedMigrationIds.Contains(x.Id)).ToImmutableArray();
                if (pendingMigrations.Length == 0)
                {
                    _logger.LogInformation("No pending MongoDB migrations found");
                }
                else
                {
                    foreach (var migration in pendingMigrations)
                    {
                        await EnsureLockStillValidAsync(mongoLock, lockLostCts, protectedCancellationToken);
                        await ApplyMigrationAsync(migration, historyCollection, mongoLock, lockLostCts, protectedCancellationToken, cancellationToken);
                        appliedIds.Add(migration.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                migrationException = ex;
            }

            await renewalStopCts.CancelAsync();

            // The caller's token only reaches an in-flight extension from here on. Linking it any earlier would end
            // renewal under a migration that ignores its cancellation token and keeps running.
            using var extensionAbortRegistration = cancellationToken.Register(extensionAbortCts.Cancel);

            Exception? renewalException = null;
            try
            {
                await renewalTask;
            }
            catch (Exception ex)
            {
                renewalException = ex;
            }

            if (migrationException is not null)
            {
                ExceptionDispatchInfo.Capture(migrationException).Throw();
            }

            if (renewalException is not null)
            {
                ExceptionDispatchInfo.Capture(renewalException).Throw();
            }

            return new MongoMigrationResult
            {
                Skipped = false,
                AppliedMigrationIds = appliedIds
            };
        }
    }
}
