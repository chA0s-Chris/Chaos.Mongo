// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests.Queues;

using Chaos.Mongo.Queues;
using FluentAssertions;
using Moq;
using NUnit.Framework;

public class MongoQueueTests
{
    [Test]
    public void Constructor_WhenPublisherIsNull_ThrowsArgumentNullException()
    {
        // Arrange
        var queueDefinition = CreateQueueDefinition();
        var mockSubscriptionFactory = new Mock<IMongoQueueSubscriptionFactory>();

        // Act
        var act = () => new MongoQueue<TestPayload>(queueDefinition, mockSubscriptionFactory.Object, null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Constructor_WhenQueueDefinitionIsNull_ThrowsArgumentNullException()
    {
        // Arrange
        var mockSubscriptionFactory = new Mock<IMongoQueueSubscriptionFactory>();
        var mockPublisher = new Mock<IMongoQueuePublisher>();

        // Act
        var act = () => new MongoQueue<TestPayload>(null!, mockSubscriptionFactory.Object, mockPublisher.Object);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Constructor_WhenSubscriptionFactoryIsNull_ThrowsArgumentNullException()
    {
        // Arrange
        var queueDefinition = CreateQueueDefinition();
        var mockPublisher = new Mock<IMongoQueuePublisher>();

        // Act
        var act = () => new MongoQueue<TestPayload>(queueDefinition, null!, mockPublisher.Object);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public async Task DisposeAsync_WithNoSubscription_CompletesSuccessfully()
    {
        // Arrange
        var queueDefinition = CreateQueueDefinition();
        var mockSubscriptionFactory = new Mock<IMongoQueueSubscriptionFactory>();
        var mockPublisher = new Mock<IMongoQueuePublisher>();
        var queue = new MongoQueue<TestPayload>(queueDefinition, mockSubscriptionFactory.Object, mockPublisher.Object);

        // Act
        await queue.DisposeAsync();

        // Assert
        queue.IsRunning.Should().BeFalse();
    }

    [Test]
    public async Task DisposeAsync_WithRunningSubscription_StopsSubscription()
    {
        // Arrange
        var queueDefinition = CreateQueueDefinition();
        var mockSubscription = new Mock<IMongoQueueSubscription<TestPayload>>();
        var mockSubscriptionFactory = new Mock<IMongoQueueSubscriptionFactory>();
        var mockPublisher = new Mock<IMongoQueuePublisher>();

        mockSubscription.Setup(x => x.IsActive).Returns(true);
        mockSubscriptionFactory.Setup(x => x.CreateAndRunAsync<TestPayload>(It.IsAny<MongoQueueDefinition>()))
                               .ReturnsAsync(mockSubscription.Object);

        var queue = new MongoQueue<TestPayload>(queueDefinition, mockSubscriptionFactory.Object, mockPublisher.Object);
        await queue.StartSubscriptionAsync();

        // Act
        await queue.DisposeAsync();

        // Assert
        mockSubscription.Verify(x => x.StopAsync(It.IsAny<CancellationToken>()), Times.Once);
        mockSubscription.Verify(x => x.DisposeAsync(), Times.Once);
    }

    [Test]
    public void IsRunning_WhenNoSubscription_ReturnsFalse()
    {
        // Arrange
        var queueDefinition = CreateQueueDefinition();
        var mockSubscriptionFactory = new Mock<IMongoQueueSubscriptionFactory>();
        var mockPublisher = new Mock<IMongoQueuePublisher>();
        var queue = new MongoQueue<TestPayload>(queueDefinition, mockSubscriptionFactory.Object, mockPublisher.Object);

        // Act
        var result = queue.IsRunning;

        // Assert
        result.Should().BeFalse();
    }

    [Test]
    public async Task IsRunning_WhenSubscriptionIsActive_ReturnsTrue()
    {
        // Arrange
        var queueDefinition = CreateQueueDefinition();
        var mockSubscription = new Mock<IMongoQueueSubscription<TestPayload>>();
        var mockSubscriptionFactory = new Mock<IMongoQueueSubscriptionFactory>();
        var mockPublisher = new Mock<IMongoQueuePublisher>();

        mockSubscription.Setup(x => x.IsActive).Returns(true);
        mockSubscriptionFactory.Setup(x => x.CreateAndRunAsync<TestPayload>(It.IsAny<MongoQueueDefinition>()))
                               .ReturnsAsync(mockSubscription.Object);

        var queue = new MongoQueue<TestPayload>(queueDefinition, mockSubscriptionFactory.Object, mockPublisher.Object);
        await queue.StartSubscriptionAsync();

        // Act
        var result = queue.IsRunning;

        // Assert
        result.Should().BeTrue();
    }

    [Test]
    public async Task IsRunning_WhenSubscriptionIsNotActive_ReturnsFalse()
    {
        // Arrange
        var queueDefinition = CreateQueueDefinition();
        var mockSubscription = new Mock<IMongoQueueSubscription<TestPayload>>();
        var mockSubscriptionFactory = new Mock<IMongoQueueSubscriptionFactory>();
        var mockPublisher = new Mock<IMongoQueuePublisher>();

        mockSubscription.Setup(x => x.IsActive).Returns(false);
        mockSubscriptionFactory.Setup(x => x.CreateAndRunAsync<TestPayload>(It.IsAny<MongoQueueDefinition>()))
                               .ReturnsAsync(mockSubscription.Object);

        var queue = new MongoQueue<TestPayload>(queueDefinition, mockSubscriptionFactory.Object, mockPublisher.Object);
        await queue.StartSubscriptionAsync();

        // Act
        var result = queue.IsRunning;

        // Assert
        result.Should().BeFalse();
    }

    [Test]
    public async Task PublishAsync_WithObjectPayload_DelegatesToTypedPublish()
    {
        // Arrange
        var queueDefinition = CreateQueueDefinition();
        var mockSubscriptionFactory = new Mock<IMongoQueueSubscriptionFactory>();
        var mockPublisher = new Mock<IMongoQueuePublisher>();
        var queue = new MongoQueue<TestPayload>(queueDefinition, mockSubscriptionFactory.Object, mockPublisher.Object);
        var payload = new TestPayload();

        mockPublisher.Setup(x => x.PublishAsync(queueDefinition, payload, It.IsAny<CancellationToken>()))
                     .Returns(Task.CompletedTask);

        // Act
        await queue.PublishAsync((Object)payload, CancellationToken.None);

        // Assert
        mockPublisher.Verify(x => x.PublishAsync(queueDefinition, payload, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task PublishAsync_WithObjectPayloadNull_ThrowsArgumentNullException()
    {
        // Arrange
        var queueDefinition = CreateQueueDefinition();
        var mockSubscriptionFactory = new Mock<IMongoQueueSubscriptionFactory>();
        var mockPublisher = new Mock<IMongoQueuePublisher>();
        var queue = new MongoQueue<TestPayload>(queueDefinition, mockSubscriptionFactory.Object, mockPublisher.Object);

        // Act
        var act = async () => await queue.PublishAsync((Object)null!, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Test]
    public async Task PublishAsync_WithObjectPayloadWrongType_ThrowsInvalidOperationException()
    {
        // Arrange
        var queueDefinition = CreateQueueDefinition();
        var mockSubscriptionFactory = new Mock<IMongoQueueSubscriptionFactory>();
        var mockPublisher = new Mock<IMongoQueuePublisher>();
        var queue = new MongoQueue<TestPayload>(queueDefinition, mockSubscriptionFactory.Object, mockPublisher.Object);
        var wrongPayload = new WrongPayload();

        // Act
        var act = async () => await queue.PublishAsync(wrongPayload, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
                 .WithMessage("Payload type * is not assignable to *");
    }

    [Test]
    public async Task PublishAsync_WithTypedPayload_DelegatesToPublisher()
    {
        // Arrange
        var queueDefinition = CreateQueueDefinition();
        var mockSubscriptionFactory = new Mock<IMongoQueueSubscriptionFactory>();
        var mockPublisher = new Mock<IMongoQueuePublisher>();
        var queue = new MongoQueue<TestPayload>(queueDefinition, mockSubscriptionFactory.Object, mockPublisher.Object);
        var payload = new TestPayload();

        mockPublisher.Setup(x => x.PublishAsync(queueDefinition, payload, It.IsAny<CancellationToken>()))
                     .Returns(Task.CompletedTask);

        // Act
        await queue.PublishAsync(payload, CancellationToken.None);

        // Assert
        mockPublisher.Verify(x => x.PublishAsync(queueDefinition, payload, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public void QueueDefinition_ReturnsConstructorValue()
    {
        // Arrange
        var queueDefinition = CreateQueueDefinition();
        var mockSubscriptionFactory = new Mock<IMongoQueueSubscriptionFactory>();
        var mockPublisher = new Mock<IMongoQueuePublisher>();
        var queue = new MongoQueue<TestPayload>(queueDefinition, mockSubscriptionFactory.Object, mockPublisher.Object);

        // Act
        var result = queue.QueueDefinition;

        // Assert
        result.Should().BeSameAs(queueDefinition);
    }

    [Test]
    public async Task StartSubscriptionAsync_WhenAlreadyRunning_DoesNotCreateNewSubscription()
    {
        // Arrange
        var queueDefinition = CreateQueueDefinition();
        var mockSubscription = new Mock<IMongoQueueSubscription<TestPayload>>();
        var mockSubscriptionFactory = new Mock<IMongoQueueSubscriptionFactory>();
        var mockPublisher = new Mock<IMongoQueuePublisher>();

        mockSubscription.Setup(x => x.IsActive).Returns(true);
        mockSubscriptionFactory.Setup(x => x.CreateAndRunAsync<TestPayload>(It.IsAny<MongoQueueDefinition>()))
                               .ReturnsAsync(mockSubscription.Object);

        var queue = new MongoQueue<TestPayload>(queueDefinition, mockSubscriptionFactory.Object, mockPublisher.Object);
        await queue.StartSubscriptionAsync();

        // Act
        await queue.StartSubscriptionAsync();

        // Assert
        mockSubscriptionFactory.Verify(x => x.CreateAndRunAsync<TestPayload>(It.IsAny<MongoQueueDefinition>()), Times.Once);
    }

    [Test]
    public async Task StartSubscriptionAsync_WhenNoSubscription_CreatesSubscription()
    {
        // Arrange
        var queueDefinition = CreateQueueDefinition();
        var mockSubscription = new Mock<IMongoQueueSubscription<TestPayload>>();
        var mockSubscriptionFactory = new Mock<IMongoQueueSubscriptionFactory>();
        var mockPublisher = new Mock<IMongoQueuePublisher>();

        mockSubscriptionFactory.Setup(x => x.CreateAndRunAsync<TestPayload>(It.IsAny<MongoQueueDefinition>()))
                               .ReturnsAsync(mockSubscription.Object);

        var queue = new MongoQueue<TestPayload>(queueDefinition, mockSubscriptionFactory.Object, mockPublisher.Object);

        // Act
        await queue.StartSubscriptionAsync();

        // Assert
        mockSubscriptionFactory.Verify(x => x.CreateAndRunAsync<TestPayload>(queueDefinition), Times.Once);
    }

    [Test]
    public async Task StartSubscriptionAsync_WhenSubscriptionNotActive_DisposesOldAndCreatesNew()
    {
        // Arrange
        var queueDefinition = CreateQueueDefinition();
        var mockOldSubscription = new Mock<IMongoQueueSubscription<TestPayload>>();
        var mockNewSubscription = new Mock<IMongoQueueSubscription<TestPayload>>();
        var mockSubscriptionFactory = new Mock<IMongoQueueSubscriptionFactory>();
        var mockPublisher = new Mock<IMongoQueuePublisher>();

        mockOldSubscription.Setup(x => x.IsActive).Returns(false);
        mockSubscriptionFactory.SetupSequence(x => x.CreateAndRunAsync<TestPayload>(It.IsAny<MongoQueueDefinition>()))
                               .ReturnsAsync(mockOldSubscription.Object)
                               .ReturnsAsync(mockNewSubscription.Object);

        var queue = new MongoQueue<TestPayload>(queueDefinition, mockSubscriptionFactory.Object, mockPublisher.Object);
        await queue.StartSubscriptionAsync();

        // Act
        await queue.StartSubscriptionAsync();

        // Assert
        mockOldSubscription.Verify(x => x.DisposeAsync(), Times.Once);
        mockSubscriptionFactory.Verify(x => x.CreateAndRunAsync<TestPayload>(queueDefinition), Times.Exactly(2));
    }

    [Test]
    public async Task StopSubscriptionAsync_WhenNoSubscription_CompletesSuccessfully()
    {
        // Arrange
        var queueDefinition = CreateQueueDefinition();
        var mockSubscriptionFactory = new Mock<IMongoQueueSubscriptionFactory>();
        var mockPublisher = new Mock<IMongoQueuePublisher>();
        var queue = new MongoQueue<TestPayload>(queueDefinition, mockSubscriptionFactory.Object, mockPublisher.Object);

        // Act
        await queue.StopSubscriptionAsync();

        // Assert
        // Should complete without errors
        queue.IsRunning.Should().BeFalse();
    }

    [Test]
    public async Task StopSubscriptionAsync_WhenSubscriptionActive_StopsAndDisposesSubscription()
    {
        // Arrange
        var queueDefinition = CreateQueueDefinition();
        var mockSubscription = new Mock<IMongoQueueSubscription<TestPayload>>();
        var mockSubscriptionFactory = new Mock<IMongoQueueSubscriptionFactory>();
        var mockPublisher = new Mock<IMongoQueuePublisher>();

        mockSubscription.Setup(x => x.IsActive).Returns(true);
        mockSubscriptionFactory.Setup(x => x.CreateAndRunAsync<TestPayload>(It.IsAny<MongoQueueDefinition>()))
                               .ReturnsAsync(mockSubscription.Object);

        var queue = new MongoQueue<TestPayload>(queueDefinition, mockSubscriptionFactory.Object, mockPublisher.Object);
        await queue.StartSubscriptionAsync();

        // Act
        await queue.StopSubscriptionAsync();

        // Assert
        mockSubscription.Verify(x => x.StopAsync(It.IsAny<CancellationToken>()), Times.Once);
        mockSubscription.Verify(x => x.DisposeAsync(), Times.Once);
    }

    [Test]
    public async Task StopSubscriptionAsync_WhenSubscriptionNotActive_DisposesSubscription()
    {
        // Arrange
        var queueDefinition = CreateQueueDefinition();
        var mockSubscription = new Mock<IMongoQueueSubscription<TestPayload>>();
        var mockSubscriptionFactory = new Mock<IMongoQueueSubscriptionFactory>();
        var mockPublisher = new Mock<IMongoQueuePublisher>();

        mockSubscription.Setup(x => x.IsActive).Returns(false);
        mockSubscriptionFactory.Setup(x => x.CreateAndRunAsync<TestPayload>(It.IsAny<MongoQueueDefinition>()))
                               .ReturnsAsync(mockSubscription.Object);

        var queue = new MongoQueue<TestPayload>(queueDefinition, mockSubscriptionFactory.Object, mockPublisher.Object);
        await queue.StartSubscriptionAsync();

        // Act
        await queue.StopSubscriptionAsync();

        // Assert
        mockSubscription.Verify(x => x.StopAsync(It.IsAny<CancellationToken>()), Times.Never);
        mockSubscription.Verify(x => x.DisposeAsync(), Times.Once);
    }

    private static MongoQueueDefinition CreateQueueDefinition()
        => new()
        {
            CollectionName = "test-queue",
            LockLeaseTime = MongoDefaults.QueueLockLeaseTime,
            PayloadType = typeof(TestPayload),
            QueryLimit = 1,
            PayloadHandlerType = typeof(IMongoQueuePayloadHandler<TestPayload>),
            AutoStartSubscription = false
        };

    public class TestPayload;

    public class WrongPayload;
}
