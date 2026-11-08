// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests.Integration.Migrations;

using Chaos.Mongo.Migrations;
using Chaos.Testing.Logging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Testcontainers.MongoDb;

[Category("Integration")]
public class MongoMigrationTransactionTests
{
    private MongoDbContainer _container;

    [OneTimeSetUp]
    public async Task GetMongoDbContainer() => _container = await MongoDbTestContainer.StartContainerAsync();

    [Test]
    public async Task RunMigrationsAsync_WithTransactionsDisabled_AppliesWithoutSession()
    {
        // Arrange
        var databaseName = $"NoTransactionMigrationTest_{Guid.NewGuid():N}";
        var connectionString = _container.GetConnectionString();
        MigrationTestTracker.Reset();
        SessionAwareMigration.Reset();

        var serviceProvider = new ServiceCollection()
                              .AddNUnitTestLogging()
                              .AddMongo(connectionString, databaseName, options =>
                              {
                                  options.UseTransactionsForMigrationsIfAvailable = false;
                              })
                              .WithMigration<SessionAwareMigration>()
                              .Services
                              .BuildServiceProvider();

        var runner = serviceProvider.GetRequiredService<IMongoMigrationRunner>();

        // Act
        await runner.RunMigrationsAsync();

        // Assert
        SessionAwareMigration.SessionWasNull.Should().BeTrue();
    }

    [Test]
    public async Task RunMigrationsAsync_WithTransactionsEnabled_AppliesWithSession()
    {
        // Arrange
        var databaseName = $"WithTransactionMigrationTest_{Guid.NewGuid():N}";
        var connectionString = _container.GetConnectionString();
        MigrationTestTracker.Reset();
        SessionAwareMigration.Reset();

        var serviceProvider = new ServiceCollection()
                              .AddNUnitTestLogging()
                              .AddMongo(connectionString, databaseName, options =>
                              {
                                  options.UseTransactionsForMigrationsIfAvailable = true;
                              })
                              .WithMigration<SessionAwareMigration>()
                              .Services
                              .BuildServiceProvider();

        var runner = serviceProvider.GetRequiredService<IMongoMigrationRunner>();

        // Act
        await runner.RunMigrationsAsync();

        // Assert
        SessionAwareMigration.SessionWasNull.Should().BeFalse();
    }
}
