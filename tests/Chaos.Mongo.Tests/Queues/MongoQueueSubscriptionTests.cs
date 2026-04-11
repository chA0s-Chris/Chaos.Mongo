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
    public void CreateAvailableQueueItemFilter_TreatsMissingIsTerminalAsNonTerminal()
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
        UsesLegacyCompatibleTerminalFilter(rendered)
            .Should()
            .BeTrue("the availability filter must exclude terminal items while still matching legacy queue items without IsTerminal");
    }

    [Test]
    public void CreateAvailableQueueItemFilter_UsesLeaseRecoveryClausesAlignedWithCompoundIndex()
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
        ContainsEquality(rendered, nameof(MongoQueueItem.IsClosed), BsonBoolean.False).Should().BeTrue();
        ContainsEquality(rendered, nameof(MongoQueueItem.IsLocked), BsonBoolean.False).Should().BeTrue();
        ContainsNullEquality(rendered, nameof(MongoQueueItem.LockedUtc)).Should().BeTrue();
        ContainsComparisonOperator(rendered, nameof(MongoQueueItem.LockedUtc), "$lt").Should().BeTrue();
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

    private static Boolean ContainsComparisonOperator(BsonValue value, String field, String comparisonOperator)
        => value switch
        {
            BsonDocument document => (document.TryGetValue(field, out var filter) &&
                                      filter is BsonDocument filterDocument &&
                                      filterDocument.Contains(comparisonOperator)) ||
                                     document.Elements.Any(element => ContainsComparisonOperator(element.Value, field, comparisonOperator)),
            BsonArray array => array.Any(item => ContainsComparisonOperator(item, field, comparisonOperator)),
            _ => false
        };

    private static Boolean ContainsEquality(BsonValue value, String field, BsonValue expected)
        => value switch
        {
            BsonDocument document => (document.TryGetValue(field, out var directValue) &&
                                      directValue == expected) ||
                                     document.Elements.Any(element => ContainsEquality(element.Value, field, expected)),
            BsonArray array => array.Any(item => ContainsEquality(item, field, expected)),
            _ => false
        };

    private static Boolean ContainsNullEquality(BsonValue value, String field)
        => ContainsEquality(value, field, BsonNull.Value);

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

    private static Boolean IsTerminalFalseClause(BsonValue clause)
        => clause is BsonDocument clauseDocument &&
           clauseDocument.TryGetValue(nameof(MongoQueueItem.IsTerminal), out var filter) &&
           filter == BsonBoolean.False;

    private static Boolean IsTerminalFalseOrMissingFilter(BsonDocument document)
        => document.TryGetValue("$or", out var orFilter) &&
           orFilter is BsonArray clauses &&
           clauses.Any(IsTerminalFalseClause) &&
           clauses.Any(IsTerminalMissingClause);

    private static Boolean IsTerminalFalseOrNullInFilter(BsonDocument document)
        => document.TryGetValue(nameof(MongoQueueItem.IsTerminal), out var filter) &&
           filter is BsonDocument filterDocument &&
           filterDocument.TryGetValue("$in", out var inValues) &&
           inValues is BsonArray values &&
           values.Contains(BsonBoolean.False) &&
           values.Contains(BsonNull.Value);

    private static Boolean IsTerminalMissingClause(BsonValue clause)
        => clause is BsonDocument clauseDocument &&
           clauseDocument.TryGetValue(nameof(MongoQueueItem.IsTerminal), out var filter) &&
           filter is BsonDocument filterDocument &&
           filterDocument.TryGetValue("$exists", out var existsValue) &&
           existsValue == BsonBoolean.False;

    private static Boolean IsTerminalNotTrueFilter(BsonDocument document)
        => document.TryGetValue(nameof(MongoQueueItem.IsTerminal), out var filter) &&
           filter is BsonDocument filterDocument &&
           filterDocument.TryGetValue("$ne", out var notEqualValue) &&
           notEqualValue == BsonBoolean.True;

    private static Boolean UsesLegacyCompatibleTerminalFilter(BsonValue value)
        => value switch
        {
            BsonDocument document => IsTerminalNotTrueFilter(document) ||
                                     IsTerminalFalseOrNullInFilter(document) ||
                                     IsTerminalFalseOrMissingFilter(document) ||
                                     document.Elements.Any(element => UsesLegacyCompatibleTerminalFilter(element.Value)),
            BsonArray array => array.Any(UsesLegacyCompatibleTerminalFilter),
            _ => false
        };

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
