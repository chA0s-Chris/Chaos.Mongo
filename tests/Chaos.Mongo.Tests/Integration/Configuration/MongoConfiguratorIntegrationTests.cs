// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests.Integration.Configuration;

using Chaos.Mongo.Configuration;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using NUnit.Framework;
using System.Collections.Concurrent;
using Testcontainers.MongoDb;

public class MongoConfiguratorIntegrationTests
{
    private MongoDbContainer _container;

    [OneTimeSetUp]
    public async Task GetMongoDbContainer() => _container = await MongoDbTestContainer.StartContainerAsync();

    [Test]
    public async Task RunConfiguratorsAsync_WithCancellationToken_CancelsExecution()
    {
        // Arrange
        var databaseName = $"CancellationTest_{Guid.NewGuid():N}";
        var connectionString = _container.GetConnectionString();
        ExecutionTracker.Reset();

        var serviceProvider = new ServiceCollection()
                              .AddLogging()
                              .AddMongo(connectionString, databaseName, options =>
                              {
                                  options.AddMapping<TestDocument>("TestDocuments");
                              })
                              .WithConfigurator<FirstTestConfigurator>()
                              .WithConfigurator<SlowTestConfigurator>()
                              .WithConfigurator<ThirdTestConfigurator>()
                              .Services
                              .BuildServiceProvider();

        var runner = serviceProvider.GetRequiredService<IMongoConfiguratorRunner>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        // Act & Assert
        var cancellationToken = cts.Token;
        var act = async () => await runner.RunConfiguratorsAsync(cancellationToken);
        await act.Should().ThrowAsync<OperationCanceledException>();

        // Assert - Only first configurator should have executed before cancellation
        ExecutionTracker.ExecutedConfigurators.Should().Contain(nameof(FirstTestConfigurator));
        ExecutionTracker.ExecutedConfigurators.Should().NotContain(nameof(ThirdTestConfigurator));
    }

    [Test]
    public async Task RunConfiguratorsAsync_WithManuallyRegisteredConfigurators_CreatesCollectionsAndIndexes()
    {
        // Arrange
        var databaseName = $"ManualConfiguratorTest_{Guid.NewGuid():N}";
        var connectionString = _container.GetConnectionString();
        ExecutionTracker.Reset();

        var serviceProvider = new ServiceCollection()
                              .AddLogging()
                              .AddMongo(connectionString, databaseName, options =>
                              {
                                  options.AddMapping<TestDocument>("TestDocuments");
                                  options.AddMapping<AuditLog>("AuditLogs");
                              })
                              .WithConfigurator<FirstTestConfigurator>()
                              .WithConfigurator<SecondTestConfigurator>()
                              .Services
                              .BuildServiceProvider();

        var runner = serviceProvider.GetRequiredService<IMongoConfiguratorRunner>();
        var mongoHelper = serviceProvider.GetRequiredService<IMongoHelper>();

        // Act
        await runner.RunConfiguratorsAsync();

        // Assert - Verify all configurators executed
        ExecutionTracker.ExecutedConfigurators.Should().Contain(nameof(FirstTestConfigurator));
        ExecutionTracker.ExecutedConfigurators.Should().Contain(nameof(SecondTestConfigurator));
        ExecutionTracker.ExecutedConfigurators.Should().HaveCount(2);

        // Assert - Verify collections were created
        var collectionNames = await (await mongoHelper.Database.ListCollectionNamesAsync()).ToListAsync();
        collectionNames.Should().Contain("TestDocuments");
        collectionNames.Should().Contain("AuditLogs");

        // Assert - Verify indexes were created on TestDocuments
        var testDocsCollection = mongoHelper.GetCollection<TestDocument>();
        var testDocsIndexes = await (await testDocsCollection.Indexes.ListAsync()).ToListAsync();
        testDocsIndexes.Should().HaveCountGreaterThan(1); // _id + custom index
        testDocsIndexes.Should().Contain(idx => idx["name"].AsString.Contains("Name"));

        // Assert - Verify indexes were created on AuditLogs
        var auditLogsCollection = mongoHelper.GetCollection<AuditLog>();
        var auditLogsIndexes = await (await auditLogsCollection.Indexes.ListAsync()).ToListAsync();
        auditLogsIndexes.Should().HaveCountGreaterThan(1); // _id + custom index
        auditLogsIndexes.Should().Contain(idx => idx["name"].AsString.Contains("Timestamp"));
    }

