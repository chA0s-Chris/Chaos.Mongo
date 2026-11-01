// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests;

using Chaos.Mongo.Configuration;
using Chaos.Mongo.Migrations;
using Chaos.Mongo.Queues;
using Chaos.Testing.Logging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;

public class MongoHostedServiceTests
{
    [Test]
    public void Constructor_WhenLoggerIsNull_ThrowsArgumentNullException()
    {
        // Arrange
        var mockServiceScopeFactory = new Mock<IServiceScopeFactory>();
        var options = Options.Create(new MongoOptions());

        // Act
        var act = () => new MongoHostedService([], mockServiceScopeFactory.Object, options, null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Constructor_WhenOptionsIsNull_ThrowsArgumentNullException()
    {
        // Arrange
        var mockServiceScopeFactory = new Mock<IServiceScopeFactory>();
        var logger = new NUnitTestLogger<MongoHostedService>();

        // Act
        var act = () => new MongoHostedService([], mockServiceScopeFactory.Object, null!, logger);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Constructor_WhenQueuesIsNull_ThrowsArgumentNullException()
    {
        // Arrange
        var mockServiceScopeFactory = new Mock<IServiceScopeFactory>();
        var options = Options.Create(new MongoOptions());
        var logger = new NUnitTestLogger<MongoHostedService>();

        // Act
        var act = () => new MongoHostedService(null!, mockServiceScopeFactory.Object, options, logger);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Constructor_WhenServiceScopeFactoryIsNull_ThrowsArgumentNullException()
    {
        // Arrange
        var options = Options.Create(new MongoOptions());
        var logger = new NUnitTestLogger<MongoHostedService>();

        // Act
        var act = () => new MongoHostedService([], null!, options, logger);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Constructor_WithValidParameters_SuccessfullyCreatesInstance()
    {
        // Arrange
        var mockServiceScopeFactory = new Mock<IServiceScopeFactory>();
        var options = Options.Create(new MongoOptions());
        var logger = new NUnitTestLogger<MongoHostedService>();

        // Act
        var service = new MongoHostedService([], mockServiceScopeFactory.Object, options, logger);

        // Assert
        service.Should().NotBeNull();
    }

    [Test]
    public async Task StartAsync_Always_ReturnsCompletedTask()
    {
        // Arrange
        var mockServiceScopeFactory = new Mock<IServiceScopeFactory>();
        var options = Options.Create(new MongoOptions());
        var logger = new NUnitTestLogger<MongoHostedService>();
        var service = new MongoHostedService([], mockServiceScopeFactory.Object, options, logger);

        // Act
        await service.StartAsync(CancellationToken.None);

        // Assert
        // Should complete without errors
    }

    [Test]
    public async Task StartedAsync_WithAutoStartQueues_StartsAllSubscriptions()
    {
        // Arrange
        var mockQueue1 = CreateMockQueue(true);
        var mockQueue2 = CreateMockQueue(true);
        var queues = new List<IMongoQueue>
        {
            mockQueue1.Object,
            mockQueue2.Object
        };
        var mockServiceScopeFactory = new Mock<IServiceScopeFactory>();
        var options = Options.Create(new MongoOptions());
        var logger = new NUnitTestLogger<MongoHostedService>();
        var service = new MongoHostedService(queues, mockServiceScopeFactory.Object, options, logger);

        // Act
        await service.StartedAsync(CancellationToken.None);

        // Assert
        mockQueue1.Verify(x => x.StartSubscriptionAsync(It.IsAny<CancellationToken>()), Times.Once);
        mockQueue2.Verify(x => x.StartSubscriptionAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task StartedAsync_WithMixedQueues_OnlyStartsAutoStartQueues()
    {
        // Arrange
        var mockAutoStartQueue = CreateMockQueue(true);
        var mockManualStartQueue = CreateMockQueue(false);
        var queues = new List<IMongoQueue>
        {
            mockAutoStartQueue.Object,
            mockManualStartQueue.Object
        };
        var mockServiceScopeFactory = new Mock<IServiceScopeFactory>();
        var options = Options.Create(new MongoOptions());
        var logger = new NUnitTestLogger<MongoHostedService>();
        var service = new MongoHostedService(queues, mockServiceScopeFactory.Object, options, logger);

        // Act
        await service.StartedAsync(CancellationToken.None);

        // Assert
        mockAutoStartQueue.Verify(x => x.StartSubscriptionAsync(It.IsAny<CancellationToken>()), Times.Once);
        mockManualStartQueue.Verify(x => x.StartSubscriptionAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task StartedAsync_WithNoAutoStartQueues_DoesNotStartAnySubscriptions()
    {
        // Arrange
        var mockQueue1 = CreateMockQueue(false);
        var mockQueue2 = CreateMockQueue(false);
        var queues = new List<IMongoQueue>
        {
            mockQueue1.Object,
            mockQueue2.Object
        };
        var mockServiceScopeFactory = new Mock<IServiceScopeFactory>();
        var options = Options.Create(new MongoOptions());
        var logger = new NUnitTestLogger<MongoHostedService>();
        var service = new MongoHostedService(queues, mockServiceScopeFactory.Object, options, logger);

        // Act
        await service.StartedAsync(CancellationToken.None);

        // Assert
        mockQueue1.Verify(x => x.StartSubscriptionAsync(It.IsAny<CancellationToken>()), Times.Never);
        mockQueue2.Verify(x => x.StartSubscriptionAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task StartedAsync_WithNoQueues_CompletesSuccessfully()
    {
        // Arrange
        var mockServiceScopeFactory = new Mock<IServiceScopeFactory>();
        var options = Options.Create(new MongoOptions());
        var logger = new NUnitTestLogger<MongoHostedService>();
        var service = new MongoHostedService([], mockServiceScopeFactory.Object, options, logger);

        // Act
        await service.StartedAsync(CancellationToken.None);

        // Assert
        // Should complete without errors
    }

    [Test]
    public async Task StartingAsync_WhenApplyMigrationsOnStartupIsDisabled_DoesNotRunMigrations()
    {
        // Arrange
        var migrationRunner = new RecordingMigrationRunner(_ => throw new InvalidOperationException("Should not run"));
        var configuratorRunner = new RecordingConfiguratorRunner();
        var mockServiceScopeFactory = CreateFakeServiceScopeFactory(configuratorRunner, migrationRunner);
        var options = Options.Create(new MongoOptions
        {
            ApplyMigrationsOnStartup = false
        });
        var logger = new NUnitTestLogger<MongoHostedService>();
        var service = new MongoHostedService([], mockServiceScopeFactory, options, logger);

        // Act
        var act = async () => await service.StartingAsync(CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Test]
    public async Task StartingAsync_WhenApplyMigrationsOnStartupIsEnabled_RunsMigrationsBeforeConfigurators()
    {
        // Arrange
        var executionOrder = new List<String>();
        var migrationRunner = new RecordingMigrationRunner(_ =>
        {
            executionOrder.Add("Migrations");
            return Task.FromResult(new MongoMigrationResult
            {
                Skipped = false,
                AppliedMigrationIds = []
            });
        });

        var configuratorRunner = new RecordingConfiguratorRunner(_ =>
        {
            executionOrder.Add("Configurators");
            return Task.CompletedTask;
        });

        var mockServiceScopeFactory = CreateFakeServiceScopeFactory(configuratorRunner, migrationRunner);
        var options = Options.Create(new MongoOptions
        {
            ApplyMigrationsOnStartup = true,
            RunConfiguratorsOnStartup = true
        });
        var logger = new NUnitTestLogger<MongoHostedService>();
        var service = new MongoHostedService([], mockServiceScopeFactory, options, logger);

        // Act
        await service.StartingAsync(CancellationToken.None);

        // Assert
        executionOrder.Should().Equal("Migrations", "Configurators");
    }

    [Test]
    public async Task StartingAsync_WhenConfiguratorRunnerIsCanceled_PropagatesCancellation()
    {
        // Arrange
        var mockConfiguratorRunner = new Mock<IMongoConfiguratorRunner>();
        mockConfiguratorRunner.Setup(x => x.RunConfiguratorsAsync(It.IsAny<CancellationToken>()))
                              .ThrowsAsync(new OperationCanceledException());
        var mockServiceScopeFactory = CreateMockServiceScopeFactory(mockConfiguratorRunner.Object);
        var options = Options.Create(new MongoOptions
        {
            RunConfiguratorsOnStartup = true
        });
        var logger = new NUnitTestLogger<MongoHostedService>();
        var service = new MongoHostedService([], mockServiceScopeFactory, options, logger);

        // Act
        var act = async () => await service.StartingAsync(CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public void StartingAsync_WhenConfiguratorRunnerThrows_PropagatesException()
    {
        // Arrange
        var mockConfiguratorRunner = new Mock<IMongoConfiguratorRunner>();
        var expectedException = new InvalidOperationException("Configurator failed");
        mockConfiguratorRunner.Setup(x => x.RunConfiguratorsAsync(It.IsAny<CancellationToken>()))
                              .ThrowsAsync(expectedException);
        var mockServiceScopeFactory = CreateMockServiceScopeFactory(mockConfiguratorRunner.Object);
        var options = Options.Create(new MongoOptions
        {
            RunConfiguratorsOnStartup = true
        });
        var logger = new NUnitTestLogger<MongoHostedService>();
        var service = new MongoHostedService([], mockServiceScopeFactory, options, logger);

        // Act
        var act = async () => await service.StartingAsync(CancellationToken.None);

        // Assert
        act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Configurator failed");
    }

    [Test]
    public async Task StartingAsync_WhenMigrationsAreSkippedAndWaitTimeoutIsZero_DoesNotThrowAndRunsConfigurators()
    {
        // Arrange
        var migrationRunner = new RecordingMigrationRunner(_ => Task.FromResult(new MongoMigrationResult
        {
            Skipped = true,
            AppliedMigrationIds = []
        }));

        var configuratorRunner = new RecordingConfiguratorRunner();
        var mockServiceScopeFactory = CreateFakeServiceScopeFactory(configuratorRunner, migrationRunner);
        var options = Options.Create(new MongoOptions
        {
            ApplyMigrationsOnStartup = true,
            RunConfiguratorsOnStartup = true,
            MigrationLockWaitTimeout = TimeSpan.Zero
        });
        var logger = new NUnitTestLogger<MongoHostedService>();
        var service = new MongoHostedService([], mockServiceScopeFactory, options, logger);

        // Act
        var act = async () => await service.StartingAsync(CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();
        configuratorRunner.RunCallCount.Should().Be(1);
    }

    [Test]
    public async Task StartingAsync_WhenMigrationsAreSkippedWithNullWaitTimeout_ThrowsAndSkipsConfigurators()
    {
        // Arrange
        var migrationRunner = new RecordingMigrationRunner(_ => Task.FromResult(new MongoMigrationResult
        {
            Skipped = true,
            AppliedMigrationIds = []
        }));

        var configuratorRunner = new RecordingConfiguratorRunner();
        var mockServiceScopeFactory = CreateFakeServiceScopeFactory(configuratorRunner, migrationRunner);
        var options = Options.Create(new MongoOptions
        {
            ApplyMigrationsOnStartup = true,
            RunConfiguratorsOnStartup = true,
            MigrationLockWaitTimeout = null
        });
        var logger = new NUnitTestLogger<MongoHostedService>();
        var service = new MongoHostedService([], mockServiceScopeFactory, options, logger);

        // Act
        var act = async () => await service.StartingAsync(CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        configuratorRunner.RunCallCount.Should().Be(0);
    }

    [Test]
    public async Task StartingAsync_WhenMigrationsAreSkippedWithPositiveWaitTimeout_ThrowsAndSkipsConfigurators()
    {
        // Arrange
        var migrationRunner = new RecordingMigrationRunner(_ => Task.FromResult(new MongoMigrationResult
        {
            Skipped = true,
            AppliedMigrationIds = []
        }));

        var configuratorRunner = new RecordingConfiguratorRunner();
        var mockServiceScopeFactory = CreateFakeServiceScopeFactory(configuratorRunner, migrationRunner);
        var options = Options.Create(new MongoOptions
        {
            ApplyMigrationsOnStartup = true,
            RunConfiguratorsOnStartup = true,
            MigrationLockWaitTimeout = TimeSpan.FromMinutes(5)
        });
        var logger = new NUnitTestLogger<MongoHostedService>();
        var service = new MongoHostedService([], mockServiceScopeFactory, options, logger);

        // Act
        var act = async () => await service.StartingAsync(CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        configuratorRunner.RunCallCount.Should().Be(0);
    }

    [Test]
    public async Task StartingAsync_WhenRunConfiguratorsOnStartupIsDisabled_DoesNotRunConfigurators()
    {
        // Arrange
        var mockConfiguratorRunner = new Mock<IMongoConfiguratorRunner>();
        var mockServiceScopeFactory = CreateMockServiceScopeFactory(mockConfiguratorRunner.Object);
        var options = Options.Create(new MongoOptions
        {
            RunConfiguratorsOnStartup = false
        });
        var logger = new NUnitTestLogger<MongoHostedService>();
        var service = new MongoHostedService([], mockServiceScopeFactory, options, logger);

        // Act
        await service.StartingAsync(CancellationToken.None);

        // Assert
        mockConfiguratorRunner.Verify(x => x.RunConfiguratorsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task StartingAsync_WhenRunConfiguratorsOnStartupIsEnabled_PassesCancellationTokenToRunner()
    {
        // Arrange
        var mockConfiguratorRunner = new Mock<IMongoConfiguratorRunner>();
        var mockServiceScopeFactory = CreateMockServiceScopeFactory(mockConfiguratorRunner.Object);
        var options = Options.Create(new MongoOptions
        {
            RunConfiguratorsOnStartup = true
        });
        var logger = new NUnitTestLogger<MongoHostedService>();
        var service = new MongoHostedService([], mockServiceScopeFactory, options, logger);
        var cancellationToken = CancellationToken.None;

        // Act
        await service.StartingAsync(cancellationToken);

        // Assert
        mockConfiguratorRunner.Verify(x => x.RunConfiguratorsAsync(cancellationToken), Times.Once);
    }

    [Test]
    public async Task StartingAsync_WhenRunConfiguratorsOnStartupIsEnabled_RunsBeforeStartedAsync()
    {
        // Arrange
        var executionOrder = new List<String>();
        var mockConfiguratorRunner = new Mock<IMongoConfiguratorRunner>();
        mockConfiguratorRunner.Setup(x => x.RunConfiguratorsAsync(It.IsAny<CancellationToken>()))
                              .Callback(() => executionOrder.Add("Configurators"))
                              .Returns(Task.CompletedTask);

        var mockQueue = CreateMockQueue(true);
        mockQueue.Setup(x => x.StartSubscriptionAsync(It.IsAny<CancellationToken>()))
                 .Callback(() => executionOrder.Add("QueueStart"))
                 .Returns(Task.CompletedTask);

        var mockServiceScopeFactory = CreateMockServiceScopeFactory(mockConfiguratorRunner.Object);
        var options = Options.Create(new MongoOptions
        {
            RunConfiguratorsOnStartup = true
        });
        var logger = new NUnitTestLogger<MongoHostedService>();
        var service = new MongoHostedService([mockQueue.Object], mockServiceScopeFactory, options, logger);

        // Act
        await service.StartingAsync(CancellationToken.None);
        await service.StartedAsync(CancellationToken.None);

        // Assert
        executionOrder.Should().Equal("Configurators", "QueueStart");
    }

    [Test]
    public async Task StartingAsync_WhenRunConfiguratorsOnStartupIsEnabled_RunsConfigurators()
    {
        // Arrange
        var mockConfiguratorRunner = new Mock<IMongoConfiguratorRunner>();
        var mockServiceScopeFactory = CreateMockServiceScopeFactory(mockConfiguratorRunner.Object);
        var options = Options.Create(new MongoOptions
        {
            RunConfiguratorsOnStartup = true
        });
        var logger = new NUnitTestLogger<MongoHostedService>();
        var service = new MongoHostedService([], mockServiceScopeFactory, options, logger);

        // Act
        await service.StartingAsync(CancellationToken.None);

        // Assert
        mockConfiguratorRunner.Verify(x => x.RunConfiguratorsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task StopAsync_Always_ReturnsCompletedTask()
    {
        // Arrange
        var mockServiceScopeFactory = new Mock<IServiceScopeFactory>();
        var options = Options.Create(new MongoOptions());
        var logger = new NUnitTestLogger<MongoHostedService>();
        var service = new MongoHostedService([], mockServiceScopeFactory.Object, options, logger);

        // Act
        await service.StopAsync(CancellationToken.None);

        // Assert
        // Should complete without errors
    }

    [Test]
    public async Task StoppedAsync_Always_ReturnsCompletedTask()
    {
        // Arrange
        var mockServiceScopeFactory = new Mock<IServiceScopeFactory>();
        var options = Options.Create(new MongoOptions());
        var logger = new NUnitTestLogger<MongoHostedService>();
        var service = new MongoHostedService([], mockServiceScopeFactory.Object, options, logger);

        // Act
        await service.StoppedAsync(CancellationToken.None);

        // Assert
        // Should complete without errors
    }

    [Test]
    public async Task StoppingAsync_WithAutoStartQueues_StopsAllSubscriptions()
    {
        // Arrange
        var mockQueue1 = CreateMockQueue(true);
        var mockQueue2 = CreateMockQueue(true);
        var queues = new List<IMongoQueue>
        {
            mockQueue1.Object,
            mockQueue2.Object
        };
        var mockServiceScopeFactory = new Mock<IServiceScopeFactory>();
        var options = Options.Create(new MongoOptions());
        var logger = new NUnitTestLogger<MongoHostedService>();
        var service = new MongoHostedService(queues, mockServiceScopeFactory.Object, options, logger);

        // Act
        await service.StoppingAsync(CancellationToken.None);

        // Assert
        mockQueue1.Verify(x => x.StopSubscriptionAsync(It.IsAny<CancellationToken>()), Times.Once);
        mockQueue2.Verify(x => x.StopSubscriptionAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task StoppingAsync_WithMixedQueues_OnlyStopsAutoStartQueues()
    {
        // Arrange
        var mockAutoStartQueue = CreateMockQueue(true);
        var mockManualStartQueue = CreateMockQueue(false);
        var queues = new List<IMongoQueue>
        {
            mockAutoStartQueue.Object,
            mockManualStartQueue.Object
        };
        var mockServiceScopeFactory = new Mock<IServiceScopeFactory>();
        var options = Options.Create(new MongoOptions());
        var logger = new NUnitTestLogger<MongoHostedService>();
        var service = new MongoHostedService(queues, mockServiceScopeFactory.Object, options, logger);

        // Act
        await service.StoppingAsync(CancellationToken.None);

        // Assert
        mockAutoStartQueue.Verify(x => x.StopSubscriptionAsync(It.IsAny<CancellationToken>()), Times.Once);
        mockManualStartQueue.Verify(x => x.StopSubscriptionAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task StoppingAsync_WithNoAutoStartQueues_DoesNotStopAnySubscriptions()
    {
        // Arrange
        var mockQueue1 = CreateMockQueue(false);
        var mockQueue2 = CreateMockQueue(false);
        var queues = new List<IMongoQueue>
        {
            mockQueue1.Object,
            mockQueue2.Object
        };
        var mockServiceScopeFactory = new Mock<IServiceScopeFactory>();
        var options = Options.Create(new MongoOptions());
        var logger = new NUnitTestLogger<MongoHostedService>();
        var service = new MongoHostedService(queues, mockServiceScopeFactory.Object, options, logger);

        // Act
        await service.StoppingAsync(CancellationToken.None);

        // Assert
        mockQueue1.Verify(x => x.StopSubscriptionAsync(It.IsAny<CancellationToken>()), Times.Never);
        mockQueue2.Verify(x => x.StopSubscriptionAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task StoppingAsync_WithNoQueues_CompletesSuccessfully()
    {
        // Arrange
        var mockServiceScopeFactory = new Mock<IServiceScopeFactory>();
        var options = Options.Create(new MongoOptions());
        var logger = new NUnitTestLogger<MongoHostedService>();
        var service = new MongoHostedService([], mockServiceScopeFactory.Object, options, logger);

        // Act
        await service.StoppingAsync(CancellationToken.None);

        // Assert
        // Should complete without errors
    }

    /// <summary>
    /// Hand-crafted <see cref="IServiceScopeFactory"/>/<see cref="IServiceScope"/>/<see cref="IServiceProvider"/>
    /// triple for the migration-related tests, following tests/AGENTS.md's rule against introducing new
    /// mocking-framework usage for new tests.
    /// </summary>
    private static IServiceScopeFactory CreateFakeServiceScopeFactory(IMongoConfiguratorRunner configuratorRunner, IMongoMigrationRunner? migrationRunner = null) =>
        new FakeServiceScopeFactory(new FakeServiceProvider(configuratorRunner, migrationRunner));

    private static Mock<IMongoQueue> CreateMockQueue(Boolean autoStart)
    {
        var mockQueue = new Mock<IMongoQueue>();
        var queueDefinition = new MongoQueueDefinition
        {
            CollectionName = "test-queue",
            LockLeaseTime = MongoDefaults.QueueLockLeaseTime,
            PayloadType = typeof(TestPayload),
            BatchSize = 1,
            PayloadHandlerType = typeof(IMongoQueuePayloadHandler<TestPayload>),
            AutoStartSubscription = autoStart
        };

        mockQueue.Setup(x => x.QueueDefinition).Returns(queueDefinition);
        mockQueue.Setup(x => x.StartSubscriptionAsync(It.IsAny<CancellationToken>()))
                 .Returns(Task.CompletedTask);
        mockQueue.Setup(x => x.StopSubscriptionAsync(It.IsAny<CancellationToken>()))
                 .Returns(Task.CompletedTask);

        return mockQueue;
    }

    private static IServiceScopeFactory CreateMockServiceScopeFactory(IMongoConfiguratorRunner configuratorRunner)
    {
        var mockServiceProvider = new Mock<IServiceProvider>();
        mockServiceProvider.Setup(x => x.GetService(typeof(IMongoConfiguratorRunner)))
                           .Returns(configuratorRunner);

        var mockScope = new Mock<IServiceScope>();
        mockScope.Setup(x => x.ServiceProvider).Returns(mockServiceProvider.Object);

        var mockServiceScopeFactory = new Mock<IServiceScopeFactory>();
        mockServiceScopeFactory.Setup(x => x.CreateScope())
                               .Returns(mockScope.Object);

        return mockServiceScopeFactory.Object;
    }

    public class TestPayload;

    /// <summary>
    /// Hand-crafted <see cref="IServiceProvider"/> double resolving only <see cref="IMongoConfiguratorRunner"/> and
    /// <see cref="IMongoMigrationRunner"/>, the two services <see cref="MongoHostedService.StartingAsync"/> resolves
    /// from its created scope.
    /// </summary>
    private sealed class FakeServiceProvider : IServiceProvider
    {
        private readonly IMongoConfiguratorRunner? _configuratorRunner;
        private readonly IMongoMigrationRunner? _migrationRunner;

        public FakeServiceProvider(IMongoConfiguratorRunner? configuratorRunner, IMongoMigrationRunner? migrationRunner)
        {
            _configuratorRunner = configuratorRunner;
            _migrationRunner = migrationRunner;
        }

        public Object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IMongoConfiguratorRunner))
            {
                return _configuratorRunner;
            }

            if (serviceType == typeof(IMongoMigrationRunner))
            {
                return _migrationRunner;
            }

            return null;
        }
    }

    /// <summary>
    /// Hand-crafted <see cref="IServiceScope"/> double wrapping a fixed <see cref="IServiceProvider"/>.
    /// </summary>
    private sealed class FakeServiceScope : IServiceScope
    {
        public FakeServiceScope(IServiceProvider serviceProvider) => ServiceProvider = serviceProvider;

        public IServiceProvider ServiceProvider { get; }

        public void Dispose() { }
    }

    /// <summary>
    /// Hand-crafted <see cref="IServiceScopeFactory"/> double that always returns a scope over a fixed
    /// <see cref="IServiceProvider"/>.
    /// </summary>
    private sealed class FakeServiceScopeFactory : IServiceScopeFactory
    {
        private readonly IServiceProvider _serviceProvider;

        public FakeServiceScopeFactory(IServiceProvider serviceProvider) => _serviceProvider = serviceProvider;

        public IServiceScope CreateScope() => new FakeServiceScope(_serviceProvider);
    }

    /// <summary>
    /// Hand-crafted recording double for <see cref="IMongoConfiguratorRunner"/>, following tests/AGENTS.md's rule
    /// against introducing new Moq usage for new tests.
    /// </summary>
    private sealed class RecordingConfiguratorRunner : IMongoConfiguratorRunner
    {
        private readonly Func<CancellationToken, Task>? _runConfiguratorsAsync;

        public RecordingConfiguratorRunner(Func<CancellationToken, Task>? runConfiguratorsAsync = null) => _runConfiguratorsAsync = runConfiguratorsAsync;

        public Int32 RunCallCount { get; private set; }

        public Task RunConfiguratorsAsync(CancellationToken cancellationToken = default)
        {
            RunCallCount++;
            return _runConfiguratorsAsync?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }
    }

    /// <summary>
    /// Hand-crafted recording double for <see cref="IMongoMigrationRunner"/>, following tests/AGENTS.md's rule
    /// against introducing new Moq usage for new tests.
    /// </summary>
    private sealed class RecordingMigrationRunner : IMongoMigrationRunner
    {
        private readonly Func<CancellationToken, Task<MongoMigrationResult>> _runMigrationsAsync;

        public RecordingMigrationRunner(Func<CancellationToken, Task<MongoMigrationResult>> runMigrationsAsync) => _runMigrationsAsync = runMigrationsAsync;

        public Task<MongoMigrationResult> RunMigrationsAsync(CancellationToken cancellationToken = default) => _runMigrationsAsync(cancellationToken);
    }
}
