// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests;

using Chaos.Mongo.Configuration;
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

    private static Mock<IMongoQueue> CreateMockQueue(Boolean autoStart)
    {
        var mockQueue = new Mock<IMongoQueue>();
        var queueDefinition = new MongoQueueDefinition
        {
            CollectionName = "test-queue",
            LockLeaseTime = MongoDefaults.QueueLockLeaseTime,
            PayloadType = typeof(TestPayload),
            QueryLimit = 1,
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
}