    [Test]
    public async Task RunConfiguratorsAsync_WithMultipleConfigurators_ExecutesInRegistrationOrder()
    {
        // Arrange
        var databaseName = $"ExecutionOrderTest_{Guid.NewGuid():N}";
        var connectionString = _container.GetConnectionString();
        ExecutionTracker.Reset();

        var serviceProvider = new ServiceCollection()
                              .AddLogging()
                              .AddMongo(connectionString, databaseName, options =>
                              {
                                  options.AddMapping<TestDocument>("TestDocuments");
                              })
                              .WithConfigurator<FirstTestConfigurator>()
                              .WithConfigurator<ThirdTestConfigurator>()
                              .WithConfigurator<SecondTestConfigurator>()
                              .Services
                              .BuildServiceProvider();

        var runner = serviceProvider.GetRequiredService<IMongoConfiguratorRunner>();

        // Act
        await runner.RunConfiguratorsAsync();

        // Assert - Verify all configurators executed
        ExecutionTracker.ExecutedConfigurators.Should().Contain(nameof(FirstTestConfigurator));
        ExecutionTracker.ExecutedConfigurators.Should().Contain(nameof(ThirdTestConfigurator));
        ExecutionTracker.ExecutedConfigurators.Should().Contain(nameof(SecondTestConfigurator));
        ExecutionTracker.ExecutedConfigurators.Should().HaveCount(3);
    }

    [Test]
    public async Task RunConfiguratorsAsync_WithNoRegisteredConfigurators_CompletesSuccessfully()
    {
        // Arrange
        var databaseName = $"NoConfiguratorsTest_{Guid.NewGuid():N}";
        var connectionString = _container.GetConnectionString();

        var serviceProvider = new ServiceCollection()
                              .AddLogging()
                              .AddMongo(connectionString, databaseName)
                              .Services
                              .BuildServiceProvider();

        var runner = serviceProvider.GetRequiredService<IMongoConfiguratorRunner>();

        // Act
        await runner.RunConfiguratorsAsync();

        // Assert - Should complete without errors
        // No exception means success
    }

    [Test]
    public async Task RunConfiguratorsAsync_WithScopedDependencies_ConfiguratorCanAccessScopedServices()
    {
        // Arrange
        var databaseName = $"ScopedDepsTest_{Guid.NewGuid():N}";
        var connectionString = _container.GetConnectionString();
        ExecutionTracker.Reset();

        var serviceProvider = new ServiceCollection()
                              .AddLogging()
                              .AddScoped<TestScopedService>()
                              .AddMongo(connectionString, databaseName, options =>
                              {
                                  options.AddMapping<TestDocument>("TestDocuments");
                              })
                              .WithConfigurator<ConfiguratorWithScopedDependency>()
                              .Services
                              .BuildServiceProvider();

        // Act - Resolve runner from a scope
        using var scope = serviceProvider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IMongoConfiguratorRunner>();
        var scopedService = scope.ServiceProvider.GetRequiredService<TestScopedService>();

        await runner.RunConfiguratorsAsync();

        // Assert - Scoped service was accessed by the configurator
        scopedService.WasAccessed.Should().BeTrue();
        ExecutionTracker.ExecutedConfigurators.Should().Contain(nameof(ConfiguratorWithScopedDependency));
    }

