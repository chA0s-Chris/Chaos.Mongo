// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests.Migrations;

using Chaos.Mongo.Migrations;
using Chaos.Testing.Logging;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Core.Bindings;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using MongoDB.Driver.Search;
using Moq;
using NUnit.Framework;
using System.Net;

public class MongoMigrationRunnerTests
{
    [Test]
    public void Constructor_WithDuplicateMigrationIds_ThrowsArgumentException()
    {
        // Arrange
        var helper = new ThrowingMongoHelper();
        var options = Options.Create(new MongoOptions());
        var logger = new NUnitTestLogger<MongoMigrationRunner>();
        var timeProvider = new FakeTimeProvider();

        var migration1 = new FakeMigration("001_Duplicate");
        var migration2 = new FakeMigration("002_Unique");
        var migration3 = new FakeMigration("001_Duplicate");

        // Act
        var act = () => new MongoMigrationRunner([migration1, migration2, migration3], helper, options, logger, timeProvider);

        // Assert
        act.Should().Throw<ArgumentException>()
           .WithParameterName("migrations")
           .WithMessage("*001_Duplicate*");
    }

    [Test]
    public void Constructor_WithMigrationsInWrongOrder_SortsThemAlphabetically()
    {
        // Arrange
        var helper = new Mock<IMongoHelper>();
        var options = Options.Create(new MongoOptions());
        var logger = new NUnitTestLogger<MongoMigrationRunner>();
        var timeProvider = new FakeTimeProvider();

        var migration1 = new Mock<IMongoMigration>();
        migration1.SetupGet(x => x.Id).Returns("003_Third");

        var migration2 = new Mock<IMongoMigration>();
        migration2.SetupGet(x => x.Id).Returns("001_First");

        var migration3 = new Mock<IMongoMigration>();
        migration3.SetupGet(x => x.Id).Returns("002_Second");

        // Act
        var sut = new MongoMigrationRunner(
            [migration1.Object, migration2.Object, migration3.Object],
            helper.Object,
            options,
            logger,
            timeProvider);

        // Assert
        var migrations = sut.Migrations;
        migrations.Should().HaveCount(3);
        migrations[0].Id.Should().Be("001_First");
        migrations[1].Id.Should().Be("002_Second");
        migrations[2].Id.Should().Be("003_Third");
    }

    [Test]
    public void Constructor_WithNullLogger_ThrowsArgumentNullException()
    {
        // Arrange
        var migration = new Mock<IMongoMigration>();
        migration.SetupGet(x => x.Id).Returns("001_Test");
        var helper = new Mock<IMongoHelper>();
        var options = Options.Create(new MongoOptions());
        var timeProvider = new FakeTimeProvider();

        // Act
        var act = () => new MongoMigrationRunner([migration.Object], helper.Object, options, null!, timeProvider);

        // Assert
        act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    [Test]
    public void Constructor_WithNullMigrations_ThrowsArgumentNullException()
    {
        // Arrange
        var helper = new Mock<IMongoHelper>(MockBehavior.Strict);
        var options = Options.Create(new MongoOptions());
        var logger = new NUnitTestLogger<MongoMigrationRunner>();
        var timeProvider = new FakeTimeProvider();

        // Act
        var act = () => new MongoMigrationRunner(null!, helper.Object, options, logger, timeProvider);

        // Assert
        act.Should().Throw<ArgumentNullException>().WithParameterName("migrations");
    }

    [Test]
    public void Constructor_WithNullMongoHelper_ThrowsArgumentNullException()
    {
        // Arrange
        var migration = new Mock<IMongoMigration>();
        migration.SetupGet(x => x.Id).Returns("001_Test");
        var options = Options.Create(new MongoOptions());
        var logger = new NUnitTestLogger<MongoMigrationRunner>();
        var timeProvider = new FakeTimeProvider();

        // Act
        var act = () => new MongoMigrationRunner([migration.Object], null!, options, logger, timeProvider);

        // Assert
        act.Should().Throw<ArgumentNullException>().WithParameterName("mongoHelper");
    }

    [Test]
    public void Constructor_WithNullOptions_ThrowsArgumentNullException()
    {
        // Arrange
        var migration = new Mock<IMongoMigration>();
        migration.SetupGet(x => x.Id).Returns("001_Test");
        var helper = new Mock<IMongoHelper>();
        var logger = new NUnitTestLogger<MongoMigrationRunner>();
        var timeProvider = new FakeTimeProvider();

        // Act
        var act = () => new MongoMigrationRunner([migration.Object], helper.Object, null!, logger, timeProvider);

        // Assert
        act.Should().Throw<ArgumentNullException>().WithParameterName("options");
    }

    [Test]
    public void Constructor_WithNullTimeProvider_ThrowsArgumentNullException()
    {
        // Arrange
        var migration = new Mock<IMongoMigration>();
        migration.SetupGet(x => x.Id).Returns("001_Test");
        var helper = new Mock<IMongoHelper>();
        var options = Options.Create(new MongoOptions());
        var logger = new NUnitTestLogger<MongoMigrationRunner>();

        // Act
        var act = () => new MongoMigrationRunner([migration.Object], helper.Object, options, logger, null!);

        // Assert
        act.Should().Throw<ArgumentNullException>().WithParameterName("timeProvider");
    }

    [Test]
    public async Task RunMigrationsAsync_WithAlreadyExpiredAcquiredLock_StopsBeforeReadingHistoryOrApplyingAnyMigration()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var leaseTime = TimeSpan.FromMinutes(10);

        // The acquired lock's expiry is already in the past, simulating a slow acquisition round-trip that
        // outlived its own lease before RunMigrationsAsync could reach any protected work. No extension is ever
        // attempted from this test, so a bug here would let the run read history and apply migrations under a
        // lock it does not provably hold.
        var mongoLock = new MongoLock("migration-lock",
                                      timeProvider.GetUtcNow().UtcDateTime.AddMilliseconds(-1),
                                      leaseTime,
                                      timeProvider,
                                      () => Task.CompletedTask,
                                      (_, _) => throw new InvalidOperationException("No extension should be attempted once the lock is already invalid."));

        var historyReadCount = 0;
        var historyCollection = new FakeHistoryCollection(onFind: _ =>
        {
            historyReadCount++;
            return Task.CompletedTask;
        });
        var helper = new FakeMongoHelper((_, _, _) => Task.FromResult<IMongoLock?>(mongoLock), historyCollection);
        var options = Options.Create(new MongoOptions
        {
            MigrationLockLeaseTime = leaseTime,
            UseTransactionsForMigrationsIfAvailable = false
        });
        var logger = new NUnitTestLogger<MongoMigrationRunner>();
        var migration = new FakeMigration("001_NeverReached");

        var sut = new MongoMigrationRunner([migration], helper, options, logger, timeProvider);

        // Act
        var act = async () => await sut.RunMigrationsAsync(CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        historyReadCount.Should().Be(0);
        migration.ApplyCallCount.Should().Be(0);
        historyCollection.InsertedItems.Should().BeEmpty();
    }

    [Test]
    public async Task RunMigrationsAsync_WithHistoryReadReturningAfterLeaseExpiry_StopsBeforeApplyingAnyMigration()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var leaseTime = TimeSpan.FromMinutes(10);

        // Every extension is refused, so the lease cannot outlive the history read below.
        var mongoLock = CreateMongoLock(timeProvider, leaseTime, (_, _) => Task.FromResult<DateTime?>(null));

        // The read itself completes normally; only the clock has passed the lease's expiry by the time it returns.
        var historyCollection = new FakeHistoryCollection(onFind: _ =>
        {
            timeProvider.Advance(leaseTime);
            return Task.CompletedTask;
        });

        var helper = new FakeMongoHelper((_, _, _) => Task.FromResult<IMongoLock?>(mongoLock), historyCollection);
        var options = Options.Create(new MongoOptions
        {
            MigrationLockLeaseTime = leaseTime,
            UseTransactionsForMigrationsIfAvailable = false
        });
        var logger = new NUnitTestLogger<MongoMigrationRunner>();
        var migration = new FakeMigration("001_NeverReached");

        var sut = new MongoMigrationRunner([migration], helper, options, logger, timeProvider);

        // Act
        var act = async () => await sut.RunMigrationsAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        migration.ApplyCallCount.Should().Be(0);
        historyCollection.InsertedItems.Should().BeEmpty();
    }

    [Test]
    public async Task RunMigrationsAsync_WithMigrationIgnoringCancellationPastLeaseExpiry_DoesNotRecordIt()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var leaseTime = TimeSpan.FromMinutes(10);
        var extensionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The extension stalls until the lease it is meant to extend has expired.
        var mongoLock = CreateMongoLock(timeProvider, leaseTime, async (_, ct) =>
        {
            extensionStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return null;
        });

        var historyCollection = new FakeHistoryCollection();
        var helper = new FakeMongoHelper((_, _, _) => Task.FromResult<IMongoLock?>(mongoLock), historyCollection);
        var options = Options.Create(new MongoOptions
        {
            MigrationLockLeaseTime = leaseTime,
            UseTransactionsForMigrationsIfAvailable = false
        });
        var logger = new NUnitTestLogger<MongoMigrationRunner>();

        // The migration ignores its cancellation token and returns normally once the test releases it.
        var migrationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMigration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var migration = new FakeMigration("001_IgnoresCancellation", async (_, _, _) =>
        {
            migrationStarted.SetResult();
            await releaseMigration.Task;
        });

        var sut = new MongoMigrationRunner([migration], helper, options, logger, timeProvider);

        // Act
        var runTask = sut.RunMigrationsAsync(CancellationToken.None);
        await migrationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        timeProvider.Advance(leaseTime / 2);
        await extensionStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        timeProvider.Advance(leaseTime / 2);
        releaseMigration.SetResult();

