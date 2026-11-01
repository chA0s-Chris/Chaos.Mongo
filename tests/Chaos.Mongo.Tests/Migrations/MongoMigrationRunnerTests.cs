// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests.Migrations;

using Chaos.Mongo.Migrations;
using Chaos.Testing.Logging;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Search;
using Moq;
using NUnit.Framework;

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
                                      _ => Task.CompletedTask,
                                      (_, _, _) => throw new InvalidOperationException("No extension should be attempted once the lock is already invalid."));

        var historyCollection = new FakeHistoryCollection();
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
        migration.ApplyCallCount.Should().Be(0);
        historyCollection.InsertedItems.Should().BeEmpty();
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

        var mongoLock = CreateMongoLock(timeProvider, leaseTime, (_, _, _) =>
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

        var mongoLock = CreateMongoLock(timeProvider, leaseTime, async (_, _, ct) =>
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
    public async Task RunMigrationsAsync_WithExtensionInFlightWhenMigrationsFinish_WaitsForItAndReleasesWithItsExpiry()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var leaseTime = TimeSpan.FromMinutes(10);
        var extensionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var extensionResult = new TaskCompletionSource<DateTime?>(TaskCreationOptions.RunContinuationsAsynchronously);
        DateTime? releasedWithExpiry = null;

        var mongoLock = CreateMongoLock(timeProvider,
                                        leaseTime,
                                        async (_, _, ct) =>
                                        {
                                            extensionStarted.SetResult();
                                            return await extensionResult.Task.WaitAsync(ct);
                                        },
                                        validUntilUtc =>
                                        {
                                            releasedWithExpiry = validUntilUtc;
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
        releasedWithExpiry.Should().Be(extendedUntilUtc);
        result.AppliedMigrationIds.Should().Equal("001_Finishing");
    }

    [Test]
    public async Task RunMigrationsAsync_WithExtensionStalledPastLeaseExpiry_StopsInProgressMigrationAndDoesNotRecordIt()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var leaseTime = TimeSpan.FromMinutes(10);
        var extensionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var mongoLock = CreateMongoLock(timeProvider, leaseTime, async (_, _, ct) =>
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
        var mongoLock = CreateMongoLock(timeProvider, leaseTime, (_, _, _) => Task.FromResult<DateTime?>(null));

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

        var mongoLock = CreateMongoLock(timeProvider, leaseTime, (_, _, _) => throw new InvalidOperationException("Lease extension failed unexpectedly"));

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

        var mongoLock = CreateMongoLock(timeProvider, leaseTime, (_, duration, _) =>
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

        var mongoLock = CreateMongoLock(timeProvider, leaseTime, (_, _, _) => Task.FromResult<DateTime?>(null)); // every extension is refused

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

        var mongoLock = CreateMongoLock(timeProvider, leaseTime, (_, duration, _) =>
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

        var mongoLock = CreateMongoLock(timeProvider, leaseTime, (_, duration, _) =>
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

        var mongoLock = CreateMongoLock(timeProvider, leaseTime, (_, duration, _) =>
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

    private static MongoLock CreateMongoLock(TimeProvider timeProvider,
                                             TimeSpan leaseTime,
                                             Func<DateTime, TimeSpan, CancellationToken, Task<DateTime?>> extendAction,
                                             Func<DateTime, Task>? releaseAction = null) =>
        new("migration-lock",
            timeProvider.GetUtcNow().Add(leaseTime).UtcDateTime,
            leaseTime,
            timeProvider,
            releaseAction ?? (_ => Task.CompletedTask),
            extendAction);

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

        public FakeHistoryCollection(IReadOnlyList<MongoMigrationHistoryItem>? initialItems = null, Func<CancellationToken, Task>? onFind = null)
        {
            _initialItems = initialItems ?? [];
            _onFind = onFind;
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

        public Task InsertOneAsync(MongoMigrationHistoryItem document, CancellationToken cancellationToken)
        {
            _insertedItems.Add(document);
            return Task.CompletedTask;
        }

        public Task InsertOneAsync(MongoMigrationHistoryItem document, InsertOneOptions? options = null, CancellationToken cancellationToken = default)
        {
            _insertedItems.Add(document);
            return Task.CompletedTask;
        }

        public Task InsertOneAsync(IClientSessionHandle session, MongoMigrationHistoryItem document, InsertOneOptions? options = null,
                                   CancellationToken cancellationToken = default)
        {
            _insertedItems.Add(document);
            return Task.CompletedTask;
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
    /// Hand-crafted stub for <see cref="IMongoHelper"/> with a controllable <see cref="TryAcquireLockAsync"/>.
    /// Every other member is unreachable in the tests that use it and throws deliberately.
    /// </summary>
    private sealed class FakeMongoHelper : IMongoHelper
    {
        private readonly Func<String, TimeSpan?, CancellationToken, Task<IMongoLock?>> _tryAcquireLockAsync;

        public FakeMongoHelper(Func<String, TimeSpan?, CancellationToken, Task<IMongoLock?>> tryAcquireLockAsync,
                               FakeHistoryCollection? historyCollection = null)
        {
            _tryAcquireLockAsync = tryAcquireLockAsync;
            Database = new FakeMongoDatabase(historyCollection);
        }

        public IMongoClient Client => throw new NotSupportedException();
        public IMongoDatabase Database { get; }

        public IMongoCollection<TDocument> GetCollection<TDocument>(MongoCollectionSettings? settings = null) => throw new NotSupportedException();

        public Task<IMongoLock?> TryAcquireLockAsync(String lockName, TimeSpan? leaseTime = null, CancellationToken cancellationToken = default) =>
            _tryAcquireLockAsync(lockName, leaseTime, cancellationToken);
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
}
