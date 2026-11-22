// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests.Integration.Migrations;

using Chaos.Mongo.Migrations;
using Chaos.Mongo.TestSupport;
using Chaos.Testing.Logging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Driver;
using NUnit.Framework;
using Testcontainers.MongoDb;

[Category("Integration")]
public class MongoMigrationConcurrencyTests
{
    private MongoDbContainer _container;

    [OneTimeSetUp]
    public async Task GetMongoDbContainer() => _container = await MongoDbTestContainer.StartContainerAsync();

    [Test]
    public async Task RunMigrationsAsync_SecondInstanceWaitsForFirstToFinish_ReturnsNonSkippedResultAfterward()
    {
        // Arrange
        var databaseName = TestDatabases.NewName("WaitForLockTest");
        var connectionString = _container.GetConnectionString();
        MigrationTestTracker.Reset();

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var serviceProvider = new ServiceCollection()
                              .AddNUnitTestLogging()
                              .AddSingleton(gate)
                              .AddMongo(connectionString, databaseName, options =>
                              {
                                  options.MigrationLockWaitTimeout = TimeSpan.FromSeconds(30);
                              })
                              .WithMigration<BlockingTestMigration>()
                              .Services
                              .BuildServiceProvider();

        var runner1 = serviceProvider.GetRequiredService<IMongoMigrationRunner>();
        var runner2 = serviceProvider.GetRequiredService<IMongoMigrationRunner>();

        // Act
        var task1 = Task.Run(async () => await runner1.RunMigrationsAsync());
        await WaitUntilAsync(() => MigrationTestTracker.ExecutedMigrations.Contains(nameof(BlockingTestMigration)));

        var task2 = Task.Run(async () => await runner2.RunMigrationsAsync());
        await Task.Delay(TimeSpan.FromMilliseconds(250));
        task2.IsCompleted.Should().BeFalse("the second instance should still be waiting for the first to finish");

        gate.SetResult();
        var result1 = await task1;
        var result2 = await task2;

        // Assert
        result1.Skipped.Should().BeFalse();
        result1.AppliedMigrationIds.Should().Equal("444_BlockingTest");
        result2.Skipped.Should().BeFalse();
        result2.AppliedMigrationIds.Should().BeEmpty();
    }

    [Test]
    public async Task RunMigrationsAsync_WithConcurrentExecutions_OnlyOneExecutes()
    {
        // Arrange
        var databaseName = TestDatabases.NewName("ConcurrentMigrationTest");
        var connectionString = _container.GetConnectionString();
        MigrationTestTracker.Reset();

        var serviceProvider = new ServiceCollection()
                              .AddNUnitTestLogging()
                              .AddMongo(connectionString, databaseName)
                              .WithMigration<ConcurrentTestMigration>()
                              .Services
                              .BuildServiceProvider();

        var runner1 = serviceProvider.GetRequiredService<IMongoMigrationRunner>();
        var runner2 = serviceProvider.GetRequiredService<IMongoMigrationRunner>();

        // Act
        var task1 = Task.Run(async () => await runner1.RunMigrationsAsync());
        var task2 = Task.Run(async () => await runner2.RunMigrationsAsync());

        var results = await Task.WhenAll(task1, task2);

        // Assert
        MigrationTestTracker.ExecutedMigrations.Should().ContainSingle(nameof(ConcurrentTestMigration));
        results.Should().AllSatisfy(x => x.Skipped.Should().BeFalse());
        results.Sum(x => x.AppliedMigrationIds.Count).Should().Be(1);
    }