    [Test]
    public async Task StartAsync_WithManualRegistrationAndRunOnStartupEnabled_ConfiguratorsRunViaHostedService()
    {
        // Arrange
        var databaseName = $"AutoRunTest_{Guid.NewGuid():N}";
        var connectionString = _container.GetConnectionString();
        ExecutionTracker.Reset();

        var services = new ServiceCollection()
                       .AddLogging()
                       .AddScoped<TestScopedService>()
                       .AddMongo(connectionString, databaseName, options =>
                       {
                           options.RunConfiguratorsOnStartup = true;
                           options.AddMapping<TestDocument>("TestDocuments");
                           options.AddMapping<AuditLog>("AuditLogs");
                       })
                       .WithConfigurator<FirstTestConfigurator>()
                       .WithConfigurator<SecondTestConfigurator>()
                       .WithConfigurator<ThirdTestConfigurator>()
                       .WithConfigurator<ConfiguratorWithScopedDependency>()
                       .Services;

        var serviceProvider = services.BuildServiceProvider();
        var hostedServices = serviceProvider.GetServices<IHostedService>().ToList();
        var mongoHelper = serviceProvider.GetRequiredService<IMongoHelper>();

        // Act - Start hosted services (which should trigger configurators)
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

        // Assert - Verify all auto-discovered configurators executed
        ExecutionTracker.ExecutedConfigurators.Should().Contain(nameof(FirstTestConfigurator));
        ExecutionTracker.ExecutedConfigurators.Should().Contain(nameof(SecondTestConfigurator));
        ExecutionTracker.ExecutedConfigurators.Should().Contain(nameof(ThirdTestConfigurator));
        ExecutionTracker.ExecutedConfigurators.Should().Contain(nameof(ConfiguratorWithScopedDependency));

        // Assert - Verify collections were created
        var collectionNames = await (await mongoHelper.Database.ListCollectionNamesAsync()).ToListAsync();
        collectionNames.Should().Contain("TestDocuments");
        collectionNames.Should().Contain("AuditLogs");

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
    public async Task StartAsync_WithManualRegistrationButRunOnStartupDisabled_ConfiguratorsDoNotRun()
    {
        // Arrange
        var databaseName = $"NoAutoRunTest_{Guid.NewGuid():N}";
        var connectionString = _container.GetConnectionString();
        ExecutionTracker.Reset();

        var services = new ServiceCollection()
                       .AddLogging()
                       .AddScoped<TestScopedService>()
                       .AddMongo(connectionString, databaseName, options =>
                       {
                           options.RunConfiguratorsOnStartup = false; // Disabled
                           options.AddMapping<TestDocument>("TestDocuments");
                       })
                       .WithConfigurator<FirstTestConfigurator>()
                       .WithConfigurator<SecondTestConfigurator>()
                       .Services;

        var serviceProvider = services.BuildServiceProvider();
        var hostedServices = serviceProvider.GetServices<IHostedService>().ToList();

        // Act - Start hosted services
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

        // Assert - Verify configurators did NOT execute
        ExecutionTracker.ExecutedConfigurators.Should().BeEmpty();

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

    // Execution Tracker for verifying configurator execution order
    public static class ExecutionTracker
    {
        private static readonly ConcurrentBag<String> Executed = [];

        public static IReadOnlyCollection<String> ExecutedConfigurators => Executed.ToList();

        public static void Reset() => Executed.Clear();

        public static void Track(String configuratorName) => Executed.Add(configuratorName);
    }

    internal class ConfiguratorWithScopedDependency : IMongoConfigurator
    {
        private readonly TestScopedService _scopedService;

        public ConfiguratorWithScopedDependency(TestScopedService scopedService)
        {
            _scopedService = scopedService;
        }

        public Task ConfigureAsync(IMongoHelper helper, CancellationToken cancellationToken = default)
        {
            ExecutionTracker.Track(nameof(ConfiguratorWithScopedDependency));
            _scopedService.WasAccessed = true;
            return Task.CompletedTask;
        }
    }

    // Test Configurators
    internal class FirstTestConfigurator : IMongoConfigurator
    {
        public async Task ConfigureAsync(IMongoHelper helper, CancellationToken cancellationToken = default)
        {
            ExecutionTracker.Track(nameof(FirstTestConfigurator));

            // Create collection and index
            var collection = helper.GetCollection<TestDocument>();
            await collection.Indexes.CreateOneAsync(
                new CreateIndexModel<TestDocument>(
                    Builders<TestDocument>.IndexKeys.Ascending(x => x.Name)
                ),
                cancellationToken: cancellationToken
            );
        }
    }

    internal class SecondTestConfigurator : IMongoConfigurator
    {
        public async Task ConfigureAsync(IMongoHelper helper, CancellationToken cancellationToken = default)
        {
            ExecutionTracker.Track(nameof(SecondTestConfigurator));

            // Create collection and index
            var collection = helper.GetCollection<AuditLog>();
            await collection.Indexes.CreateOneAsync(
                new CreateIndexModel<AuditLog>(
                    Builders<AuditLog>.IndexKeys.Descending(x => x.Timestamp)
                ),
                cancellationToken: cancellationToken
            );
        }
    }

    internal class SlowTestConfigurator : IMongoConfigurator
    {
        public async Task ConfigureAsync(IMongoHelper helper, CancellationToken cancellationToken = default)
        {
            ExecutionTracker.Track(nameof(SlowTestConfigurator));
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken); // Will be cancelled
        }
    }

    // Test Services
    internal class TestScopedService
    {
        public Boolean WasAccessed { get; set; }
    }

    internal class ThirdTestConfigurator : IMongoConfigurator
    {
        public Task ConfigureAsync(IMongoHelper helper, CancellationToken cancellationToken = default)
        {
            ExecutionTracker.Track(nameof(ThirdTestConfigurator));
            return Task.CompletedTask;
        }
    }

    private class AuditLog
    {
        // MongoDB accesses this identifier through BSON serialization.
        // ReSharper disable once UnusedMember.Local
        [BsonId]
        public ObjectId Id { get; init; }

        public DateTime Timestamp { get; init; }
    }

    // Test Documents
    private class TestDocument
    {
        // MongoDB accesses this identifier through BSON serialization.
        // ReSharper disable once UnusedMember.Local
        [BsonId]
        public ObjectId Id { get; init; }

        public String Name { get; init; } = String.Empty;
    }
}