        var act = async () => await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        migration.ApplyCallCount.Should().Be(1);
        historyCollection.InsertedItems.Should().BeEmpty();
    }

    [Test]
    public async Task EnsureLockStillValidAsync_InvalidLockWithLockLostAlreadyCancelled_ThrowsWithoutWaitingForProtectedToken()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var sut = CreateGuardTestRunner(timeProvider, new NUnitTestLogger<MongoMigrationRunner>());
        var mongoLock = CreateExpiredMongoLock(timeProvider);
        using var lockLostCts = new CancellationTokenSource();
        await lockLostCts.CancelAsync();

        // An uncancelled token stands in for a protectedCts whose propagation from lockLostCts is still pending.
        using var pendingProtectedCts = new CancellationTokenSource();

        // Act
        // The assertion below awaits the delegate before the token sources are disposed.
        // ReSharper disable AccessToDisposedClosure
        var act = async () => await sut.EnsureLockStillValidAsync(mongoLock, lockLostCts, pendingProtectedCts.Token);
        // ReSharper restore AccessToDisposedClosure

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task EnsureLockStillValidAsync_ValidLockWithLockLostAlreadyCancelled_ThrowsWithoutWaitingForProtectedToken()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var sut = CreateGuardTestRunner(timeProvider, new NUnitTestLogger<MongoMigrationRunner>());

        // The lease is still locally valid, as after an extension that threw instead of being refused.
        var mongoLock = CreateMongoLock(timeProvider, TimeSpan.FromMinutes(10), (_, _) => Task.FromResult<DateTime?>(null));
        using var lockLostCts = new CancellationTokenSource();
        await lockLostCts.CancelAsync();
        using var pendingProtectedCts = new CancellationTokenSource();

        // Act
        // The assertion below awaits the delegate before the token sources are disposed.
        // ReSharper disable AccessToDisposedClosure
        var act = async () => await sut.EnsureLockStillValidAsync(mongoLock, lockLostCts, pendingProtectedCts.Token);
        // ReSharper restore AccessToDisposedClosure

        // Assert
        mongoLock.IsValid.Should().BeTrue();
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task EnsureLockStillValidAsync_InvalidLockWithLockLostNotCancelled_CancelsLockLostAndThrows()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var sut = CreateGuardTestRunner(timeProvider, new NUnitTestLogger<MongoMigrationRunner>());
        var mongoLock = CreateExpiredMongoLock(timeProvider);
        using var lockLostCts = new CancellationTokenSource();
        using var pendingProtectedCts = new CancellationTokenSource();

        // Act
        // The assertion below awaits the delegate before the token sources are disposed.
        // ReSharper disable AccessToDisposedClosure
        var act = async () => await sut.EnsureLockStillValidAsync(mongoLock, lockLostCts, pendingProtectedCts.Token);
        // ReSharper restore AccessToDisposedClosure

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        lockLostCts.IsCancellationRequested.Should().BeTrue();
    }

    [Test]
    public async Task EnsureLockStillValidAsync_CancellationCallbackThrows_LogsCallbackFailureAndThrowsOperationCanceled()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var logger = new NUnitTestLogger<MongoMigrationRunner>(captureMessages: true);
        var sut = CreateGuardTestRunner(timeProvider, logger);
        var mongoLock = CreateExpiredMongoLock(timeProvider);
        using var lockLostCts = new CancellationTokenSource();

        // A migration's token is linked to lockLostCts through protectedCts, so its registrations run during the
        // propagation that cancelling lockLostCts starts.
        using var migrationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(lockLostCts.Token);
        var callbackFailure = new InvalidOperationException("Migration cancellation callback failed");
        await using var registration = migrationTokenSource.Token.Register(() => throw callbackFailure);

        // Act
        // The assertion below awaits the delegate before lockLostCts is disposed.
        // ReSharper disable once AccessToDisposedClosure
        var act = async () => await sut.EnsureLockStillValidAsync(mongoLock, lockLostCts, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        logger.LogMessages.Should().Contain(message => message.LogLevel == LogLevel.Warning && ContainsException(message.Exception, callbackFailure));
    }

    [Test]
    public async Task RunMigrationsAsync_WithLeaseLostWhileTransactionStarts_AbortsTransactionWithoutApplyingMigration()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var leaseTime = TimeSpan.FromMinutes(10);

        // Every extension is refused, so the lease cannot outlive the transaction start below.
        var mongoLock = CreateMongoLock(timeProvider, leaseTime, (_, _) => Task.FromResult<DateTime?>(null));
        var client = new FakeMongoClient(() =>
        {
            timeProvider.Advance(leaseTime);
            return Task.CompletedTask;
        });

        var historyCollection = new FakeHistoryCollection();
        var helper = new FakeMongoHelper((_, _, _) => Task.FromResult<IMongoLock?>(mongoLock), historyCollection, client);
        var options = Options.Create(new MongoOptions
        {
            MigrationLockLeaseTime = leaseTime,
            UseTransactionsForMigrationsIfAvailable = true
        });
        var logger = new NUnitTestLogger<MongoMigrationRunner>();
        var migration = new FakeMigration("001_NeverReached");

        var sut = new MongoMigrationRunner([migration], helper, options, logger, timeProvider);

        // Act
        var act = async () => await sut.RunMigrationsAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        migration.ApplyCallCount.Should().Be(0);
        var session = client.Sessions.Should().ContainSingle().Subject;
        session.AbortCount.Should().Be(1);
        session.CommitCount.Should().Be(0);
    }

    [Test]
    public async Task RunMigrationsAsync_WithLeaseLostWhileHistoryEntryIsWritten_AbortsTransactionWithoutCommitting()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var leaseTime = TimeSpan.FromMinutes(10);

        // Every extension is refused, so the lease cannot outlive the history write below.
        var mongoLock = CreateMongoLock(timeProvider, leaseTime, (_, _) => Task.FromResult<DateTime?>(null));
        var client = new FakeMongoClient();

        // The write itself completes normally; only the clock has passed the lease's expiry by the time it returns.
        var historyCollection = new FakeHistoryCollection(onInsert: _ =>
        {
            timeProvider.Advance(leaseTime);
            return Task.CompletedTask;
        });

        var helper = new FakeMongoHelper((_, _, _) => Task.FromResult<IMongoLock?>(mongoLock), historyCollection, client);
        var options = Options.Create(new MongoOptions
        {
            MigrationLockLeaseTime = leaseTime,
            UseTransactionsForMigrationsIfAvailable = true
        });
        var logger = new NUnitTestLogger<MongoMigrationRunner>();
        var migration = new FakeMigration("001_NotCommitted");

        var sut = new MongoMigrationRunner([migration], helper, options, logger, timeProvider);

        // Act
        var act = async () => await sut.RunMigrationsAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        migration.ApplyCallCount.Should().Be(1);
        var session = client.Sessions.Should().ContainSingle().Subject;
        session.CommitCount.Should().Be(0);
        session.AbortCount.Should().Be(1);
    }

    [Test]
    public async Task RunMigrationsAsync_WithRefusedExtensionAndThrowingCancellationCallback_LogsCallbackFailureAndStopsMigration()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var leaseTime = TimeSpan.FromMinutes(10);
        var mongoLock = CreateMongoLock(timeProvider, leaseTime, (_, _) => Task.FromResult<DateTime?>(null));

        var historyCollection = new FakeHistoryCollection();
        var helper = new FakeMongoHelper((_, _, _) => Task.FromResult<IMongoLock?>(mongoLock), historyCollection);
        var options = Options.Create(new MongoOptions
        {
            MigrationLockLeaseTime = leaseTime,
            UseTransactionsForMigrationsIfAvailable = false
        });
        var logger = new NUnitTestLogger<MongoMigrationRunner>(captureMessages: true);

        // The migration's only cancellation callback releases it and then throws, so the callback is guaranteed to
        // run before the migration can continue, regardless of callback order or scheduling.
        var callbackFailure = new InvalidOperationException("Migration cancellation callback failed");
        var migrationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var migrationReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var migration = new FakeMigration("001_ThrowingCallback", async (_, _, ct) =>
        {
            await using var registration = ct.Register(() =>
            {
                migrationReleased.TrySetResult();
                throw callbackFailure;
            });
            migrationStarted.SetResult();
            await migrationReleased.Task;
            ct.ThrowIfCancellationRequested();
        });

        var sut = new MongoMigrationRunner([migration], helper, options, logger, timeProvider);

        // Act
        var runTask = sut.RunMigrationsAsync(CancellationToken.None);
        await migrationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        timeProvider.Advance(leaseTime / 2);

        var act = async () => await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert: the refused renewal stays a lock loss; the failing callback is logged instead of turning it into an
        // unexpected renewal failure.
        await act.Should().ThrowAsync<OperationCanceledException>();
        historyCollection.InsertedItems.Should().BeEmpty();
        logger.LogMessages.Should().Contain(message => message.LogLevel == LogLevel.Warning &&
                                                       message.Message.StartsWith("A cancellation callback failed while stopping work under migration lock") &&
                                                       ContainsException(message.Exception, callbackFailure));
        logger.LogMessages.Should().NotContain(message => message.Message.Contains("renewal failed"));
    }

    [Test]
    public async Task RunMigrationsAsync_WithNothingPendingAndHistoryReadReturningAfterLeaseExpiry_ThrowsInsteadOfReportingSuccess()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var leaseTime = TimeSpan.FromMinutes(10);
        var extensionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseExtension = new TaskCompletionSource<DateTime?>(TaskCreationOptions.RunContinuationsAsynchronously);

        // The extension stalls and ignores its token until the test releases it, so the renewal loop cannot signal the
        // loss while the history read is running; only the local lease check can detect it.
        var mongoLock = CreateMongoLock(timeProvider, leaseTime, async (_, _) =>
        {
            extensionStarted.TrySetResult();
            return await releaseExtension.Task;
        });

        // Every registered migration is already in the history, but the read returns only after the lease expired.
        var historyCollection = new FakeHistoryCollection([
                                                              new MongoMigrationHistoryItem
                                                              {
                                                                  Id = "001_AlreadyApplied",
                                                                  AppliedUtc = DateTime.UnixEpoch,
                                                                  DurationMs = 0
                                                              }
                                                          ],
                                                          onFind: async _ =>
                                                          {
                                                              timeProvider.Advance(leaseTime / 2);
                                                              await extensionStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                                                              timeProvider.Advance(leaseTime / 2);
                                                          });

        var helper = new FakeMongoHelper((_, _, _) => Task.FromResult<IMongoLock?>(mongoLock), historyCollection);
        var options = Options.Create(new MongoOptions
        {
            MigrationLockLeaseTime = leaseTime,
            UseTransactionsForMigrationsIfAvailable = false
        });
        var logger = new SignalingLogger(new NUnitTestLogger<MongoMigrationRunner>(),
                                         message => message.StartsWith("No pending MongoDB migrations found") ||
                                                    message.Contains("is no longer valid"));
        var migration = new FakeMigration("001_AlreadyApplied");

        var sut = new MongoMigrationRunner([migration], helper, options, logger, timeProvider);

        // Act: release the stalled extension only once the run has decided how to end, so it cannot influence that.
        var runTask = sut.RunMigrationsAsync(CancellationToken.None);
        try
        {
            await logger.Signal.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            releaseExtension.TrySetResult(null);
        }

        var act = async () => await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        migration.ApplyCallCount.Should().Be(0);
    }

    [Test]
    public async Task RunMigrationsAsync_WithCancelledTokenAndLockNeverAvailable_PropagatesCancellation()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        var helper = new FakeMongoHelper((_, _, _) =>
        {
            cts.Cancel();
            return Task.FromResult<IMongoLock?>(null);
        });
        var options = Options.Create(new MongoOptions
        {
            MigrationLockWaitTimeout = TimeSpan.FromMinutes(30)
        });
        var logger = new NUnitTestLogger<MongoMigrationRunner>();
        var timeProvider = new FakeTimeProvider();
        var migration = new FakeMigration("001_TestMigration");

        var sut = new MongoMigrationRunner([migration], helper, options, logger, timeProvider);

        // Act
        var act = async () => await sut.RunMigrationsAsync(cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task RunMigrationsAsync_WithExtensionFailingAfterMigrationsFinished_ReturnsSuccessfulResult()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var leaseTime = TimeSpan.FromMinutes(10);
        var extensionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var extensionResult = new TaskCompletionSource<DateTime?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var mongoLock = CreateMongoLock(timeProvider, leaseTime, (_, _) =>
        {
            extensionStarted.SetResult();
            return extensionResult.Task;
        });

        var historyCollection = new FakeHistoryCollection();
        var helper = new FakeMongoHelper((_, _, _) => Task.FromResult<IMongoLock?>(mongoLock), historyCollection);
        var options = Options.Create(new MongoOptions
        {
            MigrationLockLeaseTime = leaseTime,
            UseTransactionsForMigrationsIfAvailable = false
        });
        var logger = new NUnitTestLogger<MongoMigrationRunner>();

        var releaseMigration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var migrationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var migration = new FakeMigration("001_Finishing", async (_, _, ct) =>
        {
            migrationStarted.SetResult();
            await releaseMigration.Task.WaitAsync(ct);
        });

        var sut = new MongoMigrationRunner([migration], helper, options, logger, timeProvider);

        // Act
        var runTask = sut.RunMigrationsAsync(CancellationToken.None);
        await migrationStarted.Task;
        timeProvider.Advance(leaseTime / 2);
        await extensionStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        releaseMigration.SetResult();
        await WaitForAsync(() => historyCollection.InsertedItems.Count > 0);
        await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromMilliseconds(100)));
        extensionResult.SetException(new InvalidOperationException("Lease extension failed unexpectedly"));
        var result = await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        result.AppliedMigrationIds.Should().Equal("001_Finishing");
    }

    [Test]
    public async Task RunMigrationsAsync_WithExtensionInFlightWhenCallerCancelsAfterMigrationsFinished_AbortsExtensionAndCompletes()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var leaseTime = TimeSpan.FromMinutes(10);
        var extensionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var extensionCancelled = false;

        var mongoLock = CreateMongoLock(timeProvider, leaseTime, async (_, ct) =>
        {
            extensionStarted.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                extensionCancelled = true;
                throw;
            }

            return null;
        });

        var historyCollection = new FakeHistoryCollection();
        var helper = new FakeMongoHelper((_, _, _) => Task.FromResult<IMongoLock?>(mongoLock), historyCollection);
        var options = Options.Create(new MongoOptions
        {
            MigrationLockLeaseTime = leaseTime,
            UseTransactionsForMigrationsIfAvailable = false
        });
        var logger = new NUnitTestLogger<MongoMigrationRunner>();

        var releaseMigration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var migrationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var migration = new FakeMigration("001_Finishing", async (_, _, _) =>
        {
            migrationStarted.SetResult();
            await releaseMigration.Task;
        });

        var sut = new MongoMigrationRunner([migration], helper, options, logger, timeProvider);
        using var callerCts = new CancellationTokenSource();

        // Act
        var runTask = sut.RunMigrationsAsync(callerCts.Token);
        await migrationStarted.Task;
        timeProvider.Advance(leaseTime / 2);
        await extensionStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        releaseMigration.SetResult();
        await WaitForAsync(() => historyCollection.InsertedItems.Count > 0);
        await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromMilliseconds(100)));
        await callerCts.CancelAsync();
        var result = await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        extensionCancelled.Should().BeTrue();
        result.AppliedMigrationIds.Should().Equal("001_Finishing");
    }

    [Test]
    public async Task RunMigrationsAsync_WithExtensionInFlightWhenMigrationsFinish_WaitsForItBeforeReleasing()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var leaseTime = TimeSpan.FromMinutes(10);
        var extensionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var extensionResult = new TaskCompletionSource<DateTime?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInvoked = false;

        var mongoLock = CreateMongoLock(timeProvider,
                                        leaseTime,
                                        async (_, ct) =>
                                        {
                                            extensionStarted.SetResult();
                                            return await extensionResult.Task.WaitAsync(ct);
                                        },
                                        () =>
                                        {
                                            releaseInvoked = true;
                                            return Task.CompletedTask;
                                        });

        var historyCollection = new FakeHistoryCollection();
        var helper = new FakeMongoHelper((_, _, _) => Task.FromResult<IMongoLock?>(mongoLock), historyCollection);
        var options = Options.Create(new MongoOptions
        {
            MigrationLockLeaseTime = leaseTime,
            UseTransactionsForMigrationsIfAvailable = false
        });
        var logger = new NUnitTestLogger<MongoMigrationRunner>();

        var releaseMigration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var migrationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var migration = new FakeMigration("001_Finishing", async (_, _, ct) =>
        {
            migrationStarted.SetResult();
            await releaseMigration.Task.WaitAsync(ct);
        });

        var sut = new MongoMigrationRunner([migration], helper, options, logger, timeProvider);

        // Act
        var runTask = sut.RunMigrationsAsync(CancellationToken.None);
        await migrationStarted.Task;
        timeProvider.Advance(leaseTime / 2);
        await extensionStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        releaseMigration.SetResult();
        await WaitForAsync(() => historyCollection.InsertedItems.Count > 0);

        // The run must not finish while the extension is in flight. Cancelling the extension on shutdown would let it.
        await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromMilliseconds(200)));
        var finishedDuringExtension = runTask.IsCompleted;

        var extendedUntilUtc = timeProvider.GetUtcNow().Add(leaseTime).UtcDateTime;
        extensionResult.SetResult(extendedUntilUtc);
        var result = await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        finishedDuringExtension.Should().BeFalse();
        releaseInvoked.Should().BeTrue();
        result.AppliedMigrationIds.Should().Equal("001_Finishing");
    }

    [Test]
    public async Task RunMigrationsAsync_WithExtensionStalledPastLeaseExpiry_StopsInProgressMigrationAndDoesNotRecordIt()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var leaseTime = TimeSpan.FromMinutes(10);
        var extensionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var mongoLock = CreateMongoLock(timeProvider, leaseTime, async (_, ct) =>
        {
            extensionStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return null;
        });

        var historyCollection = new FakeHistoryCollection();
        var helper = new FakeMongoHelper((_, _, _) => Task.FromResult<IMongoLock?>(mongoLock), historyCollection);
        var options = Options.Create(new MongoOptions
        {
            MigrationLockLeaseTime = leaseTime,
            UseTransactionsForMigrationsIfAvailable = false
        });
        var logger = new NUnitTestLogger<MongoMigrationRunner>();

        var migrationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var migration = new FakeMigration("001_Blocked", async (_, _, ct) =>
        {
            migrationStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });

        var sut = new MongoMigrationRunner([migration], helper, options, logger, timeProvider);

        // Act
        var runTask = sut.RunMigrationsAsync(CancellationToken.None);
        await migrationStarted.Task;
        timeProvider.Advance(leaseTime / 2);
        await extensionStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        timeProvider.Advance(leaseTime / 2);

        var act = async () => await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        historyCollection.InsertedItems.Should().BeEmpty();
    }

    [Test]
    public async Task RunMigrationsAsync_WithHistoryReadBlockedPastLeaseExpiry_StopsBeforeApplyingAnyMigration()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var leaseTime = TimeSpan.FromMinutes(10);

        // Every extension is refused, simulating a lease another instance has already taken over.
        var mongoLock = CreateMongoLock(timeProvider, leaseTime, (_, _) => Task.FromResult<DateTime?>(null));

        var historyReadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var historyReadObservedCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var historyCollection = new FakeHistoryCollection(onFind: async ct =>
        {
            historyReadStarted.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                historyReadObservedCancellation.TrySetResult();
                throw;
            }
        });

        var helper = new FakeMongoHelper((_, _, _) => Task.FromResult<IMongoLock?>(mongoLock), historyCollection);
        var options = Options.Create(new MongoOptions
        {
            MigrationLockLeaseTime = leaseTime,
            UseTransactionsForMigrationsIfAvailable = false
        });
        var logger = new NUnitTestLogger<MongoMigrationRunner>();
        var migration = new FakeMigration("001_NeverReached");

        var sut = new MongoMigrationRunner([migration], helper, options, logger, timeProvider);

        // Act: the history read blocks past the first renewal attempt. This only reaches the pending history
        // read because the renewal loop is started immediately after acquiring the lock, before that read - not
        // after it completes.
        var runTask = sut.RunMigrationsAsync(CancellationToken.None);
        await historyReadStarted.Task;
        timeProvider.Advance(leaseTime / 2);
        await historyReadObservedCancellation.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var act = async () => await runTask;

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        migration.ApplyCallCount.Should().Be(0);
    }

    [Test]
    public async Task RunMigrationsAsync_WithLeaseExtensionThrowing_StopsInProgressMigrationAndPrefersMigrationCancellation()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var leaseTime = TimeSpan.FromMinutes(10);

        var mongoLock = CreateMongoLock(timeProvider, leaseTime, (_, _) => throw new InvalidOperationException("Lease extension failed unexpectedly"));

        var historyCollection = new FakeHistoryCollection();
        var helper = new FakeMongoHelper((_, _, _) => Task.FromResult<IMongoLock?>(mongoLock), historyCollection);
        var options = Options.Create(new MongoOptions
        {
            MigrationLockLeaseTime = leaseTime,
            UseTransactionsForMigrationsIfAvailable = false
        });
        var logger = new NUnitTestLogger<MongoMigrationRunner>();

        var migrationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var migrationObservedCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var migration = new FakeMigration("001_Blocked", async (_, _, ct) =>
        {
            migrationStarted.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                migrationObservedCancellation.TrySetResult();
                throw;
            }
        });

        var sut = new MongoMigrationRunner([migration], helper, options, logger, timeProvider);

        // Act
        var runTask = sut.RunMigrationsAsync(CancellationToken.None);
        await migrationStarted.Task;
        timeProvider.Advance(leaseTime / 2);
        await migrationObservedCancellation.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var act = async () => await runTask;

        // Assert: the migration's own OperationCanceledException takes precedence over the renewal loop's
        // InvalidOperationException, per the plan's documented exception-priority rule.
        await act.Should().ThrowAsync<OperationCanceledException>();
        historyCollection.InsertedItems.Should().BeEmpty();
    }

    [Test]
    public async Task RunMigrationsAsync_WithLockAlreadyHeldAndZeroWaitTimeout_ReturnsSkippedResultWithoutRetrying()
    {
        // Arrange
        var helper = new Mock<IMongoHelper>(MockBehavior.Strict);
        var database = new Mock<IMongoDatabase>(MockBehavior.Strict);
        var historyCollection = new Mock<IMongoCollection<MongoMigrationHistoryItem>>(MockBehavior.Strict);
        var migration = new Mock<IMongoMigration>(MockBehavior.Strict);
        var options = Options.Create(new MongoOptions
        {
            MigrationLockWaitTimeout = TimeSpan.Zero
        });
        var logger = new NUnitTestLogger<MongoMigrationRunner>();
        var timeProvider = new FakeTimeProvider();

        helper.SetupGet(x => x.Database).Returns(database.Object);
        database.Setup(x => x.GetCollection<MongoMigrationHistoryItem>(It.IsAny<String>(), null))
                .Returns(historyCollection.Object);

        helper.Setup(x => x.TryAcquireLockAsync(It.IsAny<String>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((IMongoLock?)null); // Lock is held by another process

        migration.SetupGet(x => x.Id).Returns("001_TestMigration");

        var sut = new MongoMigrationRunner([migration.Object], helper.Object, options, logger, timeProvider);

        // Act
        var result = await sut.RunMigrationsAsync(CancellationToken.None);

        // Assert
        result.Skipped.Should().BeTrue();
        result.AppliedMigrationIds.Should().BeEmpty();
        migration.Verify(x => x.ApplyAsync(It.IsAny<IMongoHelper>(), It.IsAny<IClientSessionHandle>(), It.IsAny<CancellationToken>()), Times.Never);
        helper.Verify(x => x.TryAcquireLockAsync(It.IsAny<String>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task RunMigrationsAsync_WithMigrationLongerThanLease_RenewsLeaseAndCompletes()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var leaseTime = TimeSpan.FromMinutes(10);
        var extendCallCount = 0;

        var mongoLock = CreateMongoLock(timeProvider, leaseTime, (duration, _) =>
        {
            Interlocked.Increment(ref extendCallCount);
            return Task.FromResult<DateTime?>(timeProvider.GetUtcNow().Add(duration).UtcDateTime);
        });

        var historyCollection = new FakeHistoryCollection();
        var helper = new FakeMongoHelper((_, _, _) => Task.FromResult<IMongoLock?>(mongoLock), historyCollection);
        var options = Options.Create(new MongoOptions
        {
            MigrationLockLeaseTime = leaseTime,
            UseTransactionsForMigrationsIfAvailable = false
        });
        var logger = new NUnitTestLogger<MongoMigrationRunner>();

        var migrationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMigration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var migration = new FakeMigration("001_LongRunning", async (_, _, ct) =>
        {
            migrationStarted.SetResult();
            await releaseMigration.Task.WaitAsync(ct);
        });

        var sut = new MongoMigrationRunner([migration], helper, options, logger, timeProvider);

        // Act
        var runTask = sut.RunMigrationsAsync(CancellationToken.None);
        await migrationStarted.Task;

        for (var i = 0; i < 4; i++)
        {
            timeProvider.Advance(leaseTime / 2);
            await WaitForAsync(() => extendCallCount > i);
        }

        releaseMigration.SetResult();
        var result = await runTask;

        // Assert
        result.Skipped.Should().BeFalse();
        result.AppliedMigrationIds.Should().Equal("001_LongRunning");
        extendCallCount.Should().BeGreaterThanOrEqualTo(4);
        historyCollection.InsertedItems.Should().ContainSingle(x => x.Id == "001_LongRunning");
    }

    [Test]
    public async Task RunMigrationsAsync_WithPositiveWaitTimeoutAndLockNeverAvailable_ReturnsSkippedResultAfterTimeout()
    {
        // Arrange
        var helper = new FakeMongoHelper((_, _, _) => Task.FromResult<IMongoLock?>(null));
        var options = Options.Create(new MongoOptions
        {
            MigrationLockWaitTimeout = TimeSpan.FromMilliseconds(50)
        });
        var logger = new NUnitTestLogger<MongoMigrationRunner>();
        var timeProvider = new FakeTimeProvider();
        var migration = new FakeMigration("001_TestMigration");

        var sut = new MongoMigrationRunner([migration], helper, options, logger, timeProvider);

        // Act
        var result = await sut.RunMigrationsAsync(CancellationToken.None);

        // Assert
        result.Skipped.Should().BeTrue();
        result.AppliedMigrationIds.Should().BeEmpty();
        migration.ApplyCallCount.Should().Be(0);
    }

    [Test]
    public async Task RunMigrationsAsync_WithRefusedLeaseExtension_StopsInProgressMigrationAndDoesNotRecordIt()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var leaseTime = TimeSpan.FromMinutes(10);

        var mongoLock = CreateMongoLock(timeProvider, leaseTime, (_, _) => Task.FromResult<DateTime?>(null)); // every extension is refused

        var historyCollection = new FakeHistoryCollection();
        var helper = new FakeMongoHelper((_, _, _) => Task.FromResult<IMongoLock?>(mongoLock), historyCollection);
        var options = Options.Create(new MongoOptions
        {
            MigrationLockLeaseTime = leaseTime,
            UseTransactionsForMigrationsIfAvailable = false
        });
        var logger = new NUnitTestLogger<MongoMigrationRunner>();

        var migrationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var migrationObservedCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var migration = new FakeMigration("001_Blocked", async (_, _, ct) =>
        {
            migrationStarted.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                migrationObservedCancellation.TrySetResult();
                throw;
            }
        });

        var sut = new MongoMigrationRunner([migration], helper, options, logger, timeProvider);

        // Act
        var runTask = sut.RunMigrationsAsync(CancellationToken.None);
        await migrationStarted.Task;
        timeProvider.Advance(leaseTime / 2);
        await migrationObservedCancellation.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var act = async () => await runTask;

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        historyCollection.InsertedItems.Should().BeEmpty();
    }

    [Test]
    public async Task RunMigrationsAsync_WithSeveralShortMigrationsExceedingHalfLeaseCumulatively_RenewsLeaseAndCompletes()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var leaseTime = TimeSpan.FromMinutes(10);
        var extendCallCount = 0;

        var mongoLock = CreateMongoLock(timeProvider, leaseTime, (duration, _) =>
        {
            Interlocked.Increment(ref extendCallCount);
            return Task.FromResult<DateTime?>(timeProvider.GetUtcNow().Add(duration).UtcDateTime);
        });

        var historyCollection = new FakeHistoryCollection();
        var helper = new FakeMongoHelper((_, _, _) => Task.FromResult<IMongoLock?>(mongoLock), historyCollection);
        var options = Options.Create(new MongoOptions
        {
            MigrationLockLeaseTime = leaseTime,
            UseTransactionsForMigrationsIfAvailable = false
        });
        var logger = new NUnitTestLogger<MongoMigrationRunner>();

        // Each migration individually advances time by less than half the lease, but there are enough of them
        // that the cumulative advance exceeds half the lease several times over, proving the single run-scoped
        // renewal loop (not a per-migration one) is what keeps the lock alive.
        var halfLeaseFraction = leaseTime / 4;
        var migrations = Enumerable.Range(1, 9)
                                   .Select(i => new FakeMigration($"{i:000}_Short", async (_, _, _) =>
                                   {
                                       timeProvider.Advance(halfLeaseFraction);
                                       await WaitForAsync(() => true); // yield so the background renewal loop can observe the advance
                                   }))
                                   .ToArray();

        var sut = new MongoMigrationRunner(migrations, helper, options, logger, timeProvider);

        // Act
        var result = await sut.RunMigrationsAsync(CancellationToken.None);

        // Assert
        result.Skipped.Should().BeFalse();
        result.AppliedMigrationIds.Should().HaveCount(9);
        extendCallCount.Should().BeGreaterThanOrEqualTo(4);
        historyCollection.InsertedItems.Should().HaveCount(9);
    }

    [Test]
    public async Task RunMigrationsAsync_WithSlowLeaseAcquisition_RenewsBeforeRemainingLeaseExpires()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var leaseTime = TimeSpan.FromMinutes(10);
        var extendCallCount = 0;

        var mongoLock = CreateMongoLock(timeProvider, leaseTime, (duration, _) =>
        {
            Interlocked.Increment(ref extendCallCount);
            return Task.FromResult<DateTime?>(timeProvider.GetUtcNow().Add(duration).UtcDateTime);
        });

        var historyCollection = new FakeHistoryCollection();

        // The acquisition round trip consumes 6 of the lease's 10 minutes, leaving 4, which is less than half the lease.
        var helper = new FakeMongoHelper((_, _, _) =>
        {
            timeProvider.Advance(TimeSpan.FromMinutes(6));
            return Task.FromResult<IMongoLock?>(mongoLock);
        }, historyCollection);
        var options = Options.Create(new MongoOptions
        {
            MigrationLockLeaseTime = leaseTime,
            UseTransactionsForMigrationsIfAvailable = false
        });
        var logger = new NUnitTestLogger<MongoMigrationRunner>();

        var migrationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMigration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var migration = new FakeMigration("001_LongRunning", async (_, _, ct) =>
        {
            migrationStarted.SetResult();
            await releaseMigration.Task.WaitAsync(ct);
        });

        var sut = new MongoMigrationRunner([migration], helper, options, logger, timeProvider);

        // Act: half of the remaining 4 minutes elapses, well before a fixed half-lease delay of 5 minutes would.
        var runTask = sut.RunMigrationsAsync(CancellationToken.None);
        await migrationStarted.Task;
        timeProvider.Advance(TimeSpan.FromMinutes(2));
        await WaitForAsync(() => extendCallCount > 0);

        releaseMigration.SetResult();
        var result = await runTask;

        // Assert
        extendCallCount.Should().BeGreaterThanOrEqualTo(1);
        result.AppliedMigrationIds.Should().Equal("001_LongRunning");
    }

    [Test]
    public async Task RunMigrationsAsync_WithSlowLeaseExtension_RenewsBeforeRemainingLeaseExpires()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var leaseTime = TimeSpan.FromMinutes(10);
        var extendCallCount = 0;

        var mongoLock = CreateMongoLock(timeProvider, leaseTime, (duration, _) =>
        {
            var validUntilUtc = timeProvider.GetUtcNow().Add(duration).UtcDateTime;

            // The first extension's round trip consumes 6 of the new lease's 10 minutes, leaving 4.
            if (Interlocked.Increment(ref extendCallCount) == 1)
            {
                timeProvider.Advance(TimeSpan.FromMinutes(6));
            }

            return Task.FromResult<DateTime?>(validUntilUtc);
        });

        var historyCollection = new FakeHistoryCollection();
        var helper = new FakeMongoHelper((_, _, _) => Task.FromResult<IMongoLock?>(mongoLock), historyCollection);
        var options = Options.Create(new MongoOptions
        {
            MigrationLockLeaseTime = leaseTime,
            UseTransactionsForMigrationsIfAvailable = false
        });
        var logger = new NUnitTestLogger<MongoMigrationRunner>();

        var migrationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMigration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var migration = new FakeMigration("001_LongRunning", async (_, _, ct) =>
        {
            migrationStarted.SetResult();
            await releaseMigration.Task.WaitAsync(ct);
        });

        var sut = new MongoMigrationRunner([migration], helper, options, logger, timeProvider);

        // Act
        var runTask = sut.RunMigrationsAsync(CancellationToken.None);
        await migrationStarted.Task;
        timeProvider.Advance(leaseTime / 2);
        await WaitForAsync(() => extendCallCount > 0);

        // Half of the remaining 4 minutes elapses, well before a fixed half-lease delay of 5 minutes would.
        timeProvider.Advance(TimeSpan.FromMinutes(2));
        await WaitForAsync(() => extendCallCount > 1);

        releaseMigration.SetResult();
        var result = await runTask;

        // Assert
        extendCallCount.Should().BeGreaterThanOrEqualTo(2);
        result.AppliedMigrationIds.Should().Equal("001_LongRunning");
    }

    [Test]
    public async Task RunMigrationsAsync_WithTransaction_ChecksOwnershipOnTheMigrationSessionBeforeApplyingAndCommitting()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var leaseTime = TimeSpan.FromMinutes(10);
        var events = new List<String>();
        IClientSessionHandle? fencedSession = null;
        var mongoLock = CreateFencedMongoLock(timeProvider,
                                              leaseTime,
                                              (session, _) =>
                                              {
                                                  events.Add("fence");
                                                  fencedSession = session;
                                                  return Task.FromResult(true);
                                              });
        var client = new FakeMongoClient();
        var historyCollection = new FakeHistoryCollection();
        var helper = new FakeMongoHelper((_, _, _) => Task.FromResult<IMongoLock?>(mongoLock), historyCollection, client);
        var options = Options.Create(new MongoOptions
        {
            MigrationLockLeaseTime = leaseTime,
            UseTransactionsForMigrationsIfAvailable = true
        });
        var migration = new FakeMigration("001_Fenced",
                                          (_, session, _) =>
                                          {
                                              events.Add("apply");
                                              session.Should().BeSameAs(fencedSession);
                                              return Task.CompletedTask;
                                          });
        var sut = new MongoMigrationRunner([migration], helper, options, new NUnitTestLogger<MongoMigrationRunner>(), timeProvider);

        // Act
        var result = await sut.RunMigrationsAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        result.AppliedMigrationIds.Should().Equal("001_Fenced");
        events.Should().Equal("fence", "apply");
        var session = client.Sessions.Should().ContainSingle().Subject;
        fencedSession.Should().BeSameAs(session);
        session.CommitCount.Should().Be(1);
        session.AbortCount.Should().Be(0);
        historyCollection.InsertedItems.Should().ContainSingle();
    }

    [Test]
    public async Task RunMigrationsAsync_WithOwnershipCheckFindingNoMatch_StopsAsLockLossWithoutApplyingOrRecording()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var leaseTime = TimeSpan.FromMinutes(10);

        // The lease is still valid on the client, which is exactly the stalled runner's view of the world.
        var mongoLock = CreateFencedMongoLock(timeProvider, leaseTime, (_, _) => Task.FromResult(false));
        var client = new FakeMongoClient();
        var historyCollection = new FakeHistoryCollection();
        var helper = new FakeMongoHelper((_, _, _) => Task.FromResult<IMongoLock?>(mongoLock), historyCollection, client);
        var options = Options.Create(new MongoOptions
        {
            MigrationLockLeaseTime = leaseTime,
            UseTransactionsForMigrationsIfAvailable = true
        });
        var logger = new NUnitTestLogger<MongoMigrationRunner>(captureMessages: true);
        var migration = new FakeMigration("001_NeverReached");
        var sut = new MongoMigrationRunner([migration], helper, options, logger, timeProvider);

        // Act
        var act = async () => await sut.RunMigrationsAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        migration.ApplyCallCount.Should().Be(0);
        historyCollection.InsertedItems.Should().BeEmpty();
        var session = client.Sessions.Should().ContainSingle().Subject;
        session.AbortCount.Should().Be(1);
        session.CommitCount.Should().Be(0);
        logger.LogMessages.Should().Contain(message => message.LogLevel == LogLevel.Warning && message.Message.Contains("no longer held"));
        logger.LogMessages.Should().NotContain(message => message.LogLevel == LogLevel.Error);
    }

    [Test]
    public async Task RunMigrationsAsync_WithOwnershipCheckSucceedingAfterLeaseExpiry_StopsAsLockLossWithoutApplyingOrRecording()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var leaseTime = TimeSpan.FromMinutes(10);

        // The write matched on the server while the lease was live, but its reply arrives after the lease has expired
        // locally. The fence runs while the lock holds its extension semaphore, so no extension can complete in the
        // meantime and only the client-side guard right after the write can detect the loss.
        var mongoLock = new MongoLock("migration-lock",
                                      timeProvider.GetUtcNow().Add(leaseTime).UtcDateTime,
                                      leaseTime,
                                      timeProvider,
                                      () => Task.CompletedTask,
                                      (_, _) => throw new InvalidOperationException("No extension should complete after the lease expired."),
                                      (_, _) =>
                                      {
                                          timeProvider.Advance(leaseTime);
                                          return Task.FromResult(true);
                                      });
        var client = new FakeMongoClient();
        var historyCollection = new FakeHistoryCollection();
        var helper = new FakeMongoHelper((_, _, _) => Task.FromResult<IMongoLock?>(mongoLock), historyCollection, client);
        var options = Options.Create(new MongoOptions
        {
            MigrationLockLeaseTime = leaseTime,
            UseTransactionsForMigrationsIfAvailable = true
        });
        var logger = new NUnitTestLogger<MongoMigrationRunner>(captureMessages: true);
        var migration = new FakeMigration("001_NeverReached");
        var sut = new MongoMigrationRunner([migration], helper, options, logger, timeProvider);

        // Act
        var act = async () => await sut.RunMigrationsAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        migration.ApplyCallCount.Should().Be(0);
        historyCollection.InsertedItems.Should().BeEmpty();
        var session = client.Sessions.Should().ContainSingle().Subject;
        session.AbortCount.Should().Be(1);
        session.CommitCount.Should().Be(0);
        logger.LogMessages.Should().Contain(message => message.LogLevel == LogLevel.Warning && message.Message.Contains("is no longer valid"));
        logger.LogMessages.Should().NotContain(message => message.LogLevel == LogLevel.Error);
    }

    [Test]
    public async Task RunMigrationsAsync_WithOwnershipCheckFailingWithWriteConflict_StopsAsLockLossAndDoesNotRetry()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var leaseTime = TimeSpan.FromMinutes(10);
        var fenceCallCount = 0;
        var mongoLock = CreateFencedMongoLock(timeProvider,
                                              leaseTime,
                                              (_, _) =>
                                              {
                                                  fenceCallCount++;
                                                  throw CreateWriteConflict();
                                              });
        var client = new FakeMongoClient();
        var historyCollection = new FakeHistoryCollection();
        var helper = new FakeMongoHelper((_, _, _) => Task.FromResult<IMongoLock?>(mongoLock), historyCollection, client);
        var options = Options.Create(new MongoOptions
        {
            MigrationLockLeaseTime = leaseTime,
            UseTransactionsForMigrationsIfAvailable = true
        });
        var logger = new NUnitTestLogger<MongoMigrationRunner>(captureMessages: true);
        var migration = new FakeMigration("001_NeverReached");
        var sut = new MongoMigrationRunner([migration], helper, options, logger, timeProvider);

        // Act
        var act = async () => await sut.RunMigrationsAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        fenceCallCount.Should().Be(1);
        migration.ApplyCallCount.Should().Be(0);
        historyCollection.InsertedItems.Should().BeEmpty();
        client.Sessions.Should().ContainSingle().Which.AbortCount.Should().Be(1);
        logger.LogMessages.Should().Contain(message => message.LogLevel == LogLevel.Warning);
        logger.LogMessages.Should().NotContain(message => message.LogLevel == LogLevel.Error);
    }

    [Test]
    public async Task RunMigrationsAsync_WithOwnershipCheckFailingWithOtherException_FailsMigrationWithoutReportingLockLoss()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var leaseTime = TimeSpan.FromMinutes(10);
        var failure = new InvalidOperationException("The server is not available.");
        var mongoLock = CreateFencedMongoLock(timeProvider, leaseTime, (_, _) => throw failure);
        var client = new FakeMongoClient();
        var historyCollection = new FakeHistoryCollection();
        var helper = new FakeMongoHelper((_, _, _) => Task.FromResult<IMongoLock?>(mongoLock), historyCollection, client);
        var options = Options.Create(new MongoOptions
        {
            MigrationLockLeaseTime = leaseTime,
            UseTransactionsForMigrationsIfAvailable = true
        });
        var logger = new NUnitTestLogger<MongoMigrationRunner>(captureMessages: true);
        var migration = new FakeMigration("001_NeverReached");
        var sut = new MongoMigrationRunner([migration], helper, options, logger, timeProvider);

        // Act
        var act = async () => await sut.RunMigrationsAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
        migration.ApplyCallCount.Should().Be(0);
        historyCollection.InsertedItems.Should().BeEmpty();
        client.Sessions.Should().ContainSingle().Which.AbortCount.Should().Be(1);
        logger.LogMessages.Should().NotContain(message => message.LogLevel == LogLevel.Warning && message.Message.Contains("lock"));
        logger.LogMessages.Should().Contain(message => message.LogLevel == LogLevel.Error);
    }

    [Test]
    public async Task RunMigrationsAsync_WithoutTransactions_DoesNotCheckOwnershipOnTheServer()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var leaseTime = TimeSpan.FromMinutes(10);
        var fenceCallCount = 0;
        var mongoLock = CreateFencedMongoLock(timeProvider,
                                              leaseTime,
                                              (_, _) =>
                                              {
                                                  fenceCallCount++;
                                                  return Task.FromResult(false);
                                              });
        var historyCollection = new FakeHistoryCollection();
        var helper = new FakeMongoHelper((_, _, _) => Task.FromResult<IMongoLock?>(mongoLock), historyCollection);
        var options = Options.Create(new MongoOptions
        {
            MigrationLockLeaseTime = leaseTime,
            UseTransactionsForMigrationsIfAvailable = false
        });
        var sut = new MongoMigrationRunner([new FakeMigration("001_Plain")], helper, options, new NUnitTestLogger<MongoMigrationRunner>(), timeProvider);

        // Act
        var result = await sut.RunMigrationsAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        result.AppliedMigrationIds.Should().Equal("001_Plain");
        fenceCallCount.Should().Be(0);
    }

    [Test]
    public async Task RunMigrationsAsync_WithServerWithoutTransactionSupport_DoesNotCheckOwnershipOnTheServer()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var leaseTime = TimeSpan.FromMinutes(10);
        var fenceCallCount = 0;
        var mongoLock = CreateFencedMongoLock(timeProvider,
                                              leaseTime,
                                              (_, _) =>
                                              {
                                                  fenceCallCount++;
                                                  return Task.FromResult(false);
                                              });
        // Sessions are not supported, so TryStartTransactionAsync reports no transaction.
        var client = new FakeMongoClient(() => throw new MongoClientException("Sessions are not supported."));
        var historyCollection = new FakeHistoryCollection();
        var helper = new FakeMongoHelper((_, _, _) => Task.FromResult<IMongoLock?>(mongoLock), historyCollection, client);
        var options = Options.Create(new MongoOptions
        {
            MigrationLockLeaseTime = leaseTime,
            UseTransactionsForMigrationsIfAvailable = true
        });
        var sut = new MongoMigrationRunner([new FakeMigration("001_Plain")], helper, options, new NUnitTestLogger<MongoMigrationRunner>(), timeProvider);

        // Act
        var result = await sut.RunMigrationsAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        result.AppliedMigrationIds.Should().Equal("001_Plain");
        fenceCallCount.Should().Be(0);
        client.Sessions.Should().BeEmpty();
    }

    [Test]
    public async Task RunMigrationsAsync_WithLockWithoutOwnershipCheckInTransaction_AppliesAndCommitsAsBefore()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var leaseTime = TimeSpan.FromMinutes(10);
        var mongoLock = new PlainLock(timeProvider.GetUtcNow().Add(leaseTime).UtcDateTime);
        var client = new FakeMongoClient();
        var historyCollection = new FakeHistoryCollection();
        var helper = new FakeMongoHelper((_, _, _) => Task.FromResult<IMongoLock?>(mongoLock), historyCollection, client);
        var options = Options.Create(new MongoOptions
        {
            MigrationLockLeaseTime = leaseTime,
            UseTransactionsForMigrationsIfAvailable = true
        });
        var sut = new MongoMigrationRunner([new FakeMigration("001_Custom")], helper, options, new NUnitTestLogger<MongoMigrationRunner>(), timeProvider);

        // Act
        var result = await sut.RunMigrationsAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        result.AppliedMigrationIds.Should().Equal("001_Custom");
        client.Sessions.Should().ContainSingle().Which.CommitCount.Should().Be(1);
    }

    private static MongoLock CreateFencedMongoLock(TimeProvider timeProvider,
                                                   TimeSpan leaseTime,
                                                   Func<IClientSessionHandle, CancellationToken, Task<Boolean>> fenceAction) =>
        new("migration-lock",
            timeProvider.GetUtcNow().Add(leaseTime).UtcDateTime,
            leaseTime,
            timeProvider,
            () => Task.CompletedTask,
            (_, _) => Task.FromResult<DateTime?>(timeProvider.GetUtcNow().Add(leaseTime).UtcDateTime),
            fenceAction);

    private static MongoCommandException CreateWriteConflict()
    {
        var exception = new MongoCommandException(new ConnectionId(new ServerId(new ClusterId(), new DnsEndPoint("localhost", 27017))),
                                                  "WriteConflict",
                                                  new BsonDocument("find", "lock"),
                                                  new BsonDocument
                                                  {
                                                      { "ok", 0 },
                                                      { "code", 112 },
                                                      { "codeName", "WriteConflict" }
                                                  });
        exception.AddErrorLabel("TransientTransactionError");
        return exception;
    }

    private static MongoLock CreateMongoLock(TimeProvider timeProvider,
                                             TimeSpan leaseTime,
                                             Func<TimeSpan, CancellationToken, Task<DateTime?>> extendAction,
                                             Func<Task>? releaseAction = null) =>
        new("migration-lock",
            timeProvider.GetUtcNow().Add(leaseTime).UtcDateTime,
            leaseTime,
            timeProvider,
            releaseAction ?? (() => Task.CompletedTask),
            extendAction);

    private static Boolean ContainsException(Exception? exception, Exception expected)
        => exception is not null &&
           (ReferenceEquals(exception, expected) ||
            (exception is AggregateException aggregate && aggregate.Flatten().InnerExceptions.Any(inner => ReferenceEquals(inner, expected))));

    private static MongoLock CreateExpiredMongoLock(TimeProvider timeProvider) =>
        new("migration-lock",
            timeProvider.GetUtcNow().UtcDateTime.AddMilliseconds(-1),
            TimeSpan.FromMinutes(10),
            timeProvider,
            () => Task.CompletedTask,
            (_, _) => throw new InvalidOperationException("No extension should be attempted by the guard."));

    private static MongoMigrationRunner CreateGuardTestRunner(TimeProvider timeProvider, NUnitTestLogger<MongoMigrationRunner> logger)
        => new([], new ThrowingMongoHelper(), Options.Create(new MongoOptions()), logger, timeProvider);

    private static async Task WaitForAsync(Func<Boolean> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }
    }

    /// <summary>
    /// Single-batch hand-crafted <see cref="IAsyncCursor{TDocument}"/> that yields its items once.
    /// </summary>
    private sealed class FakeAsyncCursor<TDocument> : IAsyncCursor<TDocument>
    {
        private readonly IReadOnlyList<TDocument> _items;
        private Boolean _consumed;

        /// <param name="items">The items to yield on the first <see cref="MoveNext"/>/<see cref="MoveNextAsync"/> call.</param>
        public FakeAsyncCursor(IReadOnlyList<TDocument> items) => _items = items;

        public IEnumerable<TDocument> Current => _consumed ? _items : [];

        public Boolean MoveNext(CancellationToken cancellationToken = default)
        {
            if (_consumed)
            {
                return false;
            }

            _consumed = true;
            return true;
        }

        public Task<Boolean> MoveNextAsync(CancellationToken cancellationToken = default) => Task.FromResult(MoveNext(cancellationToken));

        public void Dispose() { }
    }

    /// <summary>
    /// Hand-crafted recording double for <see cref="IMongoCollection{TDocument}"/> of
    /// <see cref="MongoMigrationHistoryItem"/>. Only the members <see cref="MongoMigrationRunner"/> actually calls
    /// — the no-session <c>Find().Project().ToListAsync()</c> chain (which routes through the single-document
    /// <see
    ///     cref="FindAsync{TProjection}(FilterDefinition{MongoMigrationHistoryItem}, FindOptions{MongoMigrationHistoryItem, TProjection}, CancellationToken)"/>
    /// overload) and both <c>InsertOneAsync</c> overloads — are implemented; every other member is unreachable and
    /// throws deliberately.
    /// </summary>
    private sealed class FakeHistoryCollection : IMongoCollection<MongoMigrationHistoryItem>
    {
        private readonly IReadOnlyList<MongoMigrationHistoryItem> _initialItems;
        private readonly List<MongoMigrationHistoryItem> _insertedItems = [];
        private readonly Func<CancellationToken, Task>? _onFind;
        private readonly Func<CancellationToken, Task>? _onInsert;

        public FakeHistoryCollection(IReadOnlyList<MongoMigrationHistoryItem>? initialItems = null,
                                     Func<CancellationToken, Task>? onFind = null,
                                     Func<CancellationToken, Task>? onInsert = null)
        {
            _initialItems = initialItems ?? [];
            _onFind = onFind;
            _onInsert = onInsert;
        }

        public IReadOnlyList<MongoMigrationHistoryItem> InsertedItems => _insertedItems;

        public CollectionNamespace CollectionNamespace => throw new NotSupportedException();
        public IMongoDatabase Database => throw new NotSupportedException();
        public IBsonSerializer<MongoMigrationHistoryItem> DocumentSerializer => throw new NotSupportedException();
        public IMongoIndexManager<MongoMigrationHistoryItem> Indexes => throw new NotSupportedException();
        public IMongoSearchIndexManager SearchIndexes => throw new NotSupportedException();
        public MongoCollectionSettings Settings => throw new NotSupportedException();

        public IAsyncCursor<TResult> Aggregate<TResult>(PipelineDefinition<MongoMigrationHistoryItem, TResult> pipeline, AggregateOptions? options = null,
                                                        CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncCursor<TResult> Aggregate<TResult>(IClientSessionHandle session, PipelineDefinition<MongoMigrationHistoryItem, TResult> pipeline,
                                                        AggregateOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IAsyncCursor<TResult>> AggregateAsync<TResult>(PipelineDefinition<MongoMigrationHistoryItem, TResult> pipeline, AggregateOptions? options = null,
                                                                   CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IAsyncCursor<TResult>> AggregateAsync<TResult>(IClientSessionHandle session, PipelineDefinition<MongoMigrationHistoryItem, TResult> pipeline,
                                                                   AggregateOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void AggregateToCollection<TResult>(PipelineDefinition<MongoMigrationHistoryItem, TResult> pipeline, AggregateOptions? options = null,
                                                   CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void AggregateToCollection<TResult>(IClientSessionHandle session, PipelineDefinition<MongoMigrationHistoryItem, TResult> pipeline, AggregateOptions? options = null,
                                                   CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task AggregateToCollectionAsync<TResult>(PipelineDefinition<MongoMigrationHistoryItem, TResult> pipeline, AggregateOptions? options = null,
                                                        CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task AggregateToCollectionAsync<TResult>(IClientSessionHandle session, PipelineDefinition<MongoMigrationHistoryItem, TResult> pipeline,
                                                        AggregateOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public BulkWriteResult<MongoMigrationHistoryItem> BulkWrite(IEnumerable<WriteModel<MongoMigrationHistoryItem>> requests, BulkWriteOptions? options = null,
                                                                    CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public BulkWriteResult<MongoMigrationHistoryItem> BulkWrite(IClientSessionHandle session, IEnumerable<WriteModel<MongoMigrationHistoryItem>> requests,
                                                                    BulkWriteOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BulkWriteResult<MongoMigrationHistoryItem>> BulkWriteAsync(IEnumerable<WriteModel<MongoMigrationHistoryItem>> requests, BulkWriteOptions? options = null,
                                                                               CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<BulkWriteResult<MongoMigrationHistoryItem>> BulkWriteAsync(IClientSessionHandle session, IEnumerable<WriteModel<MongoMigrationHistoryItem>> requests,
                                                                               BulkWriteOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Int64 Count(FilterDefinition<MongoMigrationHistoryItem> filter, CountOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Int64 Count(IClientSessionHandle session, FilterDefinition<MongoMigrationHistoryItem> filter, CountOptions? options = null,
                           CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Int64> CountAsync(FilterDefinition<MongoMigrationHistoryItem> filter, CountOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Int64> CountAsync(IClientSessionHandle session, FilterDefinition<MongoMigrationHistoryItem> filter, CountOptions? options = null,
                                      CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Int64 CountDocuments(FilterDefinition<MongoMigrationHistoryItem> filter, CountOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Int64 CountDocuments(IClientSessionHandle session, FilterDefinition<MongoMigrationHistoryItem> filter, CountOptions? options = null,
                                    CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Int64> CountDocumentsAsync(FilterDefinition<MongoMigrationHistoryItem> filter, CountOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Int64> CountDocumentsAsync(IClientSessionHandle session, FilterDefinition<MongoMigrationHistoryItem> filter, CountOptions? options = null,
                                               CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public DeleteResult DeleteMany(FilterDefinition<MongoMigrationHistoryItem> filter, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public DeleteResult DeleteMany(FilterDefinition<MongoMigrationHistoryItem> filter, DeleteOptions options, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public DeleteResult DeleteMany(IClientSessionHandle session, FilterDefinition<MongoMigrationHistoryItem> filter, DeleteOptions? options = null,
                                       CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DeleteResult> DeleteManyAsync(FilterDefinition<MongoMigrationHistoryItem> filter, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DeleteResult> DeleteManyAsync(FilterDefinition<MongoMigrationHistoryItem> filter, DeleteOptions options, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DeleteResult> DeleteManyAsync(IClientSessionHandle session, FilterDefinition<MongoMigrationHistoryItem> filter, DeleteOptions? options = null,
                                                  CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public DeleteResult DeleteOne(FilterDefinition<MongoMigrationHistoryItem> filter, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public DeleteResult DeleteOne(FilterDefinition<MongoMigrationHistoryItem> filter, DeleteOptions options, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public DeleteResult DeleteOne(IClientSessionHandle session, FilterDefinition<MongoMigrationHistoryItem> filter, DeleteOptions? options = null,
                                      CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DeleteResult> DeleteOneAsync(FilterDefinition<MongoMigrationHistoryItem> filter, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DeleteResult> DeleteOneAsync(FilterDefinition<MongoMigrationHistoryItem> filter, DeleteOptions options, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DeleteResult> DeleteOneAsync(IClientSessionHandle session, FilterDefinition<MongoMigrationHistoryItem> filter, DeleteOptions? options = null,
                                                 CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncCursor<TField> Distinct<TField>(FieldDefinition<MongoMigrationHistoryItem, TField> field, FilterDefinition<MongoMigrationHistoryItem> filter,
                                                     DistinctOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncCursor<TField> Distinct<TField>(IClientSessionHandle session, FieldDefinition<MongoMigrationHistoryItem, TField> field,
                                                     FilterDefinition<MongoMigrationHistoryItem> filter, DistinctOptions? options = null,
                                                     CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IAsyncCursor<TField>> DistinctAsync<TField>(FieldDefinition<MongoMigrationHistoryItem, TField> field, FilterDefinition<MongoMigrationHistoryItem> filter,
                                                                DistinctOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IAsyncCursor<TField>> DistinctAsync<TField>(IClientSessionHandle session, FieldDefinition<MongoMigrationHistoryItem, TField> field,
                                                                FilterDefinition<MongoMigrationHistoryItem> filter, DistinctOptions? options = null,
                                                                CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncCursor<TItem> DistinctMany<TItem>(FieldDefinition<MongoMigrationHistoryItem, IEnumerable<TItem>> field, FilterDefinition<MongoMigrationHistoryItem> filter,
                                                       DistinctOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncCursor<TItem> DistinctMany<TItem>(IClientSessionHandle session, FieldDefinition<MongoMigrationHistoryItem, IEnumerable<TItem>> field,
                                                       FilterDefinition<MongoMigrationHistoryItem> filter, DistinctOptions? options = null,
                                                       CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IAsyncCursor<TItem>> DistinctManyAsync<TItem>(FieldDefinition<MongoMigrationHistoryItem, IEnumerable<TItem>> field,
                                                                  FilterDefinition<MongoMigrationHistoryItem> filter, DistinctOptions? options = null,
                                                                  CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IAsyncCursor<TItem>> DistinctManyAsync<TItem>(IClientSessionHandle session, FieldDefinition<MongoMigrationHistoryItem, IEnumerable<TItem>> field,
                                                                  FilterDefinition<MongoMigrationHistoryItem> filter, DistinctOptions? options = null,
                                                                  CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Int64 EstimatedDocumentCount(EstimatedDocumentCountOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Int64> EstimatedDocumentCountAsync(EstimatedDocumentCountOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<IAsyncCursor<TProjection>> FindAsync<TProjection>(FilterDefinition<MongoMigrationHistoryItem> filter,
                                                                            FindOptions<MongoMigrationHistoryItem, TProjection>? options = null,
                                                                            CancellationToken cancellationToken = default)
        {
            if (_onFind is not null)
            {
                await _onFind(cancellationToken);
            }

            IReadOnlyList<TProjection> projected;
            if (options?.Projection is FindExpressionProjectionDefinition<MongoMigrationHistoryItem, TProjection> expressionProjection)
            {
                var project = expressionProjection.Expression.Compile();
                projected = _initialItems.Select(project).ToList();
            }
            else if (_initialItems is IReadOnlyList<TProjection> sameTypeItems)
            {
                projected = sameTypeItems;
            }
            else
            {
                throw new NotSupportedException("FakeHistoryCollection only supports an empty filter with an optional Project(x => x.Id)-style projection.");
            }

            return new FakeAsyncCursor<TProjection>(projected);
        }

        public Task<IAsyncCursor<TProjection>> FindAsync<TProjection>(IClientSessionHandle session, FilterDefinition<MongoMigrationHistoryItem> filter,
                                                                      FindOptions<MongoMigrationHistoryItem, TProjection>? options = null,
                                                                      CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public TProjection FindOneAndDelete<TProjection>(FilterDefinition<MongoMigrationHistoryItem> filter,
                                                         FindOneAndDeleteOptions<MongoMigrationHistoryItem, TProjection>? options = null,
                                                         CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public TProjection FindOneAndDelete<TProjection>(IClientSessionHandle session, FilterDefinition<MongoMigrationHistoryItem> filter,
                                                         FindOneAndDeleteOptions<MongoMigrationHistoryItem, TProjection>? options = null,
                                                         CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TProjection> FindOneAndDeleteAsync<TProjection>(FilterDefinition<MongoMigrationHistoryItem> filter,
                                                                    FindOneAndDeleteOptions<MongoMigrationHistoryItem, TProjection>? options = null,
                                                                    CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TProjection> FindOneAndDeleteAsync<TProjection>(IClientSessionHandle session, FilterDefinition<MongoMigrationHistoryItem> filter,
                                                                    FindOneAndDeleteOptions<MongoMigrationHistoryItem, TProjection>? options = null,
                                                                    CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public TProjection FindOneAndReplace<TProjection>(FilterDefinition<MongoMigrationHistoryItem> filter, MongoMigrationHistoryItem replacement,
                                                          FindOneAndReplaceOptions<MongoMigrationHistoryItem, TProjection>? options = null,
                                                          CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public TProjection FindOneAndReplace<TProjection>(IClientSessionHandle session, FilterDefinition<MongoMigrationHistoryItem> filter, MongoMigrationHistoryItem replacement,
                                                          FindOneAndReplaceOptions<MongoMigrationHistoryItem, TProjection>? options = null,
                                                          CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TProjection> FindOneAndReplaceAsync<TProjection>(FilterDefinition<MongoMigrationHistoryItem> filter, MongoMigrationHistoryItem replacement,
                                                                     FindOneAndReplaceOptions<MongoMigrationHistoryItem, TProjection>? options = null,
                                                                     CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TProjection> FindOneAndReplaceAsync<TProjection>(IClientSessionHandle session, FilterDefinition<MongoMigrationHistoryItem> filter,
                                                                     MongoMigrationHistoryItem replacement,
                                                                     FindOneAndReplaceOptions<MongoMigrationHistoryItem, TProjection>? options = null,
                                                                     CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public TProjection FindOneAndUpdate<TProjection>(FilterDefinition<MongoMigrationHistoryItem> filter, UpdateDefinition<MongoMigrationHistoryItem> update,
                                                         FindOneAndUpdateOptions<MongoMigrationHistoryItem, TProjection>? options = null,
                                                         CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public TProjection FindOneAndUpdate<TProjection>(IClientSessionHandle session, FilterDefinition<MongoMigrationHistoryItem> filter,
                                                         UpdateDefinition<MongoMigrationHistoryItem> update,
                                                         FindOneAndUpdateOptions<MongoMigrationHistoryItem, TProjection>? options = null,
                                                         CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TProjection> FindOneAndUpdateAsync<TProjection>(FilterDefinition<MongoMigrationHistoryItem> filter, UpdateDefinition<MongoMigrationHistoryItem> update,
                                                                    FindOneAndUpdateOptions<MongoMigrationHistoryItem, TProjection>? options = null,
                                                                    CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TProjection> FindOneAndUpdateAsync<TProjection>(IClientSessionHandle session, FilterDefinition<MongoMigrationHistoryItem> filter,
                                                                    UpdateDefinition<MongoMigrationHistoryItem> update,
                                                                    FindOneAndUpdateOptions<MongoMigrationHistoryItem, TProjection>? options = null,
                                                                    CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncCursor<TProjection> FindSync<TProjection>(FilterDefinition<MongoMigrationHistoryItem> filter,
                                                               FindOptions<MongoMigrationHistoryItem, TProjection>? options = null,
                                                               CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncCursor<TProjection> FindSync<TProjection>(IClientSessionHandle session, FilterDefinition<MongoMigrationHistoryItem> filter,
                                                               FindOptions<MongoMigrationHistoryItem, TProjection>? options = null,
                                                               CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void InsertMany(IEnumerable<MongoMigrationHistoryItem> documents, InsertManyOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void InsertMany(IClientSessionHandle session, IEnumerable<MongoMigrationHistoryItem> documents, InsertManyOptions? options = null,
                               CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task InsertManyAsync(IEnumerable<MongoMigrationHistoryItem> documents, InsertManyOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task InsertManyAsync(IClientSessionHandle session, IEnumerable<MongoMigrationHistoryItem> documents, InsertManyOptions? options = null,
                                    CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void InsertOne(MongoMigrationHistoryItem document, InsertOneOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void InsertOne(IClientSessionHandle session, MongoMigrationHistoryItem document, InsertOneOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task InsertOneAsync(MongoMigrationHistoryItem document, CancellationToken cancellationToken)
        {
            _insertedItems.Add(document);
            if (_onInsert is not null)
            {
                await _onInsert(cancellationToken);
            }
        }

        public async Task InsertOneAsync(MongoMigrationHistoryItem document, InsertOneOptions? options = null, CancellationToken cancellationToken = default)
        {
            _insertedItems.Add(document);
            if (_onInsert is not null)
            {
                await _onInsert(cancellationToken);
            }
        }

        public async Task InsertOneAsync(IClientSessionHandle session, MongoMigrationHistoryItem document, InsertOneOptions? options = null,
                                         CancellationToken cancellationToken = default)
        {
            _insertedItems.Add(document);
            if (_onInsert is not null)
            {
                await _onInsert(cancellationToken);
            }
        }

        public IAsyncCursor<TResult> MapReduce<TResult>(BsonJavaScript map, BsonJavaScript reduce, MapReduceOptions<MongoMigrationHistoryItem, TResult>? options = null,
                                                        CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncCursor<TResult> MapReduce<TResult>(IClientSessionHandle session, BsonJavaScript map, BsonJavaScript reduce,
                                                        MapReduceOptions<MongoMigrationHistoryItem, TResult>? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IAsyncCursor<TResult>> MapReduceAsync<TResult>(BsonJavaScript map, BsonJavaScript reduce, MapReduceOptions<MongoMigrationHistoryItem, TResult>? options = null,
                                                                   CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IAsyncCursor<TResult>> MapReduceAsync<TResult>(IClientSessionHandle session, BsonJavaScript map, BsonJavaScript reduce,
                                                                   MapReduceOptions<MongoMigrationHistoryItem, TResult>? options = null,
                                                                   CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ReplaceOneResult ReplaceOne(FilterDefinition<MongoMigrationHistoryItem> filter, MongoMigrationHistoryItem replacement, ReplaceOptions? options = null,
                                           CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ReplaceOneResult ReplaceOne(FilterDefinition<MongoMigrationHistoryItem> filter, MongoMigrationHistoryItem replacement, UpdateOptions options,
                                           CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ReplaceOneResult ReplaceOne(IClientSessionHandle session, FilterDefinition<MongoMigrationHistoryItem> filter, MongoMigrationHistoryItem replacement,
                                           ReplaceOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ReplaceOneResult ReplaceOne(IClientSessionHandle session, FilterDefinition<MongoMigrationHistoryItem> filter, MongoMigrationHistoryItem replacement,
                                           UpdateOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ReplaceOneResult> ReplaceOneAsync(FilterDefinition<MongoMigrationHistoryItem> filter, MongoMigrationHistoryItem replacement, ReplaceOptions? options = null,
                                                      CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ReplaceOneResult> ReplaceOneAsync(FilterDefinition<MongoMigrationHistoryItem> filter, MongoMigrationHistoryItem replacement, UpdateOptions options,
                                                      CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ReplaceOneResult> ReplaceOneAsync(IClientSessionHandle session, FilterDefinition<MongoMigrationHistoryItem> filter, MongoMigrationHistoryItem replacement,
                                                      ReplaceOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ReplaceOneResult> ReplaceOneAsync(IClientSessionHandle session, FilterDefinition<MongoMigrationHistoryItem> filter, MongoMigrationHistoryItem replacement,
                                                      UpdateOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public UpdateResult UpdateMany(FilterDefinition<MongoMigrationHistoryItem> filter, UpdateDefinition<MongoMigrationHistoryItem> update, UpdateOptions? options = null,
                                       CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public UpdateResult UpdateMany(IClientSessionHandle session, FilterDefinition<MongoMigrationHistoryItem> filter, UpdateDefinition<MongoMigrationHistoryItem> update,
                                       UpdateOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UpdateResult> UpdateManyAsync(FilterDefinition<MongoMigrationHistoryItem> filter, UpdateDefinition<MongoMigrationHistoryItem> update,
                                                  UpdateOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UpdateResult> UpdateManyAsync(IClientSessionHandle session, FilterDefinition<MongoMigrationHistoryItem> filter,
                                                  UpdateDefinition<MongoMigrationHistoryItem> update, UpdateOptions? options = null,
                                                  CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public UpdateResult UpdateOne(FilterDefinition<MongoMigrationHistoryItem> filter, UpdateDefinition<MongoMigrationHistoryItem> update, UpdateOptions? options = null,
                                      CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public UpdateResult UpdateOne(IClientSessionHandle session, FilterDefinition<MongoMigrationHistoryItem> filter, UpdateDefinition<MongoMigrationHistoryItem> update,
                                      UpdateOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UpdateResult> UpdateOneAsync(FilterDefinition<MongoMigrationHistoryItem> filter, UpdateDefinition<MongoMigrationHistoryItem> update,
                                                 UpdateOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UpdateResult> UpdateOneAsync(IClientSessionHandle session, FilterDefinition<MongoMigrationHistoryItem> filter,
                                                 UpdateDefinition<MongoMigrationHistoryItem> update, UpdateOptions? options = null,
                                                 CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IChangeStreamCursor<TResult> Watch<TResult>(PipelineDefinition<ChangeStreamDocument<MongoMigrationHistoryItem>, TResult> pipeline,
                                                           ChangeStreamOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IChangeStreamCursor<TResult> Watch<TResult>(IClientSessionHandle session, PipelineDefinition<ChangeStreamDocument<MongoMigrationHistoryItem>, TResult> pipeline,
                                                           ChangeStreamOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IChangeStreamCursor<TResult>> WatchAsync<TResult>(PipelineDefinition<ChangeStreamDocument<MongoMigrationHistoryItem>, TResult> pipeline,
                                                                      ChangeStreamOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IChangeStreamCursor<TResult>> WatchAsync<TResult>(IClientSessionHandle session,
                                                                      PipelineDefinition<ChangeStreamDocument<MongoMigrationHistoryItem>, TResult> pipeline,
                                                                      ChangeStreamOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IMongoCollection<MongoMigrationHistoryItem> WithReadConcern(ReadConcern readConcern) => throw new NotSupportedException();
        public IMongoCollection<MongoMigrationHistoryItem> WithReadPreference(ReadPreference readPreference) => throw new NotSupportedException();
        public IMongoCollection<MongoMigrationHistoryItem> WithWriteConcern(WriteConcern writeConcern) => throw new NotSupportedException();
        IFilteredMongoCollection<TDerivedDocument> IMongoCollection<MongoMigrationHistoryItem>.OfType<TDerivedDocument>() => throw new NotSupportedException();
    }

    /// <summary>
    /// Hand-crafted recording double for <see cref="IMongoMigration"/>, per tests/AGENTS.md's rule against
    /// introducing new Moq usage for new tests.
    /// </summary>
    private sealed class FakeMigration : IMongoMigration
    {
        private readonly Func<IMongoHelper, IClientSessionHandle?, CancellationToken, Task>? _applyAsync;

        public FakeMigration(String id, Func<IMongoHelper, IClientSessionHandle?, CancellationToken, Task>? applyAsync = null)
        {
            Id = id;
            _applyAsync = applyAsync;
        }

        public Int32 ApplyCallCount { get; private set; }
        public String Id { get; }

        public async Task ApplyAsync(IMongoHelper mongoHelper, IClientSessionHandle? session = null, CancellationToken cancellationToken = default)
        {
            ApplyCallCount++;
            if (_applyAsync is not null)
            {
                await _applyAsync(mongoHelper, session, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Hand-crafted stub for <see cref="IMongoDatabase"/>.
    /// <see cref="GetCollection{TDocument}(String, MongoCollectionSettings)"/>
    /// is reached while building the history collection handle; it returns the provided
    /// <see cref="FakeHistoryCollection"/>, or a deliberately unusable value when none was given, since the tests
    /// that don't supply one never touch the collection further (a skipped run returns before reaching it).
    /// </summary>
    private sealed class FakeMongoDatabase : IMongoDatabase
    {
        private readonly FakeHistoryCollection? _historyCollection;

        public FakeMongoDatabase(FakeHistoryCollection? historyCollection = null) => _historyCollection = historyCollection;

        public IMongoClient Client => throw new NotSupportedException();
        public DatabaseNamespace DatabaseNamespace => throw new NotSupportedException();
        public MongoDatabaseSettings Settings => throw new NotSupportedException();

        public IAsyncCursor<TResult> Aggregate<TResult>(PipelineDefinition<NoPipelineInput, TResult> pipeline, AggregateOptions? options = null,
                                                        CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncCursor<TResult> Aggregate<TResult>(IClientSessionHandle session, PipelineDefinition<NoPipelineInput, TResult> pipeline, AggregateOptions? options = null,
                                                        CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IAsyncCursor<TResult>> AggregateAsync<TResult>(PipelineDefinition<NoPipelineInput, TResult> pipeline, AggregateOptions? options = null,
                                                                   CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IAsyncCursor<TResult>> AggregateAsync<TResult>(IClientSessionHandle session, PipelineDefinition<NoPipelineInput, TResult> pipeline,
                                                                   AggregateOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void AggregateToCollection<TResult>(PipelineDefinition<NoPipelineInput, TResult> pipeline, AggregateOptions? options = null,
                                                   CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void AggregateToCollection<TResult>(IClientSessionHandle session, PipelineDefinition<NoPipelineInput, TResult> pipeline, AggregateOptions? options = null,
                                                   CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task AggregateToCollectionAsync<TResult>(PipelineDefinition<NoPipelineInput, TResult> pipeline, AggregateOptions? options = null,
                                                        CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task AggregateToCollectionAsync<TResult>(IClientSessionHandle session, PipelineDefinition<NoPipelineInput, TResult> pipeline, AggregateOptions? options = null,
                                                        CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void CreateCollection(String name, CreateCollectionOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void CreateCollection(IClientSessionHandle session, String name, CreateCollectionOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CreateCollectionAsync(String name, CreateCollectionOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task CreateCollectionAsync(IClientSessionHandle session, String name, CreateCollectionOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void CreateView<TDocument, TResult>(String viewName, String viewOn, PipelineDefinition<TDocument, TResult> pipeline, CreateViewOptions<TDocument>? options = null,
                                                   CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void CreateView<TDocument, TResult>(IClientSessionHandle session, String viewName, String viewOn, PipelineDefinition<TDocument, TResult> pipeline,
                                                   CreateViewOptions<TDocument>? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CreateViewAsync<TDocument, TResult>(String viewName, String viewOn, PipelineDefinition<TDocument, TResult> pipeline,
                                                        CreateViewOptions<TDocument>? options = null,
                                                        CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task CreateViewAsync<TDocument, TResult>(IClientSessionHandle session, String viewName, String viewOn, PipelineDefinition<TDocument, TResult> pipeline,
                                                        CreateViewOptions<TDocument>? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void DropCollection(String name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void DropCollection(String name, DropCollectionOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void DropCollection(IClientSessionHandle session, String name, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void DropCollection(IClientSessionHandle session, String name, DropCollectionOptions options, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DropCollectionAsync(String name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DropCollectionAsync(String name, DropCollectionOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DropCollectionAsync(IClientSessionHandle session, String name, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DropCollectionAsync(IClientSessionHandle session, String name, DropCollectionOptions options, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IMongoCollection<TDocument> GetCollection<TDocument>(String name, MongoCollectionSettings? settings = null) =>
            _historyCollection is IMongoCollection<TDocument> typedCollection ? typedCollection : null!;

        public IAsyncCursor<String> ListCollectionNames(ListCollectionNamesOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncCursor<String> ListCollectionNames(IClientSessionHandle session, ListCollectionNamesOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IAsyncCursor<String>> ListCollectionNamesAsync(ListCollectionNamesOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IAsyncCursor<String>> ListCollectionNamesAsync(IClientSessionHandle session, ListCollectionNamesOptions? options = null,
                                                                   CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncCursor<BsonDocument> ListCollections(ListCollectionsOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncCursor<BsonDocument> ListCollections(IClientSessionHandle session, ListCollectionsOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IAsyncCursor<BsonDocument>> ListCollectionsAsync(ListCollectionsOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IAsyncCursor<BsonDocument>> ListCollectionsAsync(IClientSessionHandle session, ListCollectionsOptions? options = null,
                                                                     CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void RenameCollection(String oldName, String newName, RenameCollectionOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void RenameCollection(IClientSessionHandle session, String oldName, String newName, RenameCollectionOptions? options = null,
                                     CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RenameCollectionAsync(String oldName, String newName, RenameCollectionOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RenameCollectionAsync(IClientSessionHandle session, String oldName, String newName, RenameCollectionOptions? options = null,
                                          CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public TResult RunCommand<TResult>(Command<TResult> command, ReadPreference? readPreference = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public TResult RunCommand<TResult>(IClientSessionHandle session, Command<TResult> command, ReadPreference? readPreference = null,
                                           CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TResult> RunCommandAsync<TResult>(Command<TResult> command, ReadPreference? readPreference = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TResult> RunCommandAsync<TResult>(IClientSessionHandle session, Command<TResult> command, ReadPreference? readPreference = null,
                                                      CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IChangeStreamCursor<TResult> Watch<TResult>(PipelineDefinition<ChangeStreamDocument<BsonDocument>, TResult> pipeline, ChangeStreamOptions? options = null,
                                                           CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IChangeStreamCursor<TResult> Watch<TResult>(IClientSessionHandle session, PipelineDefinition<ChangeStreamDocument<BsonDocument>, TResult> pipeline,
                                                           ChangeStreamOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IChangeStreamCursor<TResult>> WatchAsync<TResult>(PipelineDefinition<ChangeStreamDocument<BsonDocument>, TResult> pipeline, ChangeStreamOptions? options = null,
                                                                      CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IChangeStreamCursor<TResult>> WatchAsync<TResult>(IClientSessionHandle session, PipelineDefinition<ChangeStreamDocument<BsonDocument>, TResult> pipeline,
                                                                      ChangeStreamOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IMongoDatabase WithReadConcern(ReadConcern readConcern) => throw new NotSupportedException();
        public IMongoDatabase WithReadPreference(ReadPreference readPreference) => throw new NotSupportedException();
        public IMongoDatabase WithWriteConcern(WriteConcern writeConcern) => throw new NotSupportedException();
    }

    /// <summary>
    /// Hand-crafted stub for <see cref="IMongoClient"/>. Only <see cref="StartSessionAsync"/> is reachable: it runs an
    /// optional hook, standing in for the time the session start takes, and returns a <see cref="FakeClientSession"/>.
    /// </summary>
    private sealed class FakeMongoClient : IMongoClient
    {
        private readonly Func<Task>? _onStartSession;

        public FakeMongoClient(Func<Task>? onStartSession = null)
        {
            _onStartSession = onStartSession;
        }

        public List<FakeClientSession> Sessions { get; } = [];

        public ICluster Cluster => throw new NotSupportedException();
        public MongoClientSettings Settings => throw new NotSupportedException();

        public void Dispose() { }

        public ClientBulkWriteResult BulkWrite(IReadOnlyList<BulkWriteModel> models, ClientBulkWriteOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ClientBulkWriteResult BulkWrite(IClientSessionHandle session, IReadOnlyList<BulkWriteModel> models, ClientBulkWriteOptions? options = null,
                                               CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ClientBulkWriteResult> BulkWriteAsync(IReadOnlyList<BulkWriteModel> models, ClientBulkWriteOptions? options = null,
                                                          CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ClientBulkWriteResult> BulkWriteAsync(IClientSessionHandle session, IReadOnlyList<BulkWriteModel> models, ClientBulkWriteOptions? options = null,
                                                          CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void DropDatabase(String name, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void DropDatabase(IClientSessionHandle session, String name, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DropDatabaseAsync(String name, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DropDatabaseAsync(IClientSessionHandle session, String name, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IMongoDatabase GetDatabase(String name, MongoDatabaseSettings? settings = null) => throw new NotSupportedException();

        public IAsyncCursor<String> ListDatabaseNames(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncCursor<String> ListDatabaseNames(ListDatabaseNamesOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncCursor<String> ListDatabaseNames(IClientSessionHandle session, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncCursor<String> ListDatabaseNames(IClientSessionHandle session, ListDatabaseNamesOptions options, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IAsyncCursor<String>> ListDatabaseNamesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IAsyncCursor<String>> ListDatabaseNamesAsync(ListDatabaseNamesOptions options, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IAsyncCursor<String>> ListDatabaseNamesAsync(IClientSessionHandle session, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IAsyncCursor<String>> ListDatabaseNamesAsync(IClientSessionHandle session, ListDatabaseNamesOptions options,
                                                                 CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncCursor<BsonDocument> ListDatabases(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncCursor<BsonDocument> ListDatabases(ListDatabasesOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncCursor<BsonDocument> ListDatabases(IClientSessionHandle session, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncCursor<BsonDocument> ListDatabases(IClientSessionHandle session, ListDatabasesOptions options, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IAsyncCursor<BsonDocument>> ListDatabasesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IAsyncCursor<BsonDocument>> ListDatabasesAsync(ListDatabasesOptions options, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IAsyncCursor<BsonDocument>> ListDatabasesAsync(IClientSessionHandle session, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IAsyncCursor<BsonDocument>> ListDatabasesAsync(IClientSessionHandle session, ListDatabasesOptions options,
                                                                   CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IClientSessionHandle StartSession(ClientSessionOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<IClientSessionHandle> StartSessionAsync(ClientSessionOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (_onStartSession is not null)
            {
                await _onStartSession();
            }

            var session = new FakeClientSession(this);
            Sessions.Add(session);
            return session;
        }

        public IChangeStreamCursor<TResult> Watch<TResult>(PipelineDefinition<ChangeStreamDocument<BsonDocument>, TResult> pipeline, ChangeStreamOptions? options = null,
                                                           CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IChangeStreamCursor<TResult> Watch<TResult>(IClientSessionHandle session, PipelineDefinition<ChangeStreamDocument<BsonDocument>, TResult> pipeline,
                                                           ChangeStreamOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IChangeStreamCursor<TResult>> WatchAsync<TResult>(PipelineDefinition<ChangeStreamDocument<BsonDocument>, TResult> pipeline,
                                                                      ChangeStreamOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IChangeStreamCursor<TResult>> WatchAsync<TResult>(IClientSessionHandle session,
                                                                      PipelineDefinition<ChangeStreamDocument<BsonDocument>, TResult> pipeline,
                                                                      ChangeStreamOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IMongoClient WithReadConcern(ReadConcern readConcern) => throw new NotSupportedException();

        public IMongoClient WithReadPreference(ReadPreference readPreference) => throw new NotSupportedException();

        public IMongoClient WithWriteConcern(WriteConcern writeConcern) => throw new NotSupportedException();
    }

    /// <summary>
    /// Hand-crafted recording double for <see cref="IClientSessionHandle"/> that tracks whether its transaction was
    /// committed or aborted.
    /// </summary>
    private sealed class FakeClientSession : IClientSessionHandle
    {
        public FakeClientSession(IMongoClient client)
        {
            Client = client;
        }

        public Int32 AbortCount { get; private set; }
        public Int32 CommitCount { get; private set; }
        public IMongoClient Client { get; }
        public BsonDocument ClusterTime => throw new NotSupportedException();
        public Boolean IsImplicit => false;
        public Boolean IsInTransaction { get; private set; }
        public BsonTimestamp OperationTime => throw new NotSupportedException();
        public ClientSessionOptions Options => throw new NotSupportedException();
        public IServerSession ServerSession => throw new NotSupportedException();
        public ICoreSessionHandle WrappedCoreSession => throw new NotSupportedException();

        public void AbortTransaction(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task AbortTransactionAsync(CancellationToken cancellationToken = default)
        {
            AbortCount++;
            IsInTransaction = false;
            return Task.CompletedTask;
        }

        public void AdvanceClusterTime(BsonDocument newClusterTime) => throw new NotSupportedException();

        public void AdvanceOperationTime(BsonTimestamp newOperationTime) => throw new NotSupportedException();

        public void CommitTransaction(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task CommitTransactionAsync(CancellationToken cancellationToken = default)
        {
            CommitCount++;
            IsInTransaction = false;
            return Task.CompletedTask;
        }

        public void StartTransaction(TransactionOptions? transactionOptions = null) => IsInTransaction = true;

        public TResult WithTransaction<TResult>(Func<IClientSessionHandle, CancellationToken, TResult> callback, TransactionOptions? transactionOptions = null,
                                                CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TResult> WithTransactionAsync<TResult>(Func<IClientSessionHandle, CancellationToken, Task<TResult>> callbackAsync,
                                                           TransactionOptions? transactionOptions = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IClientSessionHandle Fork() => throw new NotSupportedException();

        public void Dispose() { }
    }

    /// <summary>
    /// Hand-crafted stub for <see cref="IMongoHelper"/> with a controllable <see cref="TryAcquireLockAsync"/>.
    /// Every other member is unreachable in the tests that use it and throws deliberately.
    /// </summary>
    private sealed class FakeMongoHelper : IMongoHelper
    {
        private readonly IMongoClient? _client;
        private readonly Func<String, TimeSpan?, CancellationToken, Task<IMongoLock?>> _tryAcquireLockAsync;

        public FakeMongoHelper(Func<String, TimeSpan?, CancellationToken, Task<IMongoLock?>> tryAcquireLockAsync,
                               FakeHistoryCollection? historyCollection = null,
                               IMongoClient? client = null)
        {
            _tryAcquireLockAsync = tryAcquireLockAsync;
            _client = client;
            Database = new FakeMongoDatabase(historyCollection);
        }

        public IMongoClient Client => _client ?? throw new NotSupportedException();
        public IMongoDatabase Database { get; }

        public IMongoCollection<TDocument> GetCollection<TDocument>(MongoCollectionSettings? settings = null) => throw new NotSupportedException();

        public Task<IMongoLock?> TryAcquireLockAsync(String lockName, TimeSpan? leaseTime = null, CancellationToken cancellationToken = default) =>
            _tryAcquireLockAsync(lockName, leaseTime, cancellationToken);
    }

    /// <summary>
    /// Forwards to another logger and completes <see cref="Signal"/> when a formatted message matches, so a test can
    /// wait for a specific point in a run without polling a shared message list.
    /// </summary>
    private sealed class SignalingLogger : ILogger<MongoMigrationRunner>
    {
        private readonly ILogger<MongoMigrationRunner> _inner;
        private readonly Func<String, Boolean> _matches;

        public SignalingLogger(ILogger<MongoMigrationRunner> inner, Func<String, Boolean> matches)
        {
            _inner = inner;
            _matches = matches;
        }

        public TaskCompletionSource Signal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);

        public Boolean IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, String> formatter)
        {
            _inner.Log(logLevel, eventId, state, exception, formatter);
            if (_matches(formatter(state, exception)))
            {
                Signal.TrySetResult();
            }
        }
    }

    /// <summary>
    /// Hand-crafted stub for <see cref="IMongoHelper"/>. Not exercised by the constructor-guard test that uses
    /// it; the constructor's duplicate-ID validation never invokes it.
    /// </summary>
    private sealed class ThrowingMongoHelper : IMongoHelper
    {
        public IMongoClient Client => throw new NotSupportedException();
        public IMongoDatabase Database => throw new NotSupportedException();
        public IMongoCollection<TDocument> GetCollection<TDocument>(MongoCollectionSettings? settings = null) => throw new NotSupportedException();

        public Task<IMongoLock?> TryAcquireLockAsync(String lockName, TimeSpan? leaseTime = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// Hand-crafted <see cref="IMongoLock"/> that does not support the transactional ownership check, like a custom
    /// implementation of the public interface.
    /// </summary>
    private sealed class PlainLock : IMongoLock
    {
        public PlainLock(DateTime validUntilUtc)
        {
            ValidUntilUtc = validUntilUtc;
        }

        public String Id => "migration-lock";
        public Boolean IsValid => true;
        public DateTime ValidUntilUtc { get; }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<Boolean> TryExtendAsync(TimeSpan? leaseTime = null, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
