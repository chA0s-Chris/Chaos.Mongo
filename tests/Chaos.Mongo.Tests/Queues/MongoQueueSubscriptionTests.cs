// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests.Queues;

using Chaos.Mongo.Queues;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Moq;
using NUnit.Framework;
using System.Reflection;

public class MongoQueueSubscriptionTests
{
    [Test]
    public void CreateAvailableQueueItemFilter_ExplicitlyExcludesTerminalItems()
    {
        // Arrange
        var sut = CreateSubscription();
        var method = GetPrivateMethod("CreateAvailableQueueItemFilter", Type.EmptyTypes);
        var serializerRegistry = BsonSerializer.SerializerRegistry;
        var documentSerializer = serializerRegistry.GetSerializer<MongoQueueItem<TestPayload>>();
        var renderContext = new RenderArgs<MongoQueueItem<TestPayload>>(documentSerializer, serializerRegistry);

        // Act
        var filter = (FilterDefinition<MongoQueueItem<TestPayload>>)method.Invoke(sut, [])!;
        var rendered = filter.Render(renderContext);

        // Assert
        ContainsFieldValue(rendered, nameof(MongoQueueItem.IsTerminal), BsonBoolean.False).Should().BeTrue();
    }

    [Test]
    public async Task HandleFailedQueueItemAsync_LogsReadableAttemptMessage()
    {
        // Arrange
        var queueItemId = ObjectId.GenerateNewId();
        var acquiredLockUtc = DateTime.UtcNow;
        var loggerMock = new Mock<ILogger<MongoQueueSubscription<TestPayload>>>();
        var collectionMock = new Mock<IMongoCollection<MongoQueueItem<TestPayload>>>();
        collectionMock
            .Setup(c => c.FindOneAndUpdateAsync(
                       It.IsAny<FilterDefinition<MongoQueueItem<TestPayload>>>(),
                       It.IsAny<UpdateDefinition<MongoQueueItem<TestPayload>>>(),
                       It.IsAny<FindOneAndUpdateOptions<MongoQueueItem<TestPayload>, MongoQueueItem<TestPayload>>>(),
                       It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MongoQueueItem<TestPayload>
            {
                Id = queueItemId,
                CreatedUtc = acquiredLockUtc,
                Payload = new(),
                RetryCount = 1
            });

        var sut = CreateSubscription(logger: loggerMock.Object);
        var method = GetPrivateMethod("HandleFailedQueueItemAsync",
        [
            typeof(IMongoCollection<MongoQueueItem<TestPayload>>), typeof(ObjectId), typeof(DateTime), typeof(Exception), typeof(CancellationToken)
        ]);

        // Act
        await (Task)method.Invoke(sut, [
            collectionMock.Object, queueItemId, acquiredLockUtc, new InvalidOperationException("boom"), CancellationToken.None
        ])!;

        // Assert
        VerifyLoggedError(loggerMock, "failed on attempt 1", Times.Once());
        VerifyLoggedError(loggerMock, "failed on failed attempt", Times.Never());
    }

    private static Boolean ContainsFieldValue(BsonValue value, String fieldName, BsonValue expectedValue)
        => value switch
        {
            BsonDocument document => document.Elements.Any(element =>
                                                               (element.Name == fieldName && element.Value == expectedValue) ||
                                                               ContainsFieldValue(element.Value, fieldName, expectedValue)),
            BsonArray array => array.Any(item => ContainsFieldValue(item, fieldName, expectedValue)),
            _ => false
        };

    private static MongoQueueSubscription<TestPayload> CreateSubscription(
        MongoQueueDefinition? queueDefinition = null,
        ILogger<MongoQueueSubscription<TestPayload>>? logger = null)
        => new(queueDefinition ?? new MongoQueueDefinition
               {
                   AutoStartSubscription = false,
                   CollectionName = "test-queue",
                   LockLeaseTime = TimeSpan.FromMinutes(1),
                   PayloadHandlerType = typeof(IMongoQueuePayloadHandler<TestPayload>),
                   PayloadType = typeof(TestPayload),
                   QueryLimit = 10
               },
               Mock.Of<IMongoHelper>(),
               Mock.Of<IMongoQueuePayloadHandlerFactory>(),
               Mock.Of<IMongoQueuePayloadPrioritizer>(),
               TimeProvider.System,
               logger ?? Mock.Of<ILogger<MongoQueueSubscription<TestPayload>>>());

    private static MethodInfo GetPrivateMethod(String name, Type[] parameterTypes)
        => typeof(MongoQueueSubscription<TestPayload>).GetMethod(name,
                                                                 BindingFlags.Instance | BindingFlags.NonPublic,
                                                                 null,
                                                                 parameterTypes,
                                                                 null)
           ?? throw new InvalidOperationException($"Could not find method {name}.");

    private static void VerifyLoggedError(Mock<ILogger<MongoQueueSubscription<TestPayload>>> loggerMock,
                                          String messageSubstring,
                                          Times times)
        => loggerMock.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains(messageSubstring)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, String>>()),
            times);

    public class TestPayload;
}
