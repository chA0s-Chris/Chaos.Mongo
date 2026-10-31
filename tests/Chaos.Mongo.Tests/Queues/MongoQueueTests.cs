// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests.Queues;

using Chaos.Mongo.Queues;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
    public async Task DisposeAsync_CalledConcurrently_DoesNotThrow()
    {
        // Arrange
        var queueDefinition = CreateQueueDefinition();
        var subscription = new CountingQueueSubscription();
        var subscriptionFactory = new SingleUseQueueSubscriptionFactory(subscription);
        var publisher = new ThrowingQueuePublisher();
        var queue = new MongoQueue<TestPayload>(queueDefinition, subscriptionFactory, publisher);
        await queue.StartSubscriptionAsync();

        var firstDispose = queue.DisposeAsync().AsTask();
        await subscription.StopEntered.WaitAsync(TimeSpan.FromSeconds(5));
        firstDispose.IsCompleted.Should().BeFalse("the first disposal must still be tearing down the subscription when the second call starts");

        // Act
        var secondDispose = queue.DisposeAsync().AsTask();
        subscription.ReleaseStop();
        var act = () => Task.WhenAll(firstDispose, secondDispose).WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        await act.Should().NotThrowAsync();
        subscription.StopCalls.Should().Be(1);
        subscription.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task DisposeAsync_CalledTwice_DoesNotThrow()
    {
        // Arrange
        var queueDefinition = CreateQueueDefinition();
        var subscriptionFactory = new ThrowingQueueSubscriptionFactory();
        var publisher = new ThrowingQueuePublisher();
        var queue = new MongoQueue<TestPayload>(queueDefinition, subscriptionFactory, publisher);
        await queue.DisposeAsync();

        // Act
        var act = async () => await queue.DisposeAsync();

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Test]
    public async Task DisposeAsync_WhenBothQueueInterfacesAreResolvedFromHost_DoesNotThrow()
    {
        // Arrange
        var host = new HostBuilder().ConfigureServices(services => services
                                                                   .AddMongo("mongodb://localhost/Unused")
                                                                   .WithQueue<TestPayload>(queue => queue
                                                                                                    .WithPayloadHandler<NoOpPayloadHandler>()
                                                                                                    .WithCollectionName("test-queue")
                                                                                                    .WithoutAutoStartSubscription()))
                                    .Build();

        host.Services.GetRequiredService<IMongoQueue<TestPayload>>();
        host.Services.GetRequiredService<IMongoQueue>();

        // Act
        var act = async () =>
        {
            await host.StartAsync();
            await host.StopAsync();
            host.Dispose();
        };

        // Assert
        await act.Should().NotThrowAsync();
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
    public async Task StartSubscriptionAsync_WithNonDefaultCancellationToken_PassesItToSubscriptionFactory()
    {
        // Arrange
        var queueDefinition = CreateQueueDefinition();
        var subscription = new RecordingQueueSubscription();
        var subscriptionFactory = new RecordingQueueSubscriptionFactory(subscription);
        var publisher = new ThrowingQueuePublisher();
        using var cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = cancellationTokenSource.Token;

        var queue = new MongoQueue<TestPayload>(queueDefinition, subscriptionFactory, publisher);

        // Act
        await queue.StartSubscriptionAsync(cancellationToken);

        // Assert
        subscriptionFactory.ReceivedQueueDefinition.Should().BeSameAs(queueDefinition);
        subscriptionFactory.ReceivedCancellationToken.Should().Be(cancellationToken);
        subscriptionFactory.CreateAndRunCallCount.Should().Be(1);
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
            BatchSize = 1,
            PayloadHandlerType = typeof(IMongoQueuePayloadHandler<TestPayload>),
            AutoStartSubscription = false
        };

    public class TestPayload;

    public class WrongPayload;

    /// <summary>
    /// Hand-crafted double for <see cref="IMongoQueueSubscription{TPayload}"/> that counts stop and dispose
    /// calls and gates <see cref="StopAsync"/> on <see cref="ReleaseStop"/>, so a test can force two
    /// <see cref="MongoQueue{TPayload}.DisposeAsync"/> calls to genuinely overlap and still verify the
    /// subscription is torn down at most once.
    /// </summary>
    private sealed class CountingQueueSubscription : IMongoQueueSubscription<TestPayload>
    {
        private readonly TaskCompletionSource _releaseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stopEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Int32 _disposeCalls;
        private Int32 _stopCalls;

        public Int32 DisposeCalls => _disposeCalls;

        public Int32 StopCalls => _stopCalls;

        /// <summary>
        /// Completes once <see cref="StopAsync"/> has been entered, letting a test assert the first disposal
        /// is genuinely still in progress before starting the second one.
        /// </summary>
        public Task StopEntered => _stopEntered.Task;

        public Boolean IsActive => true;

        public void ReleaseStop() => _releaseGate.TrySetResult();

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            return ValueTask.CompletedTask;
        }

        public Task StartAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _stopCalls);
            _stopEntered.TrySetResult();
            await _releaseGate.Task;
        }
    }

    /// <summary>
    /// Hand-crafted no-op stub for <see cref="IMongoQueuePayloadHandler{TPayload}"/>. Never exercised: the
    /// test that registers it through DI keeps the queue's subscription from auto-starting.
    /// </summary>
    private sealed class NoOpPayloadHandler : IMongoQueuePayloadHandler<TestPayload>
    {
        public Task HandlePayloadAsync(TestPayload payload, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>
    /// Minimal hand-crafted stub for <see cref="IMongoQueueSubscription{TPayload}"/>. None of its members are
    /// exercised by the test that uses it; the subscription is only ever created and stored.
    /// </summary>
    private sealed class RecordingQueueSubscription : IMongoQueueSubscription<TestPayload>
    {
        public Boolean IsActive => throw new NotSupportedException();

        public ValueTask DisposeAsync() => throw new NotSupportedException();

        public Task StartAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task StopAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>
    /// Hand-crafted recording double for <see cref="IMongoQueueSubscriptionFactory"/>, used instead of a
    /// mocking framework per tests/AGENTS.md for this newly added test.
    /// </summary>
    private sealed class RecordingQueueSubscriptionFactory : IMongoQueueSubscriptionFactory
    {
        private readonly IMongoQueueSubscription<TestPayload> _subscription;

        public RecordingQueueSubscriptionFactory(IMongoQueueSubscription<TestPayload> subscription) => _subscription = subscription;

        public Int32 CreateAndRunCallCount { get; private set; }

        public CancellationToken ReceivedCancellationToken { get; private set; }

        public MongoQueueDefinition? ReceivedQueueDefinition { get; private set; }

        public Task<IMongoQueueSubscription<TPayload>> CreateAndRunAsync<TPayload>(MongoQueueDefinition queueDefinition, CancellationToken cancellationToken = default)
            where TPayload : class, new()
        {
            ReceivedQueueDefinition = queueDefinition;
            ReceivedCancellationToken = cancellationToken;
            CreateAndRunCallCount++;
            return Task.FromResult((IMongoQueueSubscription<TPayload>)_subscription);
        }
    }

    /// <summary>
    /// Hand-crafted single-use double for <see cref="IMongoQueueSubscriptionFactory"/> that always returns the
    /// given subscription, used to verify disposal tears down the subscription at most once.
    /// </summary>
    private sealed class SingleUseQueueSubscriptionFactory : IMongoQueueSubscriptionFactory
    {
        private readonly IMongoQueueSubscription<TestPayload> _subscription;

        public SingleUseQueueSubscriptionFactory(IMongoQueueSubscription<TestPayload> subscription) => _subscription = subscription;

        public Task<IMongoQueueSubscription<TPayload>> CreateAndRunAsync<TPayload>(MongoQueueDefinition queueDefinition, CancellationToken cancellationToken = default)
            where TPayload : class, new()
            => Task.FromResult((IMongoQueueSubscription<TPayload>)_subscription);
    }

    /// <summary>
    /// Hand-crafted stub for <see cref="IMongoQueuePublisher"/>. Not exercised by the test that uses it.
    /// </summary>
    private sealed class ThrowingQueuePublisher : IMongoQueuePublisher
    {
        public Task PublishAsync<TPayload>(MongoQueueDefinition queueDefinition, TPayload payload, CancellationToken cancellationToken)
            where TPayload : class, new()
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Hand-crafted stub for <see cref="IMongoQueueSubscriptionFactory"/>. Not exercised by the test that uses
    /// it, which never starts a subscription before disposing the queue.
    /// </summary>
    private sealed class ThrowingQueueSubscriptionFactory : IMongoQueueSubscriptionFactory
    {
        public Task<IMongoQueueSubscription<TPayload>> CreateAndRunAsync<TPayload>(MongoQueueDefinition queueDefinition, CancellationToken cancellationToken = default)
            where TPayload : class, new()
            => throw new NotSupportedException();
    }
}
