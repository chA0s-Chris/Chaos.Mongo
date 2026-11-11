// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests.Integration.Migrations;

using Chaos.Mongo.Migrations;
using Chaos.Testing.Logging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
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
        var databaseName = $"WaitForLockTest_{Guid.NewGuid():N}";
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
        var databaseName = $"ConcurrentMigrationTest_{Guid.NewGuid():N}";
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
    public async Task RunMigrationsAsync_WithLockStolenWhileTransactionalMigrationInProgress_AbortsTransactionAndDoesNotRecordMigration()
    {
        // Arrange
        var databaseName = $"LockLossTransactionTest_{Guid.NewGuid():N}";
        var connectionString = _container.GetConnectionString();
        MigrationTestTracker.Reset();

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var serviceProvider = new ServiceCollection()
                              .AddNUnitTestLogging()
                              .AddSingleton(gate)
                              .AddMongo(connectionString, databaseName, options =>
                              {
                                  options.AddMapping<TestDocument>("TestDocuments");
                                  options.MigrationLockLeaseTime = TimeSpan.FromMilliseconds(300);
                                  options.UseTransactionsForMigrationsIfAvailable = true;
                              })
                              .WithMigration<TransactionalBlockingTestMigration>()
                              .Services
                              .BuildServiceProvider();

        var runner = serviceProvider.GetRequiredService<IMongoMigrationRunner>();
        var mongoHelper = serviceProvider.GetRequiredService<IMongoHelper>();

        // Act
        var runTask = runner.RunMigrationsAsync();
        await WaitUntilAsync(() => MigrationTestTracker.ExecutedMigrations.Contains(nameof(TransactionalBlockingTestMigration)));

        // Steal the lock out from under the runner by flipping its holder, so the renewal loop's next tick is
        // refused. The lease (300ms) and the renewal interval (half of it) mean this is observed within ~150ms.
        var lockCollection = mongoHelper.Database.GetCollection<MongoLockDocument>("_locks");
        await lockCollection.UpdateOneAsync(
            Builders<MongoLockDocument>.Filter.Eq(x => x.Id, Chaos.Mongo.MongoDefaults.MigrationsLockName),
            Builders<MongoLockDocument>.Update.Set(x => x.Holder, "someone-else"));

        var act = async () => await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        gate.TrySetResult();

        var historyCollection = mongoHelper.Database.GetCollection<MongoMigrationHistoryItem>("_migrations");
        var historyItems = await historyCollection.Find(x => true).ToListAsync();
        historyItems.Should().BeEmpty();

        var testDocsCollection = mongoHelper.GetCollection<TestDocument>();
        var count = await testDocsCollection.CountDocumentsAsync(x => true);
        count.Should().Be(0);
    }

    [Test]
    public async Task RunMigrationsAsync_WithMigrationLongerThanLease_RenewsLeaseAndCompletes()
    {
        // Arrange
        var databaseName = $"LeaseRenewalTest_{Guid.NewGuid():N}";
        var connectionString = _container.GetConnectionString();
        MigrationTestTracker.Reset();

        var serviceProvider = new ServiceCollection()
                              .AddNUnitTestLogging()
                              .AddMongo(connectionString, databaseName, options =>
                              {
                                  // Renewal waits half the remaining lease, so this tolerates a ~1 s database round
                                  // trip; a 500 ms lease failed on loaded CI runners after one ~250 ms round trip.
                                  options.MigrationLockLeaseTime = TimeSpan.FromSeconds(2);
                              })
                              .WithMigration<VerySlowTestMigration>()
                              .Services
                              .BuildServiceProvider();

        var runner = serviceProvider.GetRequiredService<IMongoMigrationRunner>();

        // Act
        var result = await runner.RunMigrationsAsync();

        // Assert: the migration (~6s) outlives three leases (2s each), and the runner re-checks the lock before
        // recording history, so this only succeeds because the lease is renewed automatically while it runs.
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
