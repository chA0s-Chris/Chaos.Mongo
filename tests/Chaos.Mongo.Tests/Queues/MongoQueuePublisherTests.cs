// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests.Queues;

using Chaos.Mongo.Queues;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using NUnit.Framework;
using MongoDefaults = MongoDefaults;

public class MongoQueuePublisherTests
{
    [Test]
    public void Constructor_WhenMongoHelperIsNull_ThrowsArgumentNullException()
    {
        // Arrange
        var timeProvider = TimeProvider.System;

        // Act
        var act = () => new MongoQueuePublisher(null!, timeProvider);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Constructor_WhenTimeProviderIsNull_ThrowsArgumentNullException()
    {
        // Arrange
        var mockMongoHelper = new Mock<IMongoHelper>();

        // Act
        var act = () => new MongoQueuePublisher(mockMongoHelper.Object, null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public async Task PublishAsync_WhenCollectionNameIsEmpty_ThrowsInvalidOperationException()
    {
        // Arrange
        var mockMongoHelper = new Mock<IMongoHelper>();
        var timeProvider = TimeProvider.System;
        var publisher = new MongoQueuePublisher(mockMongoHelper.Object, timeProvider);
        var queueDefinition = new MongoQueueDefinition
        {
            CollectionName = String.Empty, // Empty collection name
            LockLeaseTime = MongoDefaults.QueueLockLeaseTime,
            PayloadType = typeof(TestPayload),
            QueryLimit = 1,
            PayloadHandlerType = typeof(IMongoQueuePayloadHandler<TestPayload>),
            AutoStartSubscription = false
        };
        var payload = new TestPayload();

        // Act
        var act = async () => await publisher.PublishAsync(queueDefinition, payload, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
                 .WithMessage("Queue collection name is empty.");
    }

    [Test]
    public async Task PublishAsync_WhenCollectionNameIsNull_ThrowsInvalidOperationException()
    {
        // Arrange
        var mockMongoHelper = new Mock<IMongoHelper>();
        var timeProvider = TimeProvider.System;
        var publisher = new MongoQueuePublisher(mockMongoHelper.Object, timeProvider);
        var queueDefinition = new MongoQueueDefinition
        {
            CollectionName = null!, // Null collection name
            LockLeaseTime = MongoDefaults.QueueLockLeaseTime,
            PayloadType = typeof(TestPayload),
            QueryLimit = 1,
            PayloadHandlerType = typeof(IMongoQueuePayloadHandler<TestPayload>),
            AutoStartSubscription = false
        };
        var payload = new TestPayload();

        // Act
        var act = async () => await publisher.PublishAsync(queueDefinition, payload, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
                 .WithMessage("Queue collection name is empty.");
    }

    [Test]
    public async Task PublishAsync_WhenPayloadIsNull_ThrowsArgumentNullException()
    {
        // Arrange
        var mockMongoHelper = new Mock<IMongoHelper>();
        var timeProvider = TimeProvider.System;
        var publisher = new MongoQueuePublisher(mockMongoHelper.Object, timeProvider);
        var queueDefinition = new MongoQueueDefinition
        {
            CollectionName = "test-queue",
            LockLeaseTime = MongoDefaults.QueueLockLeaseTime,
            PayloadType = typeof(TestPayload),
            QueryLimit = 1,
            PayloadHandlerType = typeof(IMongoQueuePayloadHandler<TestPayload>),
            AutoStartSubscription = false
        };

        // Act
        var act = async () => await publisher.PublishAsync<TestPayload>(queueDefinition, null!, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Test]
    public async Task PublishAsync_WhenPayloadTypeMismatch_ThrowsInvalidOperationException()
    {
        // Arrange
        var mockMongoHelper = new Mock<IMongoHelper>();
        var timeProvider = TimeProvider.System;
        var publisher = new MongoQueuePublisher(mockMongoHelper.Object, timeProvider);
        var queueDefinition = new MongoQueueDefinition
        {
            CollectionName = "test-queue",
            LockLeaseTime = MongoDefaults.QueueLockLeaseTime,
            PayloadType = typeof(AnotherTestPayload), // Different type
            QueryLimit = 1,
            PayloadHandlerType = typeof(IMongoQueuePayloadHandler<TestPayload>),
            AutoStartSubscription = false
        };
        var payload = new TestPayload();

        // Act
        var act = async () => await publisher.PublishAsync(queueDefinition, payload, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
                 .WithMessage("Payload type * does not match queue payload type *");
    }

    [Test]
    public async Task PublishAsync_WhenQueueDefinitionIsNull_ThrowsArgumentNullException()
    {
        // Arrange
        var mockMongoHelper = new Mock<IMongoHelper>();
        var timeProvider = TimeProvider.System;
        var publisher = new MongoQueuePublisher(mockMongoHelper.Object, timeProvider);
        var payload = new TestPayload();

        // Act
        var act = async () => await publisher.PublishAsync(null!, payload, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Test]
    public async Task PublishAsync_WithCancellationToken_PassesCancellationTokenToCollection()
    {
        // Arrange
        var mockCollection = new Mock<IMongoCollection<MongoQueueItem<TestPayload>>>();
        var mockDatabase = new Mock<IMongoDatabase>();
        var mockMongoHelper = new Mock<IMongoHelper>();
        var timeProvider = TimeProvider.System;

        mockDatabase.Setup(x => x.GetCollection<MongoQueueItem<TestPayload>>("test-queue", null))
                    .Returns(mockCollection.Object);
        mockMongoHelper.Setup(x => x.Database).Returns(mockDatabase.Object);

        var publisher = new MongoQueuePublisher(mockMongoHelper.Object, timeProvider);
        var queueDefinition = new MongoQueueDefinition
        {
            CollectionName = "test-queue",
            LockLeaseTime = MongoDefaults.QueueLockLeaseTime,
            PayloadType = typeof(TestPayload),
            QueryLimit = 1,
            PayloadHandlerType = typeof(IMongoQueuePayloadHandler<TestPayload>),
            AutoStartSubscription = false
        };
        var payload = new TestPayload();
        var cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = cancellationTokenSource.Token;

        mockCollection.Setup(x => x.InsertOneAsync(
                                 It.IsAny<MongoQueueItem<TestPayload>>(),
                                 null,
                                 It.IsAny<CancellationToken>()))
                      .Returns(Task.CompletedTask);

        // Act
        await publisher.PublishAsync(queueDefinition, payload, cancellationToken);

        // Assert
        mockCollection.Verify(x => x.InsertOneAsync(
                                  It.IsAny<MongoQueueItem<TestPayload>>(),
                                  null,
                                  cancellationToken), Times.Once);
    }

    [Test]
    public async Task PublishAsync_WithValidParameters_InsertsQueueItem()
    {
        // Arrange
        var mockCollection = new Mock<IMongoCollection<MongoQueueItem<TestPayload>>>();
        var mockDatabase = new Mock<IMongoDatabase>();
        var mockMongoHelper = new Mock<IMongoHelper>();
        var testTime = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var mockTimeProvider = new Mock<TimeProvider>();
        mockTimeProvider.Setup(x => x.GetUtcNow()).Returns(new DateTimeOffset(testTime));

        mockDatabase.Setup(x => x.GetCollection<MongoQueueItem<TestPayload>>("test-queue", null))
                    .Returns(mockCollection.Object);
        mockMongoHelper.Setup(x => x.Database).Returns(mockDatabase.Object);

        var publisher = new MongoQueuePublisher(mockMongoHelper.Object, mockTimeProvider.Object);
        var queueDefinition = new MongoQueueDefinition
        {
            CollectionName = "test-queue",
            LockLeaseTime = MongoDefaults.QueueLockLeaseTime,
            PayloadType = typeof(TestPayload),
            QueryLimit = 1,
            PayloadHandlerType = typeof(IMongoQueuePayloadHandler<TestPayload>),
            AutoStartSubscription = false
        };
        var payload = new TestPayload
        {
            Value = "test"
        };
        MongoQueueItem<TestPayload>? capturedItem = null;

        mockCollection.Setup(x => x.InsertOneAsync(
                                 It.IsAny<MongoQueueItem<TestPayload>>(),
                                 null,
                                 It.IsAny<CancellationToken>()))
                      .Callback<MongoQueueItem<TestPayload>, InsertOneOptions, CancellationToken>((item, _, _) => capturedItem = item)
                      .Returns(Task.CompletedTask);

        // Act
        await publisher.PublishAsync(queueDefinition, payload, CancellationToken.None);

        // Assert
        mockCollection.Verify(x => x.InsertOneAsync(
                                  It.IsAny<MongoQueueItem<TestPayload>>(),
                                  null,
                                  It.IsAny<CancellationToken>()), Times.Once);

        capturedItem.Should().NotBeNull();
        capturedItem!.Payload.Should().BeSameAs(payload);
        capturedItem.CreatedUtc.Should().Be(testTime);
        capturedItem.Id.Should().NotBe(ObjectId.Empty);
        capturedItem.RetryCount.Should().Be(0);
        capturedItem.IsTerminal.Should().BeFalse();
    }

    public class AnotherTestPayload;

    public class TestPayload
    {
        public String? Value { get; set; }
    }
}