    [Test]
    public async Task RunMigrationsAsync_WithLockStolenWhileTransactionalMigrationInProgressAndNoOwnershipCheck_AbortsTransactionAndDoesNotRecordMigration()
    {
        // Arrange
        // The built-in lock writes to its document inside the migration transaction, so a theft attempted meanwhile waits
        // for the transaction (see MongoMigrationLockFencingTests). A lock without that ownership check, as for a custom
        // IMongoLock, leaves the document free, which keeps this scenario observable: the renewal loop refuses the stolen
        // lock while a real transaction with a session-bound write is open. The clock is controlled, so the lease stays
        // live until the refused renewal and cannot expire first.
        var databaseName = TestDatabases.NewName("LockLossTransactionTest");
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var leaseTime = TimeSpan.FromMinutes(10);
        var inside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var migration = new FencingTestMigration("holder", async (_, token) =>
        {
            // Signalled only once the session-bound insert has succeeded.
            inside.TrySetResult();
            await new TaskCompletionSource().Task.WaitAsync(token);
        });
        var serviceProvider = new ServiceCollection()
                              .AddSingleton<TimeProvider>(clock)
                              .AddMongo(_container.GetConnectionString(), databaseName, options =>
                              {
                                  options.MigrationLockLeaseTime = leaseTime;
                                  options.UseTransactionsForMigrationsIfAvailable = true;
                              })
                              .Services
                              .BuildServiceProvider();
        var mongoHelper = serviceProvider.GetRequiredService<IMongoHelper>();
        var plainHelper = new PlainLockMongoHelper(mongoHelper);
        var logger = new NUnitTestLogger<MongoMigrationRunner>(captureMessages: true);
        var runner = new MongoMigrationRunner([migration],
                                              plainHelper,
                                              serviceProvider.GetRequiredService<IOptions<MongoOptions>>(),
                                              logger,
                                              clock);
        using var testCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var cancellationToken = testCancellation.Token;
        Task<MongoMigrationResult>? runTask = null;

        try
        {
            // Act
            runTask = runner.RunMigrationsAsync(cancellationToken);
            await inside.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

            // Steal the lock while the lease is live, then let the clock reach the renewal tick (half of the lease), which
            // is well before expiry, so only the refused renewal can stop the run.
            var plainLock = plainHelper.Locks.Should().ContainSingle().Subject;
            var validUntilUtc = plainLock.ValidUntilUtc;
            var lockCollection = mongoHelper.Database.GetCollection<MongoLockDocument>("_locks");
            await lockCollection.UpdateOneAsync(
                Builders<MongoLockDocument>.Filter.Eq(x => x.Id, Chaos.Mongo.MongoDefaults.MigrationLockName),
                Builders<MongoLockDocument>.Update.Set(x => x.Holder, "someone-else"),
                cancellationToken: cancellationToken);

            clock.Advance((leaseTime / 2) + TimeSpan.FromSeconds(1));
            await plainLock.FirstExtensionCompleted.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

            var act = async () => await runTask.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

            // Assert
            await act.Should().ThrowAsync<OperationCanceledException>();
            testCancellation.IsCancellationRequested.Should().BeFalse("the run must have stopped because of the refused renewal, not the test timeout");
            plainLock.ExtensionResults.Should().Equal(false);
            clock.GetUtcNow().UtcDateTime.Should().BeBefore(validUntilUtc, "the lease had not expired, so the refusal was the stolen lock");
            logger.LogMessages.Should().Contain(message => message.LogLevel == LogLevel.Warning && message.Message.Contains("could not be renewed"));
            logger.LogMessages.Should().Contain(message => message.LogLevel == LogLevel.Warning && message.Message.Contains("was lost"));
            migration.ApplyCallCount.Should().Be(1);

            var historyCollection = mongoHelper.Database.GetCollection<MongoMigrationHistoryItem>("_migrations");
            var historyItems = await historyCollection.Find(x => true).ToListAsync(cancellationToken);
            historyItems.Should().BeEmpty();

            var documents = mongoHelper.Database.GetCollection<FencingTestDocument>(FencingTestMigration.CollectionName);
            var count = await documents.CountDocumentsAsync(x => true, cancellationToken: cancellationToken);
            count.Should().Be(0, "the session-bound insert that had succeeded was rolled back");
        }
        finally
        {
            await testCancellation.CancelAsync();
            if (runTask is not null)
            {
                await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(10)));
            }
        }
    }

    [Test]
    public async Task RunMigrationsAsync_WithMigrationLongerThanLease_RenewsLeaseAndCompletes()
    {
        // Arrange
        var databaseName = TestDatabases.NewName("LeaseRenewalTest");
        var connectionString = _container.GetConnectionString();
        MigrationTestTracker.Reset();

        var serviceProvider = new ServiceCollection()
                              .AddNUnitTestLogging()
                              .AddMongo(connectionString, databaseName, options =>
                              {
                                  // Renewal waits half the remaining lease, so this tolerates a ~1 s database round
                                  // trip; a 500 ms lease failed on loaded CI runners after one ~250 ms round trip.
                                  options.MigrationLockLeaseTime = TimeSpan.FromSeconds(2);

                                  // A transaction holds the lock document and so blocks renewal until it ends; this test
                                  // covers the renewal loop of a run without a transaction.
                                  options.UseTransactionsForMigrationsIfAvailable = false;
                              })
                              .WithMigration<VerySlowTestMigration>()
                              .Services
                              .BuildServiceProvider();

        var runner = serviceProvider.GetRequiredService<IMongoMigrationRunner>();

        // Act
        var result = await runner.RunMigrationsAsync();

        // Assert: the migration (~3 s) outlives its 2 s lease, and the runner re-checks the lock before recording
        // history, so this only succeeds because the lease is renewed automatically while it runs.
        result.Skipped.Should().BeFalse();
        result.AppliedMigrationIds.Should().Equal("777_VerySlowTest");
    }

    private static async Task WaitUntilAsync(Func<Boolean> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }
    }
}

