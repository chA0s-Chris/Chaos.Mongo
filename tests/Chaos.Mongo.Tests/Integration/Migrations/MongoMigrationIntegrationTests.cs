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

public class MongoMigrationIntegrationTests
{
    private MongoDbContainer _container;

    [OneTimeSetUp]
    public async Task GetMongoDbContainer() => _container = await MongoDbTestContainer.StartContainerAsync();

    [Test]
    public async Task RunMigrationsAsync_WithCancellationToken_CancelsExecution()
    {
        // Arrange
        var databaseName = $"MigrationCancellationTest_{Guid.NewGuid():N}";
        var connectionString = _container.GetConnectionString();
        MigrationTestTracker.Reset();

        // Cancellation is triggered by the second migration rather than by a wall-clock deadline,
        // so the run reaches a known point before it is cancelled no matter how slow the
        // connection, lock acquisition and transaction start are.
        using var cts = new CancellationTokenSource();

        var serviceProvider = new ServiceCollection()
                              .AddNUnitTestLogging()
                              .AddSingleton(cts)
                              .AddMongo(connectionString, databaseName)
                              .WithMigration<FastTestMigration>()
                              .WithMigration<CancellingTestMigration>()
                              .WithMigration<ThirdTestMigration>()
                              .Services
                              .BuildServiceProvider();

        var runner = serviceProvider.GetRequiredService<IMongoMigrationRunner>();

        // Act
        var cancellationToken = cts.Token;
        var act = async () => await runner.RunMigrationsAsync(cancellationToken);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        MigrationTestTracker.ExecutedMigrations.Should().Contain(nameof(FastTestMigration));
        MigrationTestTracker.ExecutedMigrations.Should().Contain(nameof(CancellingTestMigration));
        MigrationTestTracker.ExecutedMigrations.Should().NotContain(nameof(ThirdTestMigration));
    }

    [Test]
    public async Task RunMigrationsAsync_WithManuallyRegisteredMigrations_AppliesInOrder()
    {
        // Arrange
        var databaseName = $"ManualMigrationTest_{Guid.NewGuid():N}";
        var connectionString = _container.GetConnectionString();
        MigrationTestTracker.Reset();

        var serviceProvider = new ServiceCollection()
                              .AddNUnitTestLogging()
                              .AddMongo(connectionString, databaseName, options =>
                              {
                                  options.AddMapping<TestDocument>("TestDocuments");
                                  options.AddMapping<AuditLog>("AuditLogs");
                              })
                              .WithMigration<Migration_001_CreateTestDocumentsIndex>()
                              .WithMigration<Migration_002_CreateAuditLogsIndex>()
                              .Services
                              .BuildServiceProvider();

        var runner = serviceProvider.GetRequiredService<IMongoMigrationRunner>();
        var mongoHelper = serviceProvider.GetRequiredService<IMongoHelper>();

        // Act
        await runner.RunMigrationsAsync();

        // Assert
        MigrationTestTracker.ExecutedMigrations.Should().Contain("001_CreateTestDocumentsIndex");
        MigrationTestTracker.ExecutedMigrations.Should().Contain("002_CreateAuditLogsIndex");
        MigrationTestTracker.ExecutedMigrations.Should().HaveCount(2);

        var testDocsCollection = mongoHelper.GetCollection<TestDocument>();
        var testDocsIndexes = await (await testDocsCollection.Indexes.ListAsync()).ToListAsync();
        testDocsIndexes.Should().HaveCountGreaterThan(1);

        var auditLogsCollection = mongoHelper.GetCollection<AuditLog>();
        var auditLogsIndexes = await (await auditLogsCollection.Indexes.ListAsync()).ToListAsync();
        auditLogsIndexes.Should().HaveCountGreaterThan(1);

        var historyCollection = mongoHelper.Database.GetCollection<MongoMigrationHistoryItem>("_migrations");
        var historyItems = await historyCollection.Find(x => true).ToListAsync();
        historyItems.Should().HaveCount(2);
        historyItems.Should().Contain(x => x.Id == "001_CreateTestDocumentsIndex");
        historyItems.Should().Contain(x => x.Id == "002_CreateAuditLogsIndex");
        historyItems.Should().AllSatisfy(x => x.DurationMs.Should().BeGreaterThanOrEqualTo(0));
    }

