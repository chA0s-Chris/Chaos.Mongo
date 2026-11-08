// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests.Integration.Migrations;

using Chaos.Mongo.Migrations;
using Chaos.Testing.Logging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using NUnit.Framework;
using Testcontainers.MongoDb;

[Category("Integration")]
public class MongoMigrationHostedServiceTests
{
    private MongoDbContainer _container;

    [OneTimeSetUp]
    public async Task GetMongoDbContainer() => _container = await MongoDbTestContainer.StartContainerAsync();

    [Test]
    public async Task StartingAsync_WhenAnotherInstanceHoldsTheMigrationLock_ThrowsAndPreventsStartup()
    {
        // Arrange
        var databaseName = $"BlockedStartupTest_{Guid.NewGuid():N}";
        var connectionString = _container.GetConnectionString();
        MigrationTestTracker.Reset();

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var services = new ServiceCollection()
                       .AddNUnitTestLogging()
                       .AddSingleton(gate)
                       .AddMongo(connectionString, databaseName, options =>
                       {
                           options.ApplyMigrationsOnStartup = true;
                           options.MigrationLockWaitTimeout = TimeSpan.FromMilliseconds(500);
                       })
                       .WithMigration<BlockingTestMigration>()
                       .Services;

        var serviceProvider = services.BuildServiceProvider();

        // Simulates a first instance that is already running the blocking migration and holding the lock.
        var firstInstanceRunner = serviceProvider.GetRequiredService<IMongoMigrationRunner>();
        var firstInstanceTask = Task.Run(async () => await firstInstanceRunner.RunMigrationsAsync());
        await WaitUntilAsync(() => MigrationTestTracker.ExecutedMigrations.Contains(nameof(BlockingTestMigration)));

        var hostedServices = serviceProvider.GetServices<IHostedService>().OfType<IHostedLifecycleService>().ToList();

        // Act: a second instance's hosted startup tries to run migrations while the first instance holds the lock.
        var act = async () =>
        {
            foreach (var hostedService in hostedServices)
            {
                await hostedService.StartingAsync(CancellationToken.None);
            }
        };

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();

        // Cleanup
        gate.SetResult();
        await firstInstanceTask;
    }

    [Test]
    public async Task StartingAsync_WithApplyMigrationsOnStartupDisabled_MigrationsDoNotRun()
    {
        // Arrange
        var databaseName = $"NoAutoRunMigrationTest_{Guid.NewGuid():N}";
        var connectionString = _container.GetConnectionString();
        MigrationTestTracker.Reset();

        var services = new ServiceCollection()
                       .AddNUnitTestLogging()
                       .AddMongo(connectionString, databaseName, options =>
                       {
                           options.ApplyMigrationsOnStartup = false;
                       })
                       .WithMigration<IdempotentTestMigration>()
                       .Services;

        var serviceProvider = services.BuildServiceProvider();
        var hostedServices = serviceProvider.GetServices<IHostedService>().ToList();

        // Act
        foreach (var hostedService in hostedServices)
        {
            if (hostedService is IHostedLifecycleService lifecycleService)
            {
                await lifecycleService.StartingAsync(CancellationToken.None);
                await lifecycleService.StartedAsync(CancellationToken.None);
            }
            else
            {
                await hostedService.StartAsync(CancellationToken.None);
            }
        }

        // Assert
        MigrationTestTracker.ExecutedMigrations.Should().BeEmpty();

        // Cleanup
        foreach (var hostedService in hostedServices)
        {
            if (hostedService is IHostedLifecycleService lifecycleService)
            {
                await lifecycleService.StoppingAsync(CancellationToken.None);
                await lifecycleService.StoppedAsync(CancellationToken.None);
            }
            else
            {
                await hostedService.StopAsync(CancellationToken.None);
            }
        }
    }

    [Test]
    public async Task StartingAsync_WithApplyMigrationsOnStartupEnabled_MigrationsRunViaHostedService()
    {
        // Arrange
        var databaseName = $"AutoRunMigrationTest_{Guid.NewGuid():N}";
        var connectionString = _container.GetConnectionString();
        MigrationTestTracker.Reset();

        var services = new ServiceCollection()
                       .AddNUnitTestLogging()
                       .AddMongo(connectionString, databaseName, options =>
                       {
                           options.ApplyMigrationsOnStartup = true;
                           options.AddMapping<TestDocument>("TestDocuments");
                       })
                       .WithMigration<Migration_001_CreateTestDocumentsIndex>()
                       .WithMigration<Migration_002_CreateAuditLogsIndex>()
                       .Services;

        var serviceProvider = services.BuildServiceProvider();
        var hostedServices = serviceProvider.GetServices<IHostedService>().ToList();
        var mongoHelper = serviceProvider.GetRequiredService<IMongoHelper>();

        // Act
        foreach (var hostedService in hostedServices)
        {
            if (hostedService is IHostedLifecycleService lifecycleService)
            {
                await lifecycleService.StartingAsync(CancellationToken.None);
                await lifecycleService.StartedAsync(CancellationToken.None);
            }
            else
            {
                await hostedService.StartAsync(CancellationToken.None);
            }
        }

        // Assert
        MigrationTestTracker.ExecutedMigrations.Should().Contain("001_CreateTestDocumentsIndex");
        MigrationTestTracker.ExecutedMigrations.Should().Contain("002_CreateAuditLogsIndex");

        var historyCollection = mongoHelper.Database.GetCollection<MongoMigrationHistoryItem>("_migrations");
        var historyItems = await historyCollection.Find(x => true).ToListAsync();
        historyItems.Should().HaveCount(2);

        // Cleanup
        foreach (var hostedService in hostedServices)
        {
            if (hostedService is IHostedLifecycleService lifecycleService)
            {
                await lifecycleService.StoppingAsync(CancellationToken.None);
                await lifecycleService.StoppedAsync(CancellationToken.None);
            }
            else
            {
                await hostedService.StopAsync(CancellationToken.None);
            }
        }
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