/// <summary>
/// Hands out the real locks behind a type that implements only <see cref="IMongoLock"/>, like a custom lock would, so the
/// runner has no server-side ownership check to use.
/// </summary>
internal sealed class PlainLockMongoHelper : IMongoHelper
{
    private readonly IMongoHelper _inner;

    public PlainLockMongoHelper(IMongoHelper inner)
    {
        _inner = inner;
    }

    public List<PlainLock> Locks { get; } = [];

    public IMongoClient Client => _inner.Client;

    public IMongoDatabase Database => _inner.Database;

    public IMongoCollection<TDocument> GetCollection<TDocument>(MongoCollectionSettings? settings = null) => _inner.GetCollection<TDocument>(settings);

    public async Task<IMongoLock?> TryAcquireLockAsync(String lockName, TimeSpan? leaseTime = null, CancellationToken cancellationToken = default)
    {
        var mongoLock = await _inner.TryAcquireLockAsync(lockName, leaseTime, cancellationToken);
        if (mongoLock is null)
        {
            return null;
        }

        var plainLock = new PlainLock(mongoLock);
        Locks.Add(plainLock);
        return plainLock;
    }
}

internal sealed class PlainLock : IMongoLock
{
    private readonly TaskCompletionSource _firstExtensionCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IMongoLock _inner;

    public PlainLock(IMongoLock inner)
    {
        _inner = inner;
    }

    /// <summary>
    /// Gets the outcome of every real extension the runner attempted.
    /// </summary>
    public List<Boolean> ExtensionResults { get; } = [];

    /// <summary>
    /// Gets a task that completes once the first real extension has returned.
    /// </summary>
    public Task FirstExtensionCompleted => _firstExtensionCompleted.Task;

    public String Id => _inner.Id;

    public Boolean IsValid => _inner.IsValid;

    public DateTime ValidUntilUtc => _inner.ValidUntilUtc;

    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    public async Task<Boolean> TryExtendAsync(TimeSpan? leaseTime = null, CancellationToken cancellationToken = default)
    {
        var extended = await _inner.TryExtendAsync(leaseTime, cancellationToken);
        ExtensionResults.Add(extended);
        _firstExtensionCompleted.TrySetResult();
        return extended;
    }
}