    [Test]
    public async Task RunMigrationsAsync_WithMigrationAlreadyApplied_SkipsIt()
    {
        // Arrange
        var databaseName = $"IdempotentMigrationTest_{Guid.NewGuid():N}";
        var connectionString = _container.GetConnectionString();
        MigrationTestTracker.Reset();

        var serviceProvider = new ServiceCollection()
                              .AddNUnitTestLogging()
                              .AddMongo(connectionString, databaseName)
                              .WithMigration<IdempotentTestMigration>()
                              .Services
                              .BuildServiceProvider();

        var runner = serviceProvider.GetRequiredService<IMongoMigrationRunner>();

        // Act
        await runner.RunMigrationsAsync();
        MigrationTestTracker.Reset();
        await runner.RunMigrationsAsync();

        // Assert
        MigrationTestTracker.ExecutedMigrations.Should().BeEmpty();
    }

    [Test]
    public async Task RunMigrationsAsync_WithMigrationFailure_RollsBackTransactionAndDoesNotRecordHistory()
    {
        // Arrange
        var databaseName = $"FailedMigrationTest_{Guid.NewGuid():N}";
        var connectionString = _container.GetConnectionString();
        MigrationTestTracker.Reset();

        var serviceProvider = new ServiceCollection()
                              .AddNUnitTestLogging()
                              .AddMongo(connectionString, databaseName, options =>
                              {
                                  options.AddMapping<TestDocument>("TestDocuments");
                                  options.UseTransactionsForMigrationsIfAvailable = true;
                              })
                              .WithMigration<FailingTestMigration>()
                              .Services
                              .BuildServiceProvider();

        var runner = serviceProvider.GetRequiredService<IMongoMigrationRunner>();
        var mongoHelper = serviceProvider.GetRequiredService<IMongoHelper>();

        // Act
        var act = async () => await runner.RunMigrationsAsync();

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();

        var historyCollection = mongoHelper.Database.GetCollection<MongoMigrationHistoryItem>("_migrations");
        var historyItems = await historyCollection.Find(x => true).ToListAsync();
        historyItems.Should().BeEmpty();

        var testDocsCollection = mongoHelper.GetCollection<TestDocument>();
        var count = await testDocsCollection.CountDocumentsAsync(x => true);
        count.Should().Be(0);
    }


    [Test]
    public async Task RunMigrationsAsync_WithMigrationsInWrongOrderInCode_ExecutesInSortedOrder()
    {
        // Arrange
        var databaseName = $"MigrationOrderTest_{Guid.NewGuid():N}";
        var connectionString = _container.GetConnectionString();
        MigrationTestTracker.Reset();

        var serviceProvider = new ServiceCollection()
                              .AddNUnitTestLogging()
                              .AddMongo(connectionString, databaseName)
                              .WithMigration<Migration_003_Third>()
                              .WithMigration<Migration_001_First>()
                              .WithMigration<Migration_002_Second>()
                              .Services
                              .BuildServiceProvider();

        var runner = serviceProvider.GetRequiredService<IMongoMigrationRunner>();

        // Act
        await runner.RunMigrationsAsync();

        // Assert
        var executionOrder = MigrationTestTracker.ExecutionOrder;
        executionOrder.Should().HaveCount(3);
        executionOrder[0].Should().Be("001_First");
        executionOrder[1].Should().Be("002_Second");
        executionOrder[2].Should().Be("003_Third");
    }

    [Test]
    public async Task RunMigrationsAsync_WithNoRegisteredMigrations_CompletesSuccessfully()
    {
        // Arrange
        var databaseName = $"NoMigrationsTest_{Guid.NewGuid():N}";
        var connectionString = _container.GetConnectionString();

        var serviceProvider = new ServiceCollection()
                              .AddNUnitTestLogging()
                              .AddMongo(connectionString, databaseName)
                              .Services
                              .BuildServiceProvider();

        var runner = serviceProvider.GetRequiredService<IMongoMigrationRunner>();

        // Act
        await runner.RunMigrationsAsync();

        // Assert - Should complete without errors
    }
}
