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
        ContainsLeaseRecoveryBranch(rendered).Should().BeTrue(
            "the lease-recovery branch must require IsLocked == true in the same clause branch as the LockedUtc recovery predicate");
    }

    [Test]
    public async Task HandleFailedQueueItemAsync_LogsReadableAttemptMessage()
    {
        // Arrange
        using var metricsCollector = new QueueMetricsCollector();
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
        VerifyLoggedError(loggerMock, "will be retried after the lock lease expires", Times.Once());
        var measurement = metricsCollector.Measurements.Should()
                                          .ContainSingle(x => x.InstrumentName == MongoQueueMetrics.Instruments.ProcessingFailed)
                                          .Subject;
        measurement.Tags[MongoQueueMetrics.Tags.Result].Should().Be("retry");
        measurement.Tags[MongoQueueMetrics.Tags.QueueCollection].Should().Be("test-queue");
    }

    [Test]
    public async Task HandleFailedQueueItemAsync_WhenRetryBudgetIsExhausted_EmitsTerminalFailureMetric()
    {
        // Arrange
        using var metricsCollector = new QueueMetricsCollector();
        var queueItemId = ObjectId.GenerateNewId();
        var acquiredLockUtc = DateTime.UtcNow;
        var loggerMock = new Mock<ILogger<MongoQueueSubscription<TestPayload>>>();
        var collectionMock = new Mock<IMongoCollection<MongoQueueItem<TestPayload>>>();
        var updateResultMock = new Mock<UpdateResult>();
        updateResultMock.SetupGet(x => x.ModifiedCount).Returns(1);
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
                RetryCount = 2
            });
        collectionMock
            .Setup(c => c.UpdateOneAsync(
                       It.IsAny<FilterDefinition<MongoQueueItem<TestPayload>>>(),
                       It.IsAny<UpdateDefinition<MongoQueueItem<TestPayload>>>(),
                       It.IsAny<UpdateOptions>(),
                       It.IsAny<CancellationToken>()))
            .ReturnsAsync(updateResultMock.Object);

        var sut = CreateSubscription(new()
                                     {
                                         AutoStartSubscription = false,
                                         CollectionName = "test-queue",
                                         LockLeaseTime = TimeSpan.FromMinutes(1),
                                         MaxRetries = 1,
                                         PayloadHandlerType = typeof(IMongoQueuePayloadHandler<TestPayload>),
                                         PayloadType = typeof(TestPayload),
                                         QueryLimit = 10
                                     },
                                     logger: loggerMock.Object);
        var method = GetPrivateMethod("HandleFailedQueueItemAsync",
        [
            typeof(IMongoCollection<MongoQueueItem<TestPayload>>), typeof(ObjectId), typeof(DateTime), typeof(Exception), typeof(CancellationToken)
        ]);

        // Act
        await (Task)method.Invoke(sut, [
            collectionMock.Object, queueItemId, acquiredLockUtc, new InvalidOperationException("boom"), CancellationToken.None
        ])!;

        // Assert
        VerifyLoggedError(loggerMock, "Marking queue item", Times.Once());
        var measurement = metricsCollector.Measurements.Should()
                                          .ContainSingle(x => x.InstrumentName == MongoQueueMetrics.Instruments.ProcessingFailed)
                                          .Subject;
        measurement.Tags[MongoQueueMetrics.Tags.Result].Should().Be("terminal");
    }

    [Test]
    public async Task ProcessQueueItemAsync_WhenCompletionOwnershipChanges_DoesNotEmitSuccessMetrics()
    {
        // Arrange
        using var metricsCollector = new QueueMetricsCollector();
        var queueItemId = ObjectId.GenerateNewId();
        var loggerMock = new Mock<ILogger<MongoQueueSubscription<TestPayload>>>();
        var collectionMock = new Mock<IMongoCollection<MongoQueueItem<TestPayload>>>();
        var payloadHandlerMock = new Mock<IMongoQueuePayloadHandler<TestPayload>>();
        var payloadHandlerFactoryMock = new Mock<IMongoQueuePayloadHandlerFactory>();
        var updateResultMock = new Mock<UpdateResult>();
        updateResultMock.SetupGet(x => x.ModifiedCount).Returns(0);
        payloadHandlerMock.Setup(x => x.HandlePayloadAsync(It.IsAny<TestPayload>(), It.IsAny<CancellationToken>()))
                          .Returns(Task.CompletedTask);
        payloadHandlerFactoryMock.Setup(x => x.CreateHandler<TestPayload>()).Returns(payloadHandlerMock.Object);
        collectionMock
            .Setup(c => c.FindOneAndUpdateAsync(
                       It.IsAny<FilterDefinition<MongoQueueItem<TestPayload>>>(),
                       It.IsAny<UpdateDefinition<MongoQueueItem<TestPayload>>>(),
                       It.IsAny<FindOneAndUpdateOptions<MongoQueueItem<TestPayload>, MongoQueueItem<TestPayload>>>(),
                       It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MongoQueueItem<TestPayload>
            {
                Id = queueItemId,
                CreatedUtc = DateTime.UtcNow.AddMinutes(-1),
                Payload = new(),
                RetryCount = 1
            });
        collectionMock
            .Setup(c => c.UpdateOneAsync(
                       It.IsAny<FilterDefinition<MongoQueueItem<TestPayload>>>(),
                       It.IsAny<UpdateDefinition<MongoQueueItem<TestPayload>>>(),
                       It.IsAny<UpdateOptions>(),
                       It.IsAny<CancellationToken>()))
            .ReturnsAsync(updateResultMock.Object);

        var sut = CreateSubscription(payloadHandlerFactory: payloadHandlerFactoryMock.Object, logger: loggerMock.Object);
        var method = GetPrivateMethod("ProcessQueueItemAsync",
        [
            typeof(IMongoCollection<MongoQueueItem<TestPayload>>), typeof(ObjectId), typeof(CancellationToken)
        ]);

        // Act
        await (Task)method.Invoke(sut, [collectionMock.Object, queueItemId, CancellationToken.None])!;

        // Assert
        VerifyLoggedWarning(loggerMock, "Skipping completion", Times.Once());
        VerifyLoggedInformation(loggerMock, "Processed queue item", Times.Never());
        metricsCollector.Measurements.Should().NotContain(x => x.InstrumentName == MongoQueueMetrics.Instruments.ProcessingSucceeded);
        metricsCollector.Measurements.Should().NotContain(x => x.InstrumentName == MongoQueueMetrics.Instruments.ProcessingDuration);
        metricsCollector.Measurements.Should().NotContain(x => x.InstrumentName == MongoQueueMetrics.Instruments.ProcessingQueueAge);
    }

    [Test]
    public async Task ProcessQueueItemAsync_WhenImmediateDeleteCompletionOwnershipChanges_DoesNotEmitSuccessMetrics()
    {
        // Arrange
        using var metricsCollector = new QueueMetricsCollector();
        var queueItemId = ObjectId.GenerateNewId();
        var loggerMock = new Mock<ILogger<MongoQueueSubscription<TestPayload>>>();
        var collectionMock = new Mock<IMongoCollection<MongoQueueItem<TestPayload>>>();
        var payloadHandlerMock = new Mock<IMongoQueuePayloadHandler<TestPayload>>();
        var payloadHandlerFactoryMock = new Mock<IMongoQueuePayloadHandlerFactory>();
        var deleteResultMock = new Mock<DeleteResult>();
        deleteResultMock.SetupGet(x => x.DeletedCount).Returns(0);
        payloadHandlerMock.Setup(x => x.HandlePayloadAsync(It.IsAny<TestPayload>(), It.IsAny<CancellationToken>()))
                          .Returns(Task.CompletedTask);
        payloadHandlerFactoryMock.Setup(x => x.CreateHandler<TestPayload>()).Returns(payloadHandlerMock.Object);
        collectionMock
            .Setup(c => c.FindOneAndUpdateAsync(
                       It.IsAny<FilterDefinition<MongoQueueItem<TestPayload>>>(),
                       It.IsAny<UpdateDefinition<MongoQueueItem<TestPayload>>>(),
                       It.IsAny<FindOneAndUpdateOptions<MongoQueueItem<TestPayload>, MongoQueueItem<TestPayload>>>(),
                       It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MongoQueueItem<TestPayload>
            {
                Id = queueItemId,
                CreatedUtc = DateTime.UtcNow.AddMinutes(-1),
                Payload = new(),
                RetryCount = 1
            });
        collectionMock
            .Setup(c => c.DeleteOneAsync(
                       It.IsAny<FilterDefinition<MongoQueueItem<TestPayload>>>(),
                       It.IsAny<CancellationToken>()))
            .ReturnsAsync(deleteResultMock.Object);

        var sut = CreateSubscription(new()
                                     {
                                         AutoStartSubscription = false,
                                         ClosedItemRetention = null,
                                         CollectionName = "test-queue",
                                         LockLeaseTime = TimeSpan.FromMinutes(1),
                                         PayloadHandlerType = typeof(IMongoQueuePayloadHandler<TestPayload>),
                                         PayloadType = typeof(TestPayload),
                                         QueryLimit = 10
                                     },
                                     payloadHandlerFactory: payloadHandlerFactoryMock.Object,
                                     logger: loggerMock.Object);
        var method = GetPrivateMethod("ProcessQueueItemAsync",
        [
            typeof(IMongoCollection<MongoQueueItem<TestPayload>>), typeof(ObjectId), typeof(CancellationToken)
        ]);

        // Act
        await (Task)method.Invoke(sut, [collectionMock.Object, queueItemId, CancellationToken.None])!;

        // Assert
        VerifyLoggedWarning(loggerMock, "Skipping completion", Times.Once());
        VerifyLoggedInformation(loggerMock, "Processed queue item", Times.Never());
        metricsCollector.Measurements.Should().NotContain(x => x.InstrumentName == MongoQueueMetrics.Instruments.ProcessingSucceeded);
        metricsCollector.Measurements.Should().NotContain(x => x.InstrumentName == MongoQueueMetrics.Instruments.ProcessingDuration);
        metricsCollector.Measurements.Should().NotContain(x => x.InstrumentName == MongoQueueMetrics.Instruments.ProcessingQueueAge);
    }

    [Test]
    public async Task ProcessQueueItemAsync_WithRecoveredLock_EmitsRecoveryAndSuccessDiagnostics()
    {
        // Arrange
        using var metricsCollector = new QueueMetricsCollector();
        var queueItemId = ObjectId.GenerateNewId();
        var now = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var recoveredLockUtc = now.UtcDateTime.AddMinutes(-2);
        var createdUtc = now.UtcDateTime.AddMinutes(-5);
        var loggerMock = new Mock<ILogger<MongoQueueSubscription<TestPayload>>>();
        var collectionMock = new Mock<IMongoCollection<MongoQueueItem<TestPayload>>>();
        var payloadHandlerMock = new Mock<IMongoQueuePayloadHandler<TestPayload>>();
        var payloadHandlerFactoryMock = new Mock<IMongoQueuePayloadHandlerFactory>();
        var updateResultMock = new Mock<UpdateResult>();
        var timeProviderMock = new Mock<TimeProvider>();
        updateResultMock.SetupGet(x => x.ModifiedCount).Returns(1);
        timeProviderMock.Setup(x => x.GetUtcNow()).Returns(now);
        payloadHandlerMock.Setup(x => x.HandlePayloadAsync(It.IsAny<TestPayload>(), It.IsAny<CancellationToken>()))
                          .Returns(Task.CompletedTask);
        payloadHandlerFactoryMock.Setup(x => x.CreateHandler<TestPayload>()).Returns(payloadHandlerMock.Object);
        collectionMock
            .Setup(c => c.FindOneAndUpdateAsync(
                       It.IsAny<FilterDefinition<MongoQueueItem<TestPayload>>>(),
                       It.IsAny<UpdateDefinition<MongoQueueItem<TestPayload>>>(),
                       It.IsAny<FindOneAndUpdateOptions<MongoQueueItem<TestPayload>, MongoQueueItem<TestPayload>>>(),
                       It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MongoQueueItem<TestPayload>
            {
                Id = queueItemId,
                CreatedUtc = createdUtc,
                IsLocked = true,
                LockedUtc = recoveredLockUtc,
                Payload = new(),
                RetryCount = 1
            });
        collectionMock
            .Setup(c => c.UpdateOneAsync(
                       It.IsAny<FilterDefinition<MongoQueueItem<TestPayload>>>(),
                       It.IsAny<UpdateDefinition<MongoQueueItem<TestPayload>>>(),
                       It.IsAny<UpdateOptions>(),
                       It.IsAny<CancellationToken>()))
            .ReturnsAsync(updateResultMock.Object);

        var sut = CreateSubscription(payloadHandlerFactory: payloadHandlerFactoryMock.Object,
                                     timeProvider: timeProviderMock.Object,
                                     logger: loggerMock.Object);
        var method = GetPrivateMethod("ProcessQueueItemAsync",
        [
            typeof(IMongoCollection<MongoQueueItem<TestPayload>>), typeof(ObjectId), typeof(CancellationToken)
        ]);

        // Act
        await (Task)method.Invoke(sut, [collectionMock.Object, queueItemId, CancellationToken.None])!;

        // Assert
        VerifyLoggedWarning(loggerMock, "Recovering queue item lock", Times.Once());
        VerifyLoggedInformation(loggerMock, "Processed queue item", Times.Once());
        metricsCollector.Measurements.Should().Contain(x => x.InstrumentName == MongoQueueMetrics.Instruments.LockRecovered);
        metricsCollector.Measurements.Should().Contain(x => x.InstrumentName == MongoQueueMetrics.Instruments.LockRecoveryAge);
        var successMeasurement = metricsCollector.Measurements.Should()
                                                 .ContainSingle(x => x.InstrumentName == MongoQueueMetrics.Instruments.ProcessingSucceeded)
                                                 .Subject;
        successMeasurement.Tags[MongoQueueMetrics.Tags.CleanupMode].Should().Be("ttl-retention");
        successMeasurement.Tags[MongoQueueMetrics.Tags.QueueCollection].Should().Be("test-queue");
        metricsCollector.Measurements.Should().Contain(x => x.InstrumentName == MongoQueueMetrics.Instruments.ProcessingDuration);
        metricsCollector.Measurements.Should().Contain(x => x.InstrumentName == MongoQueueMetrics.Instruments.ProcessingQueueAge);
    }

    private static Boolean BranchHasLockedRecoveryClause(BsonDocument document)
        => SubtreeContainsLockedState(document) &&
           SubtreeContainsLeaseRecoveryLockedUtcClause(document);

    private static Boolean ContainsEquality(BsonValue value, String field, BsonValue expected)
        => value switch
        {
            BsonDocument document => (document.TryGetValue(field, out var directValue) &&
                                      directValue == expected) ||
                                     document.Elements.Any(element => ContainsEquality(element.Value, field, expected)),
            BsonArray array => array.Any(item => ContainsEquality(item, field, expected)),
            _ => false
        };

    private static Boolean ContainsLeaseRecoveryBranch(BsonValue value)
        => value switch
        {
            BsonDocument document => document.Elements.Any(element => ContainsLeaseRecoveryBranch(element.Value)),
            BsonArray array => array.Any(item => item is BsonDocument document &&
                                                 (BranchHasLockedRecoveryClause(document) ||
                                                  ContainsLeaseRecoveryBranch(document))),
            _ => false
        };

    private static MongoQueueSubscription<TestPayload> CreateSubscription(
        MongoQueueDefinition? queueDefinition = null,
        IMongoHelper? mongoHelper = null,
        IMongoQueuePayloadHandlerFactory? payloadHandlerFactory = null,
        IMongoQueuePayloadPrioritizer? payloadPrioritizer = null,
        TimeProvider? timeProvider = null,
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
               mongoHelper ?? Mock.Of<IMongoHelper>(),
               payloadHandlerFactory ?? Mock.Of<IMongoQueuePayloadHandlerFactory>(),
               payloadPrioritizer ?? Mock.Of<IMongoQueuePayloadPrioritizer>(),
               timeProvider ?? TimeProvider.System,
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

    private static Boolean SubtreeContainsLeaseRecoveryLockedUtcClause(BsonValue value)
        => value switch
        {
            BsonDocument document => (document.TryGetValue(nameof(MongoQueueItem.LockedUtc), out var lockedUtcValue) &&
                                      (lockedUtcValue == BsonNull.Value ||
                                       (lockedUtcValue is BsonDocument lockedUtcDocument && lockedUtcDocument.Contains("$lt")))) ||
                                     document.Elements.Any(element => SubtreeContainsLeaseRecoveryLockedUtcClause(element.Value)),
            BsonArray array => array.Any(SubtreeContainsLeaseRecoveryLockedUtcClause),
            _ => false
        };

    private static Boolean SubtreeContainsLockedState(BsonValue value)
        => value switch
        {
            BsonDocument document => (document.TryGetValue(nameof(MongoQueueItem.IsLocked), out var isLockedValue) &&
                                      isLockedValue == BsonBoolean.True) ||
                                     document.Elements.Any(element => SubtreeContainsLockedState(element.Value)),
            BsonArray array => array.Any(SubtreeContainsLockedState),
            _ => false
        };

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

    private static void VerifyLoggedInformation(Mock<ILogger<MongoQueueSubscription<TestPayload>>> loggerMock,
                                                String messageSubstring,
                                                Times times)
        => loggerMock.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains(messageSubstring)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, String>>()),
            times);

    private static void VerifyLoggedWarning(Mock<ILogger<MongoQueueSubscription<TestPayload>>> loggerMock,
                                            String messageSubstring,
                                            Times times)
        => loggerMock.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains(messageSubstring)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, String>>()),
            times);

    public class TestPayload;
}
