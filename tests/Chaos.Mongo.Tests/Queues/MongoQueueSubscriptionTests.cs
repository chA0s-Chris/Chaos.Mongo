// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests.Queues;

using Chaos.Mongo.Queues;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Search;
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

        var sut = CreateSubscription(logger: loggerMock.Object);
        var method = GetPrivateMethod("HandleFailedQueueItemAsync",
        [
            typeof(IMongoCollection<MongoQueueItem<TestPayload>>), typeof(ObjectId), typeof(DateTime), typeof(Int64), typeof(Exception), typeof(CancellationToken)
        ]);

        // Act: the caller already knows this is attempt 1 — HandleFailedQueueItemAsync no longer re-queries or
        // increments it itself, so no FindOneAndUpdateAsync setup is needed for the retry branch at all.
        await (Task)method.Invoke(sut, [
            collectionMock.Object, queueItemId, acquiredLockUtc, 1L, new InvalidOperationException("boom"), CancellationToken.None
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
            .Setup(c => c.UpdateOneAsync(
                       It.IsAny<FilterDefinition<MongoQueueItem<TestPayload>>>(),
                       It.IsAny<UpdateDefinition<MongoQueueItem<TestPayload>>>(),
                       It.IsAny<UpdateOptions>(),
                       It.IsAny<CancellationToken>()))
            .ReturnsAsync(updateResultMock.Object);

        var sut = CreateSubscription(new MongoQueueDefinition
                                     {
                                         AutoStartSubscription = false,
                                         CollectionName = "test-queue",
                                         LockLeaseTime = TimeSpan.FromMinutes(1),
                                         MaxRetries = 1,
                                         PayloadHandlerType = typeof(IMongoQueuePayloadHandler<TestPayload>),
                                         PayloadType = typeof(TestPayload),
                                         BatchSize = 10
                                     },
                                     logger: loggerMock.Object);
        var method = GetPrivateMethod("HandleFailedQueueItemAsync",
        [
            typeof(IMongoCollection<MongoQueueItem<TestPayload>>), typeof(ObjectId), typeof(DateTime), typeof(Int64), typeof(Exception), typeof(CancellationToken)
        ]);

        // Act: attempt 2 already exceeds MaxRetries (1), so this must terminalize instead of retrying.
        await (Task)method.Invoke(sut, [
            collectionMock.Object, queueItemId, acquiredLockUtc, 2L, new InvalidOperationException("boom"), CancellationToken.None
        ])!;

        // Assert
        VerifyLoggedError(loggerMock, "Marking queue item", Times.Once());
        var measurement = metricsCollector.Measurements.Should()
                                          .ContainSingle(x => x.InstrumentName == MongoQueueMetrics.Instruments.ProcessingFailed)
                                          .Subject;
        measurement.Tags[MongoQueueMetrics.Tags.Result].Should().Be("terminal");
    }

    [Test]
    public async Task ProcessQueueItemAsync_WhenCancellationTokenIsCanceledBeforeCompletionWrite_StillCompletesTheItem()
    {
        // Arrange
        var queueItemId = ObjectId.GenerateNewId();
        var logger = new CapturingLogger();
        var collection = new RecordingQueueItemCollection(queueItemId, DateTime.UtcNow);
        var cancellationTokenSource = new CancellationTokenSource();
        var payloadHandlerFactory = new StubPayloadHandlerFactory(() => cancellationTokenSource.CancelAsync());

        var sut = CreateSubscription(payloadHandlerFactory: payloadHandlerFactory, logger: logger);
        var method = GetPrivateMethod("ProcessQueueItemAsync",
        [
            typeof(IMongoCollection<MongoQueueItem<TestPayload>>), typeof(ObjectId), typeof(CancellationToken)
        ]);

        // Act
        await (Task)method.Invoke(sut, [collection, queueItemId, cancellationTokenSource.Token])!;

        // Assert
        logger.Count(LogLevel.Information, "Processed queue item").Should().Be(1);
        logger.Count(LogLevel.Warning, "Skipping completion").Should().Be(0);
        logger.Count(LogLevel.Warning, "Completing queue item").Should().Be(0);
        collection.UpdateOneCallCount.Should().Be(1, "the retention completion write must run exactly once");
        collection.LastUpdateCancellationToken.IsCancellationRequested.Should().BeFalse(
            "the completion write must use the unlinked grace-period token, not the already-cancelled ambient token");
        ContainsEquality(collection.LastUpdateFilter!, nameof(MongoQueueItem.IsLocked), BsonBoolean.True).Should().BeTrue();
        ContainsEquality(collection.LastUpdateFilter!, nameof(MongoQueueItem.IsClosed), BsonBoolean.False).Should().BeTrue();
        ContainsEquality(collection.LastUpdateDefinition!, nameof(MongoQueueItem.IsClosed), BsonBoolean.True).Should().BeTrue(
            "the item must be marked closed so it is excluded from the next poll's availability filter");
        ContainsEquality(collection.LastUpdateDefinition!, nameof(MongoQueueItem.IsLocked), BsonBoolean.False).Should().BeTrue();
        collection.LastUpdateDefinition!["$unset"].AsBsonDocument.Contains(nameof(MongoQueueItem.LockedUtc)).Should().BeTrue();
        collection.LastUpdateDefinition!.Contains("$inc").Should().BeFalse("completion must not touch RetryCount");
    }

    [Test]
    public async Task ProcessQueueItemAsync_WhenCancellationTokenIsCanceledBeforeImmediateDeleteCompletionWrite_StillDeletesTheItem()
    {
        // Arrange
        var queueItemId = ObjectId.GenerateNewId();
        var logger = new CapturingLogger();
        var collection = new RecordingQueueItemCollection(queueItemId, DateTime.UtcNow);
        var cancellationTokenSource = new CancellationTokenSource();
        var payloadHandlerFactory = new StubPayloadHandlerFactory(() => cancellationTokenSource.CancelAsync());

        var sut = CreateSubscription(new MongoQueueDefinition
                                     {
                                         AutoStartSubscription = false,
                                         RetentionPeriod = null,
                                         CollectionName = "test-queue",
                                         LockLeaseTime = TimeSpan.FromMinutes(1),
                                         PayloadHandlerType = typeof(IMongoQueuePayloadHandler<TestPayload>),
                                         PayloadType = typeof(TestPayload),
                                         BatchSize = 10
                                     },
                                     payloadHandlerFactory: payloadHandlerFactory,
                                     logger: logger);
        var method = GetPrivateMethod("ProcessQueueItemAsync",
        [
            typeof(IMongoCollection<MongoQueueItem<TestPayload>>), typeof(ObjectId), typeof(CancellationToken)
        ]);

        // Act
        await (Task)method.Invoke(sut, [collection, queueItemId, cancellationTokenSource.Token])!;

        // Assert
        logger.Count(LogLevel.Information, "Processed queue item").Should().Be(1);
        logger.Count(LogLevel.Warning, "Skipping completion").Should().Be(0);
        collection.UpdateOneCallCount.Should().Be(0, "immediate-delete completion must use DeleteOneAsync, not UpdateOneAsync");
        collection.DeleteOneCallCount.Should().Be(1, "the immediate-delete completion write must run exactly once");
        collection.LastDeleteCancellationToken.IsCancellationRequested.Should().BeFalse(
            "the delete write must use the unlinked grace-period token, not the already-cancelled ambient token");
        ContainsEquality(collection.LastDeleteFilter!, nameof(MongoQueueItem.IsLocked), BsonBoolean.True).Should().BeTrue();
        ContainsEquality(collection.LastDeleteFilter!, nameof(MongoQueueItem.IsClosed), BsonBoolean.False).Should().BeTrue();
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
        payloadHandlerFactoryMock.Setup(x => x.CreateHandler<TestPayload>(It.IsAny<IServiceProvider>())).Returns(payloadHandlerMock.Object);
        collectionMock
            .Setup(c => c.FindOneAndUpdateAsync(
                       It.IsAny<FilterDefinition<MongoQueueItem<TestPayload>>>(),
                       It.IsAny<UpdateDefinition<MongoQueueItem<TestPayload>>>(),
                       It.IsAny<FindOneAndUpdateOptions<MongoQueueItem<TestPayload>, MongoQueueItem>>(),
                       It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MongoQueueItem
            {
                Id = queueItemId,
                CreatedUtc = DateTime.UtcNow.AddMinutes(-1),
                AttemptCount = 1
            });
        collectionMock
            .Setup(c => c.WithReadPreference(ReadPreference.Primary))
            .Returns(collectionMock.Object);
        collectionMock
            .Setup(c => c.FindAsync(
                       It.IsAny<FilterDefinition<MongoQueueItem<TestPayload>>>(),
                       It.IsAny<FindOptions<MongoQueueItem<TestPayload>, MongoQueueItem<TestPayload>>>(),
                       It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SingleItemAsyncCursor<MongoQueueItem<TestPayload>>(new MongoQueueItem<TestPayload>
            {
                Id = queueItemId,
                CreatedUtc = DateTime.UtcNow.AddMinutes(-1),
                Payload = new TestPayload()
            }));
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
    public async Task ProcessQueueItemAsync_WhenHandlerIsCanceledByShutdownAndLockOwnershipChanged_LogsWarningWithoutThrowing()
    {
        // Arrange
        var queueItemId = ObjectId.GenerateNewId();
        var logger = new CapturingLogger();
        var collection = new RecordingQueueItemCollection(queueItemId, DateTime.UtcNow)
        {
            ModifiedCount = 0
        };
        using var cancellationTokenSource = new CancellationTokenSource();
        var payloadHandlerFactory = new StubPayloadHandlerFactory(async () =>
        {
            // The ambient token is cancelled only after the handler was actually invoked with
            // an acquired lock, matching how the real driver cancels in-flight operations
            // rather than refusing to acquire the lock in the first place.
            // The handler runs within the awaited ProcessQueueItemAsync call, before the source is disposed.
            // ReSharper disable once AccessToDisposedClosure
            await cancellationTokenSource.CancelAsync();
            throw new OperationCanceledException();
        });

        var sut = CreateSubscription(payloadHandlerFactory: payloadHandlerFactory, logger: logger);
        var method = GetPrivateMethod("ProcessQueueItemAsync",
        [
            typeof(IMongoCollection<MongoQueueItem<TestPayload>>), typeof(ObjectId), typeof(CancellationToken)
        ]);

        // Act
        await (Task)method.Invoke(sut, [collection, queueItemId, cancellationTokenSource.Token])!;

        // Assert
        collection.UpdateOneCallCount.Should().Be(1);
        logger.Count(LogLevel.Warning, "Skipping lock release").Should().Be(1);
        logger.Count(LogLevel.Error, "failed on attempt").Should().Be(0);
    }

    [Test]
    public async Task ProcessQueueItemAsync_WhenHandlerIsCanceledByShutdown_ReleaseWriteDoesNotIncrementAttemptCountAgain()
    {
        // Arrange
        var queueItemId = ObjectId.GenerateNewId();
        var acquiredLockUtc = new DateTimeOffset(2025, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var logger = new CapturingLogger();
        var collection = new RecordingQueueItemCollection(queueItemId, acquiredLockUtc.UtcDateTime.AddMinutes(-1));
        var timeProvider = new FakeTimeProvider(acquiredLockUtc);
        using var cancellationTokenSource = new CancellationTokenSource();
        var payloadHandlerFactory = new StubPayloadHandlerFactory(async () =>
        {
            // The ambient token is cancelled only after the handler was actually invoked with
            // an acquired lock, matching how the real driver cancels in-flight operations
            // rather than refusing to acquire the lock in the first place.
            // The handler runs within the awaited ProcessQueueItemAsync call, before the source is disposed.
            // ReSharper disable once AccessToDisposedClosure
            await cancellationTokenSource.CancelAsync();
            throw new OperationCanceledException();
        });

        var sut = CreateSubscription(payloadHandlerFactory: payloadHandlerFactory,
                                     timeProvider: timeProvider,
                                     logger: logger);
        var method = GetPrivateMethod("ProcessQueueItemAsync",
        [
            typeof(IMongoCollection<MongoQueueItem<TestPayload>>), typeof(ObjectId), typeof(CancellationToken)
        ]);

        // Act
        await (Task)method.Invoke(sut, [collection, queueItemId, cancellationTokenSource.Token])!;

        // Assert
        collection.UpdateOneCallCount.Should().Be(1, "the lock-release write must run exactly once; the retry-handling path must not run");
        collection.LastUpdateCancellationToken.IsCancellationRequested.Should().BeFalse(
            "the release write must use the unlinked grace-period token, not the already-cancelled ambient token");
        ContainsEquality(collection.LastUpdateFilter!, "_id", new BsonObjectId(queueItemId)).Should().BeTrue(
            "the release must target the original lock owner");
        ContainsEquality(collection.LastUpdateFilter!, nameof(MongoQueueItem.IsLocked), BsonBoolean.True).Should().BeTrue();
        ContainsEquality(collection.LastUpdateFilter!, nameof(MongoQueueItem.LockedUtc), acquiredLockUtc.UtcDateTime).Should().BeTrue(
            "the release must only match the exact lock instance this attempt acquired, so a replacement lock acquired after a lease recovery is left untouched");
        ContainsEquality(collection.LastUpdateDefinition!, nameof(MongoQueueItem.IsLocked), BsonBoolean.False).Should().BeTrue(
            "the release must clear the lock so the item becomes available again immediately");
        collection.LastUpdateDefinition!["$unset"].AsBsonDocument.Contains(nameof(MongoQueueItem.LockedUtc)).Should().BeTrue();
        collection.LastUpdateDefinition!.Contains("$inc").Should().BeFalse(
            "the release write is a plain Set/Unset, not an increment; AttemptCount was already incremented once, atomically, by the earlier acquire");
        ContainsEquality(collection.LastUpdateDefinition!, nameof(MongoQueueItem.IsClosed), BsonBoolean.True).Should().BeFalse(
            "a shutdown-caused cancellation must not close the item, or it would never become available again");
        ContainsEquality(collection.LastUpdateDefinition!, nameof(MongoQueueItem.IsTerminal), BsonBoolean.True).Should().BeFalse(
            "a shutdown-caused cancellation must not terminalize the item");
        logger.Count(LogLevel.Error, "failed on attempt").Should().Be(0);
    }

    [Test]
    public async Task ProcessQueueItemAsync_WhenHandlerIsCanceledWithoutShutdown_TreatsItAsFailureAndDisposesItemScope()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddScoped<ScopedDependency>();
        var tracker = new DisposalTracker
        {
            ShouldCancel = true
        };
        services.AddSingleton(tracker);
        services.AddTransient<IMongoQueuePayloadHandler<TestPayload>, ScopedTestPayloadHandler>();
        var serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true
        });

        var queueItemId = ObjectId.GenerateNewId();
        var logger = new CapturingLogger();
        var collection = CreateSuccessfulCompletionCollection(queueItemId);
        var sut = CreateSubscription(payloadHandlerFactory: new MongoQueuePayloadHandlerFactory(),
                                     serviceScopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                                     logger: logger);
        var method = GetPrivateMethod("ProcessQueueItemAsync",
        [
            typeof(IMongoCollection<MongoQueueItem<TestPayload>>), typeof(ObjectId), typeof(CancellationToken)
        ]);

        // Act
        await (Task)method.Invoke(sut, [collection, queueItemId, CancellationToken.None])!;

        // Assert
        tracker.WasDisposed.Should().BeTrue("the per-item scope must dispose the handler even when its own cancellation is treated as a failure");
        logger.Count(LogLevel.Error, "failed on attempt").Should().Be(1);
    }

    [Test]
    public async Task ProcessQueueItemAsync_WhenHandlerThrowsTaskCanceledExceptionWithoutShutdown_IncrementsAttemptCountAndRespectsMaxRetries()
    {
        // Arrange
        var queueItemId = ObjectId.GenerateNewId();
        var logger = new CapturingLogger();
        var collection = new FailureTrackingQueueItemCollection(queueItemId, DateTime.UtcNow);
        var handlerException = new TaskCanceledException();
        var payloadHandlerFactory = new StubPayloadHandlerFactory(() => Task.FromException(handlerException));

        var sut = CreateSubscription(new MongoQueueDefinition
                                     {
                                         AutoStartSubscription = false,
                                         CollectionName = "test-queue",
                                         LockLeaseTime = TimeSpan.FromMinutes(1),
                                         MaxRetries = 1,
                                         PayloadHandlerType = typeof(IMongoQueuePayloadHandler<TestPayload>),
                                         PayloadType = typeof(TestPayload),
                                         BatchSize = 10
                                     },
                                     payloadHandlerFactory: payloadHandlerFactory,
                                     logger: logger);
        var method = GetPrivateMethod("ProcessQueueItemAsync",
        [
            typeof(IMongoCollection<MongoQueueItem<TestPayload>>), typeof(ObjectId), typeof(CancellationToken)
        ]);

        // Act - first attempt: ambient token is not cancelled, so this is a handler failure, not shutdown.
        await (Task)method.Invoke(sut, [collection, queueItemId, CancellationToken.None])!;

        // Assert - retried, not yet terminal.
        collection.AttemptCount.Should().Be(1, "the acquire must count this attempt the same way regardless of what the handler later does");
        collection.TerminalTransitionCount.Should().Be(0);
        logger.Count(LogLevel.Error, "failed on attempt 1", handlerException).Should().Be(1);

        // Act - second attempt exceeds MaxRetries (1).
        await (Task)method.Invoke(sut, [collection, queueItemId, CancellationToken.None])!;

        // Assert - MaxRetries respected: the item becomes terminal instead of retrying again.
        collection.AttemptCount.Should().Be(2);
        collection.TerminalTransitionCount.Should().Be(1);
        logger.Count(LogLevel.Error, "Marking queue item", handlerException).Should().Be(1);
    }

    [Test]
    public async Task ProcessQueueItemAsync_WhenHandlerThrows_StillDisposesItemScope()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddScoped<ScopedDependency>();
        var tracker = new DisposalTracker
        {
            ShouldThrow = true
        };
        services.AddSingleton(tracker);
        services.AddTransient<IMongoQueuePayloadHandler<TestPayload>, ScopedTestPayloadHandler>();
        var serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true
        });

        var queueItemId = ObjectId.GenerateNewId();
        var collection = CreateSuccessfulCompletionCollection(queueItemId);
        var sut = CreateSubscription(payloadHandlerFactory: new MongoQueuePayloadHandlerFactory(),
                                     serviceScopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>());
        var method = GetPrivateMethod("ProcessQueueItemAsync",
        [
            typeof(IMongoCollection<MongoQueueItem<TestPayload>>), typeof(ObjectId), typeof(CancellationToken)
        ]);

        // Act
        await (Task)method.Invoke(sut, [collection, queueItemId, CancellationToken.None])!;

        // Assert
        tracker.WasDisposed.Should().BeTrue("the per-item scope must dispose the handler even when it throws");
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
        payloadHandlerFactoryMock.Setup(x => x.CreateHandler<TestPayload>(It.IsAny<IServiceProvider>())).Returns(payloadHandlerMock.Object);
        collectionMock
            .Setup(c => c.FindOneAndUpdateAsync(
                       It.IsAny<FilterDefinition<MongoQueueItem<TestPayload>>>(),
                       It.IsAny<UpdateDefinition<MongoQueueItem<TestPayload>>>(),
                       It.IsAny<FindOneAndUpdateOptions<MongoQueueItem<TestPayload>, MongoQueueItem>>(),
                       It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MongoQueueItem
            {
                Id = queueItemId,
                CreatedUtc = DateTime.UtcNow.AddMinutes(-1),
                AttemptCount = 1
            });
        collectionMock
            .Setup(c => c.WithReadPreference(ReadPreference.Primary))
            .Returns(collectionMock.Object);
        collectionMock
            .Setup(c => c.FindAsync(
                       It.IsAny<FilterDefinition<MongoQueueItem<TestPayload>>>(),
                       It.IsAny<FindOptions<MongoQueueItem<TestPayload>, MongoQueueItem<TestPayload>>>(),
                       It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SingleItemAsyncCursor<MongoQueueItem<TestPayload>>(new MongoQueueItem<TestPayload>
            {
                Id = queueItemId,
                CreatedUtc = DateTime.UtcNow.AddMinutes(-1),
                Payload = new TestPayload()
            }));
        collectionMock
            .Setup(c => c.DeleteOneAsync(
                       It.IsAny<FilterDefinition<MongoQueueItem<TestPayload>>>(),
                       It.IsAny<CancellationToken>()))
            .ReturnsAsync(deleteResultMock.Object);

        var sut = CreateSubscription(new MongoQueueDefinition
                                     {
                                         AutoStartSubscription = false,
                                         RetentionPeriod = null,
                                         CollectionName = "test-queue",
                                         LockLeaseTime = TimeSpan.FromMinutes(1),
                                         PayloadHandlerType = typeof(IMongoQueuePayloadHandler<TestPayload>),
                                         PayloadType = typeof(TestPayload),
                                         BatchSize = 10
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
    public async Task ProcessQueueItemAsync_WhenLockAcquired_ReadsPayloadFromPrimary()
    {
        // Arrange: the acquire runs on the primary, but the collection inherits the client's read preference.
        // The follow-up payload read must not go to a secondary that may not have replicated the item yet,
        // or replication lag would be counted as a failed attempt.
        var queueItemId = ObjectId.GenerateNewId();
        var collection = new RecordingQueueItemCollection(queueItemId, DateTime.UtcNow.AddMinutes(-1));
        var handlerInvoked = false;
        var payloadHandlerFactory = new StubPayloadHandlerFactory(() =>
        {
            handlerInvoked = true;
            return Task.CompletedTask;
        });

        var sut = CreateSubscription(payloadHandlerFactory: payloadHandlerFactory);
        var method = GetPrivateMethod("ProcessQueueItemAsync",
        [
            typeof(IMongoCollection<MongoQueueItem<TestPayload>>), typeof(ObjectId), typeof(CancellationToken)
        ]);

        // Act
        await (Task)method.Invoke(sut, [collection, queueItemId, CancellationToken.None])!;

        // Assert
        collection.FindReadPreference.Should().Be(ReadPreference.Primary);
        handlerInvoked.Should().BeTrue();
    }

    [Test]
    public async Task ProcessQueueItemAsync_WhenPayloadHandlerFactoryPersistentlyThrows_IncrementsAttemptCountAndRespectsMaxRetries()
    {
        // Arrange: a DI resolution failure happens outside the handler's own try/catch (closing the gap where a
        // misconfigured handler dependency used to skip counting entirely), so it must be funneled through the
        // same HandleFailedQueueItemAsync path and respect the same MaxRetries budget as a handler exception.
        var queueItemId = ObjectId.GenerateNewId();
        var logger = new CapturingLogger();
        var collection = new FailureTrackingQueueItemCollection(queueItemId, DateTime.UtcNow);
        var payloadHandlerFactory = new ThrowingPayloadHandlerFactory();

        var sut = CreateSubscription(new MongoQueueDefinition
                                     {
                                         AutoStartSubscription = false,
                                         CollectionName = "test-queue",
                                         LockLeaseTime = TimeSpan.FromMinutes(1),
                                         MaxRetries = 1,
                                         PayloadHandlerType = typeof(IMongoQueuePayloadHandler<TestPayload>),
                                         PayloadType = typeof(TestPayload),
                                         BatchSize = 10
                                     },
                                     payloadHandlerFactory: payloadHandlerFactory,
                                     logger: logger);
        var method = GetPrivateMethod("ProcessQueueItemAsync",
        [
            typeof(IMongoCollection<MongoQueueItem<TestPayload>>), typeof(ObjectId), typeof(CancellationToken)
        ]);

        // Act - attempt 1 and attempt 2 (MaxRetries + 1 total), both failing DI resolution.
        await (Task)method.Invoke(sut, [collection, queueItemId, CancellationToken.None])!;
        await (Task)method.Invoke(sut, [collection, queueItemId, CancellationToken.None])!;

        // Assert - exactly MaxRetries + 1 attempts were counted, and the item reached terminal on the attempt
        // that exceeded the budget, without ever invoking a handler.
        collection.AttemptCount.Should().Be(2);
        collection.TerminalTransitionCount.Should().Be(1);
        logger.Count(LogLevel.Error, "failed on attempt 1").Should().Be(1);
        logger.Count(LogLevel.Error, "Marking queue item").Should().Be(1);
    }

    [Test]
    public async Task ProcessQueueItemAsync_WithPayloadHandlerFactoryRegistration_ResolvesScopedDependencyAndDisposesHandler()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddScoped<ScopedDependency>();
        var tracker = new DisposalTracker();
        services.AddSingleton(tracker);
        services.AddTransient<IMongoQueuePayloadHandler<TestPayload>>(sp => new ScopedTestPayloadHandler(sp.GetRequiredService<ScopedDependency>(),
                                                                                                         sp.GetRequiredService<DisposalTracker>()));
        var serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true
        });

        var queueItemId = ObjectId.GenerateNewId();
        var collection = CreateSuccessfulCompletionCollection(queueItemId);
        var sut = CreateSubscription(payloadHandlerFactory: new MongoQueuePayloadHandlerFactory(),
                                     serviceScopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>());
        var method = GetPrivateMethod("ProcessQueueItemAsync",
        [
            typeof(IMongoCollection<MongoQueueItem<TestPayload>>), typeof(ObjectId), typeof(CancellationToken)
        ]);

        // Act
        await (Task)method.Invoke(sut, [collection, queueItemId, CancellationToken.None])!;

        // Assert
        tracker.ResolvedScopedDependency.Should().NotBeNull("a factory-registered handler must receive the item's scoped IServiceProvider");
        tracker.WasDisposed.Should().BeTrue("the per-item scope must dispose the handler once handling completes");
    }

    [Test]
    public async Task ProcessQueueItemAsync_WithPayloadHandlerTypeRegistration_ResolvesScopedDependencyAndDisposesHandler()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddScoped<ScopedDependency>();
        var tracker = new DisposalTracker();
        services.AddSingleton(tracker);
        services.AddTransient<IMongoQueuePayloadHandler<TestPayload>, ScopedTestPayloadHandler>();
        var serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true
        });

        var queueItemId = ObjectId.GenerateNewId();
        var collection = CreateSuccessfulCompletionCollection(queueItemId);
        var sut = CreateSubscription(payloadHandlerFactory: new MongoQueuePayloadHandlerFactory(),
                                     serviceScopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>());
        var method = GetPrivateMethod("ProcessQueueItemAsync",
        [
            typeof(IMongoCollection<MongoQueueItem<TestPayload>>), typeof(ObjectId), typeof(CancellationToken)
        ]);

        // Act
        await (Task)method.Invoke(sut, [collection, queueItemId, CancellationToken.None])!;

        // Assert
        tracker.ResolvedScopedDependency.Should().NotBeNull("a handler-type registration must resolve scoped services from the item's scope");
        tracker.WasDisposed.Should().BeTrue("the per-item scope must dispose the handler once handling completes");
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
        payloadHandlerFactoryMock.Setup(x => x.CreateHandler<TestPayload>(It.IsAny<IServiceProvider>())).Returns(payloadHandlerMock.Object);
        collectionMock
            .Setup(c => c.FindOneAndUpdateAsync(
                       It.IsAny<FilterDefinition<MongoQueueItem<TestPayload>>>(),
                       It.IsAny<UpdateDefinition<MongoQueueItem<TestPayload>>>(),
                       It.IsAny<FindOneAndUpdateOptions<MongoQueueItem<TestPayload>, MongoQueueItem>>(),
                       It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MongoQueueItem
            {
                Id = queueItemId,
                CreatedUtc = createdUtc,
                IsLocked = true,
                LockedUtc = recoveredLockUtc,
                AttemptCount = 1
            });
        collectionMock
            .Setup(c => c.WithReadPreference(ReadPreference.Primary))
            .Returns(collectionMock.Object);
        collectionMock
            .Setup(c => c.FindAsync(
                       It.IsAny<FilterDefinition<MongoQueueItem<TestPayload>>>(),
                       It.IsAny<FindOptions<MongoQueueItem<TestPayload>, MongoQueueItem<TestPayload>>>(),
                       It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SingleItemAsyncCursor<MongoQueueItem<TestPayload>>(new MongoQueueItem<TestPayload>
            {
                Id = queueItemId,
                CreatedUtc = createdUtc,
                Payload = new TestPayload()
            }));
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
        var lockRecoveryAgeMeasurement = metricsCollector.Measurements.Should()
                                                         .ContainSingle(x => x.InstrumentName == MongoQueueMetrics.Instruments.LockRecoveryAge)
                                                         .Subject;
        lockRecoveryAgeMeasurement.Unit.Should().Be("s");
        lockRecoveryAgeMeasurement.Value.Should().Be(120.0);
        var successMeasurement = metricsCollector.Measurements.Should()
                                                 .ContainSingle(x => x.InstrumentName == MongoQueueMetrics.Instruments.ProcessingSucceeded)
                                                 .Subject;
        successMeasurement.Tags[MongoQueueMetrics.Tags.CleanupMode].Should().Be("ttl-retention");
        successMeasurement.Tags[MongoQueueMetrics.Tags.QueueCollection].Should().Be("test-queue");
        var processingDurationMeasurement = metricsCollector.Measurements.Should()
                                                            .ContainSingle(x => x.InstrumentName == MongoQueueMetrics.Instruments.ProcessingDuration)
                                                            .Subject;
        processingDurationMeasurement.Unit.Should().Be("s");
        var queueAgeMeasurement = metricsCollector.Measurements.Should()
                                                  .ContainSingle(x => x.InstrumentName == MongoQueueMetrics.Instruments.ProcessingQueueAge)
                                                  .Subject;
        queueAgeMeasurement.Unit.Should().Be("s");
        queueAgeMeasurement.Value.Should().Be(300.0);
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

    private static IServiceScopeFactory CreateDefaultServiceScopeFactory()
        => new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

    private static MongoQueueSubscription<TestPayload> CreateSubscription(
        MongoQueueDefinition? queueDefinition = null,
        IMongoHelper? mongoHelper = null,
        IMongoQueuePayloadHandlerFactory? payloadHandlerFactory = null,
        IMongoQueuePayloadPrioritizer? payloadPrioritizer = null,
        IServiceScopeFactory? serviceScopeFactory = null,
        TimeProvider? timeProvider = null,
        ILogger<MongoQueueSubscription<TestPayload>>? logger = null)
        => new(queueDefinition ?? new MongoQueueDefinition
               {
                   AutoStartSubscription = false,
                   CollectionName = "test-queue",
                   LockLeaseTime = TimeSpan.FromMinutes(1),
                   PayloadHandlerType = typeof(IMongoQueuePayloadHandler<TestPayload>),
                   PayloadType = typeof(TestPayload),
                   BatchSize = 10
               },
               mongoHelper ?? Mock.Of<IMongoHelper>(),
               payloadHandlerFactory ?? Mock.Of<IMongoQueuePayloadHandlerFactory>(),
               payloadPrioritizer ?? Mock.Of<IMongoQueuePayloadPrioritizer>(),
               serviceScopeFactory ?? CreateDefaultServiceScopeFactory(),
               timeProvider ?? TimeProvider.System,
               logger ?? Mock.Of<ILogger<MongoQueueSubscription<TestPayload>>>());

    private static IMongoCollection<MongoQueueItem<TestPayload>> CreateSuccessfulCompletionCollection(ObjectId queueItemId)
        => new CompletingQueueItemCollection(queueItemId);

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
                It.Is<It.IsAnyType>((state, _) => state!.ToString()!.Contains(messageSubstring)),
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
                It.Is<It.IsAnyType>((state, _) => state!.ToString()!.Contains(messageSubstring)),
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
                It.Is<It.IsAnyType>((state, _) => state!.ToString()!.Contains(messageSubstring)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, String>>()),
            times);

    public class TestPayload;

    /// <summary>
    /// Logger test double that records the level, message and exception of every log entry.
    /// </summary>
    private sealed class CapturingLogger : ILogger<MongoQueueSubscription<TestPayload>>
    {
        private readonly List<(LogLevel Level, String Message, Exception? Exception)> _entries = [];

        public Int32 Count(LogLevel level, String messageSubstring, Exception? exception = null)
            => _entries.Count(entry => entry.Level == level &&
                                       entry.Message.Contains(messageSubstring) &&
                                       (exception is null || ReferenceEquals(entry.Exception, exception)));

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public Boolean IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel,
                                EventId eventId,
                                TState state,
                                Exception? exception,
                                Func<TState, Exception?, String> formatter)
            => _entries.Add((logLevel, formatter(state, exception), exception));
    }

    /// <summary>
    /// Hand-crafted collection double simulating a successful acquire-then-complete sequence for a single queue
    /// item:
    /// <see
    ///     cref="FindOneAndUpdateAsync{TProjection}(FilterDefinition{MongoQueueItem{TestPayload}}, UpdateDefinition{MongoQueueItem{TestPayload}}, FindOneAndUpdateOptions{MongoQueueItem{TestPayload}, TProjection}, CancellationToken)"/>
    /// returns the queue item with the given ID, and <see cref="UpdateOneAsync"/> reports it as modified. Every
    /// other member is inherited from <see cref="QueueItemCollectionTestDoubleBase"/> and throws deliberately.
    /// </summary>
    private sealed class CompletingQueueItemCollection : QueueItemCollectionTestDoubleBase
    {
        private readonly ObjectId _queueItemId;

        public CompletingQueueItemCollection(ObjectId queueItemId) => _queueItemId = queueItemId;

        public override Task<IAsyncCursor<TProjection>> FindAsync<TProjection>(FilterDefinition<MongoQueueItem<TestPayload>> filter,
                                                                               FindOptions<MongoQueueItem<TestPayload>, TProjection> options,
                                                                               CancellationToken cancellationToken)
        {
            var queueItem = new MongoQueueItem<TestPayload>
            {
                Id = _queueItemId,
                CreatedUtc = DateTime.UtcNow,
                Payload = new TestPayload()
            };
            return Task.FromResult<IAsyncCursor<TProjection>>(new SingleItemAsyncCursor<TProjection>((TProjection)(Object)queueItem));
        }

        public override Task<TProjection> FindOneAndUpdateAsync<TProjection>(FilterDefinition<MongoQueueItem<TestPayload>> filter,
                                                                             UpdateDefinition<MongoQueueItem<TestPayload>> update,
                                                                             FindOneAndUpdateOptions<MongoQueueItem<TestPayload>, TProjection> options,
                                                                             CancellationToken cancellationToken)
        {
            var queueItem = new MongoQueueItem<TestPayload>
            {
                Id = _queueItemId,
                CreatedUtc = DateTime.UtcNow,
                Payload = new TestPayload()
            };
            return Task.FromResult((TProjection)(Object)queueItem);
        }

        public override Task<UpdateResult> UpdateOneAsync(FilterDefinition<MongoQueueItem<TestPayload>> filter, UpdateDefinition<MongoQueueItem<TestPayload>> update,
                                                          UpdateOptions options,
                                                          CancellationToken cancellationToken)
            => Task.FromResult<UpdateResult>(new UpdateResult.Acknowledged(1, 1, null));
    }

    /// <summary>
    /// Records whether a <see cref="ScopedTestPayloadHandler"/> resolved its scoped dependency and whether it was
    /// disposed, and configures whether <see cref="ScopedTestPayloadHandler.HandlePayloadAsync"/> throws or cancels.
    /// </summary>
    private sealed class DisposalTracker
    {
        public ScopedDependency? ResolvedScopedDependency { get; set; }
        public Boolean ShouldCancel { get; set; }
        public Boolean ShouldThrow { get; set; }
        public Boolean WasDisposed { get; set; }
    }

    /// <summary>
    /// Hand-crafted collection double that tracks a queue item's attempt count and terminal transitions across
    /// repeated <c>ProcessQueueItemAsync</c> invocations, so tests can assert that a resolved failure (a handler
    /// exception or a DI resolution failure) increments <c>AttemptCount</c> at acquire time and that exceeding
    /// <c>MaxRetries</c> marks the item terminal. Every acquire call unconditionally grants a new attempt — none
    /// of these tests simulate the acquire pipeline's own lease-expiry-reclaim terminal branch, which is covered
    /// separately against a real MongoDB server.
    /// </summary>
    private sealed class FailureTrackingQueueItemCollection : QueueItemCollectionTestDoubleBase
    {
        private readonly DateTime _createdUtc;
        private readonly ObjectId _queueItemId;

        public FailureTrackingQueueItemCollection(ObjectId queueItemId, DateTime createdUtc)
        {
            _queueItemId = queueItemId;
            _createdUtc = createdUtc;
        }

        public Int64 AttemptCount { get; private set; }
        public Int32 TerminalTransitionCount { get; private set; }

        public override Task<IAsyncCursor<TProjection>> FindAsync<TProjection>(FilterDefinition<MongoQueueItem<TestPayload>> filter,
                                                                               FindOptions<MongoQueueItem<TestPayload>, TProjection> options,
                                                                               CancellationToken cancellationToken)
        {
            var queueItem = new MongoQueueItem<TestPayload>
            {
                Id = _queueItemId,
                CreatedUtc = _createdUtc,
                Payload = new TestPayload()
            };
            return Task.FromResult<IAsyncCursor<TProjection>>(new SingleItemAsyncCursor<TProjection>((TProjection)(Object)queueItem));
        }

        public override Task<TProjection> FindOneAndUpdateAsync<TProjection>(FilterDefinition<MongoQueueItem<TestPayload>> filter,
                                                                             UpdateDefinition<MongoQueueItem<TestPayload>> update,
                                                                             FindOneAndUpdateOptions<MongoQueueItem<TestPayload>, TProjection> options,
                                                                             CancellationToken cancellationToken)
        {
            var priorAttemptCount = AttemptCount;
            AttemptCount++;

            var projected = new MongoQueueItem
            {
                Id = _queueItemId,
                CreatedUtc = _createdUtc,
                AttemptCount = priorAttemptCount
            };
            return Task.FromResult((TProjection)(Object)projected);
        }

        public override Task<UpdateResult> UpdateOneAsync(FilterDefinition<MongoQueueItem<TestPayload>> filter, UpdateDefinition<MongoQueueItem<TestPayload>> update,
                                                          UpdateOptions options,
                                                          CancellationToken cancellationToken)
        {
            TerminalTransitionCount++;
            return Task.FromResult<UpdateResult>(new UpdateResult.Acknowledged(1, 1, null));
        }
    }

    /// <summary>
    /// Base type for hand-crafted <see cref="IMongoCollection{TDocument}"/> test doubles used by
    /// <c>ProcessQueueItemAsync</c>/<c>HandleFailedQueueItemAsync</c> tests. Every member throws
    /// <see cref="NotSupportedException"/> by default; derived doubles override only the members their
    /// scenario actually reaches. The one exception is <see cref="WithReadPreference"/>, which every acquired
    /// item reaches through its payload read: it records the requested preference and returns the same double.
    /// </summary>
    private abstract class QueueItemCollectionTestDoubleBase : IMongoCollection<MongoQueueItem<TestPayload>>
    {
        public ReadPreference? RequestedReadPreference { get; private set; }

        public virtual CollectionNamespace CollectionNamespace => throw new NotSupportedException();
        public virtual IMongoDatabase Database => throw new NotSupportedException();
        public virtual IBsonSerializer<MongoQueueItem<TestPayload>> DocumentSerializer => throw new NotSupportedException();
        public virtual IMongoIndexManager<MongoQueueItem<TestPayload>> Indexes => throw new NotSupportedException();
        public virtual IMongoSearchIndexManager SearchIndexes => throw new NotSupportedException();
        public virtual MongoCollectionSettings Settings => throw new NotSupportedException();

        public virtual IAsyncCursor<TResult> Aggregate<TResult>(PipelineDefinition<MongoQueueItem<TestPayload>, TResult> pipeline, AggregateOptions options,
                                                                CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual IAsyncCursor<TResult> Aggregate<TResult>(IClientSessionHandle session, PipelineDefinition<MongoQueueItem<TestPayload>, TResult> pipeline,
                                                                AggregateOptions options,
                                                                CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual Task<IAsyncCursor<TResult>> AggregateAsync<TResult>(PipelineDefinition<MongoQueueItem<TestPayload>, TResult> pipeline, AggregateOptions options,
                                                                           CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual Task<IAsyncCursor<TResult>> AggregateAsync<TResult>(IClientSessionHandle session, PipelineDefinition<MongoQueueItem<TestPayload>, TResult> pipeline,
                                                                           AggregateOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual void AggregateToCollection<TResult>(PipelineDefinition<MongoQueueItem<TestPayload>, TResult> pipeline, AggregateOptions options,
                                                           CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual void AggregateToCollection<TResult>(IClientSessionHandle session, PipelineDefinition<MongoQueueItem<TestPayload>, TResult> pipeline,
                                                           AggregateOptions options,
                                                           CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual Task AggregateToCollectionAsync<TResult>(PipelineDefinition<MongoQueueItem<TestPayload>, TResult> pipeline, AggregateOptions options,
                                                                CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual Task AggregateToCollectionAsync<TResult>(IClientSessionHandle session, PipelineDefinition<MongoQueueItem<TestPayload>, TResult> pipeline,
                                                                AggregateOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual BulkWriteResult<MongoQueueItem<TestPayload>> BulkWrite(IEnumerable<WriteModel<MongoQueueItem<TestPayload>>> requests, BulkWriteOptions options,
                                                                              CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual BulkWriteResult<MongoQueueItem<TestPayload>> BulkWrite(IClientSessionHandle session, IEnumerable<WriteModel<MongoQueueItem<TestPayload>>> requests,
                                                                              BulkWriteOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual Task<BulkWriteResult<MongoQueueItem<TestPayload>>> BulkWriteAsync(IEnumerable<WriteModel<MongoQueueItem<TestPayload>>> requests, BulkWriteOptions options,
                                                                                         CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual Task<BulkWriteResult<MongoQueueItem<TestPayload>>> BulkWriteAsync(IClientSessionHandle session,
                                                                                         IEnumerable<WriteModel<MongoQueueItem<TestPayload>>> requests,
                                                                                         BulkWriteOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public virtual Int64 Count(FilterDefinition<MongoQueueItem<TestPayload>> filter, CountOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public virtual Int64 Count(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter, CountOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public virtual Task<Int64> CountAsync(FilterDefinition<MongoQueueItem<TestPayload>> filter, CountOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public virtual Task<Int64> CountAsync(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter, CountOptions options,
                                              CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual Int64 CountDocuments(FilterDefinition<MongoQueueItem<TestPayload>> filter, CountOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public virtual Int64 CountDocuments(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter, CountOptions options,
                                            CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual Task<Int64> CountDocumentsAsync(FilterDefinition<MongoQueueItem<TestPayload>> filter, CountOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public virtual Task<Int64> CountDocumentsAsync(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter, CountOptions options,
                                                       CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual DeleteResult DeleteMany(FilterDefinition<MongoQueueItem<TestPayload>> filter, CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual DeleteResult DeleteMany(FilterDefinition<MongoQueueItem<TestPayload>> filter, DeleteOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public virtual DeleteResult DeleteMany(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter, DeleteOptions options,
                                               CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual Task<DeleteResult> DeleteManyAsync(FilterDefinition<MongoQueueItem<TestPayload>> filter, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public virtual Task<DeleteResult> DeleteManyAsync(FilterDefinition<MongoQueueItem<TestPayload>> filter, DeleteOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public virtual Task<DeleteResult> DeleteManyAsync(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter, DeleteOptions options,
                                                          CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual DeleteResult DeleteOne(FilterDefinition<MongoQueueItem<TestPayload>> filter, CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual DeleteResult DeleteOne(FilterDefinition<MongoQueueItem<TestPayload>> filter, DeleteOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public virtual DeleteResult DeleteOne(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter, DeleteOptions options,
                                              CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual Task<DeleteResult> DeleteOneAsync(FilterDefinition<MongoQueueItem<TestPayload>> filter, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public virtual Task<DeleteResult> DeleteOneAsync(FilterDefinition<MongoQueueItem<TestPayload>> filter, DeleteOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public virtual Task<DeleteResult> DeleteOneAsync(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter, DeleteOptions options,
                                                         CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual IAsyncCursor<TField> Distinct<TField>(FieldDefinition<MongoQueueItem<TestPayload>, TField> field, FilterDefinition<MongoQueueItem<TestPayload>> filter,
                                                             DistinctOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual IAsyncCursor<TField> Distinct<TField>(IClientSessionHandle session, FieldDefinition<MongoQueueItem<TestPayload>, TField> field,
                                                             FilterDefinition<MongoQueueItem<TestPayload>> filter, DistinctOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public virtual Task<IAsyncCursor<TField>> DistinctAsync<TField>(FieldDefinition<MongoQueueItem<TestPayload>, TField> field,
                                                                        FilterDefinition<MongoQueueItem<TestPayload>> filter,
                                                                        DistinctOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual Task<IAsyncCursor<TField>> DistinctAsync<TField>(IClientSessionHandle session, FieldDefinition<MongoQueueItem<TestPayload>, TField> field,
                                                                        FilterDefinition<MongoQueueItem<TestPayload>> filter, DistinctOptions options,
                                                                        CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual IAsyncCursor<TItem> DistinctMany<TItem>(FieldDefinition<MongoQueueItem<TestPayload>, IEnumerable<TItem>> field,
                                                               FilterDefinition<MongoQueueItem<TestPayload>> filter, DistinctOptions options,
                                                               CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public virtual IAsyncCursor<TItem> DistinctMany<TItem>(IClientSessionHandle session, FieldDefinition<MongoQueueItem<TestPayload>, IEnumerable<TItem>> field,
                                                               FilterDefinition<MongoQueueItem<TestPayload>> filter, DistinctOptions options,
                                                               CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public virtual Task<IAsyncCursor<TItem>> DistinctManyAsync<TItem>(FieldDefinition<MongoQueueItem<TestPayload>, IEnumerable<TItem>> field,
                                                                          FilterDefinition<MongoQueueItem<TestPayload>> filter, DistinctOptions options,
                                                                          CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual Task<IAsyncCursor<TItem>> DistinctManyAsync<TItem>(IClientSessionHandle session, FieldDefinition<MongoQueueItem<TestPayload>, IEnumerable<TItem>> field,
                                                                          FilterDefinition<MongoQueueItem<TestPayload>> filter, DistinctOptions options,
                                                                          CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual Int64 EstimatedDocumentCount(EstimatedDocumentCountOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual Task<Int64> EstimatedDocumentCountAsync(EstimatedDocumentCountOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual Task<IAsyncCursor<TProjection>> FindAsync<TProjection>(FilterDefinition<MongoQueueItem<TestPayload>> filter,
                                                                              FindOptions<MongoQueueItem<TestPayload>, TProjection> options,
                                                                              CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual Task<IAsyncCursor<TProjection>> FindAsync<TProjection>(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter,
                                                                              FindOptions<MongoQueueItem<TestPayload>, TProjection> options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public virtual TProjection FindOneAndDelete<TProjection>(FilterDefinition<MongoQueueItem<TestPayload>> filter,
                                                                 FindOneAndDeleteOptions<MongoQueueItem<TestPayload>, TProjection> options,
                                                                 CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual TProjection FindOneAndDelete<TProjection>(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter,
                                                                 FindOneAndDeleteOptions<MongoQueueItem<TestPayload>, TProjection> options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public virtual Task<TProjection> FindOneAndDeleteAsync<TProjection>(FilterDefinition<MongoQueueItem<TestPayload>> filter,
                                                                            FindOneAndDeleteOptions<MongoQueueItem<TestPayload>, TProjection> options,
                                                                            CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual Task<TProjection> FindOneAndDeleteAsync<TProjection>(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter,
                                                                            FindOneAndDeleteOptions<MongoQueueItem<TestPayload>, TProjection> options,
                                                                            CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual TProjection FindOneAndReplace<TProjection>(FilterDefinition<MongoQueueItem<TestPayload>> filter, MongoQueueItem<TestPayload> replacement,
                                                                  FindOneAndReplaceOptions<MongoQueueItem<TestPayload>, TProjection> options,
                                                                  CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public virtual TProjection FindOneAndReplace<TProjection>(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter,
                                                                  MongoQueueItem<TestPayload> replacement,
                                                                  FindOneAndReplaceOptions<MongoQueueItem<TestPayload>, TProjection> options,
                                                                  CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual Task<TProjection> FindOneAndReplaceAsync<TProjection>(FilterDefinition<MongoQueueItem<TestPayload>> filter, MongoQueueItem<TestPayload> replacement,
                                                                             FindOneAndReplaceOptions<MongoQueueItem<TestPayload>, TProjection> options,
                                                                             CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual Task<TProjection> FindOneAndReplaceAsync<TProjection>(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter,
                                                                             MongoQueueItem<TestPayload> replacement,
                                                                             FindOneAndReplaceOptions<MongoQueueItem<TestPayload>, TProjection> options,
                                                                             CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual TProjection FindOneAndUpdate<TProjection>(FilterDefinition<MongoQueueItem<TestPayload>> filter, UpdateDefinition<MongoQueueItem<TestPayload>> update,
                                                                 FindOneAndUpdateOptions<MongoQueueItem<TestPayload>, TProjection> options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public virtual TProjection FindOneAndUpdate<TProjection>(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter,
                                                                 UpdateDefinition<MongoQueueItem<TestPayload>> update,
                                                                 FindOneAndUpdateOptions<MongoQueueItem<TestPayload>, TProjection> options,
                                                                 CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual Task<TProjection> FindOneAndUpdateAsync<TProjection>(FilterDefinition<MongoQueueItem<TestPayload>> filter,
                                                                            UpdateDefinition<MongoQueueItem<TestPayload>> update,
                                                                            FindOneAndUpdateOptions<MongoQueueItem<TestPayload>, TProjection> options,
                                                                            CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual Task<TProjection> FindOneAndUpdateAsync<TProjection>(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter,
                                                                            UpdateDefinition<MongoQueueItem<TestPayload>> update,
                                                                            FindOneAndUpdateOptions<MongoQueueItem<TestPayload>, TProjection> options,
                                                                            CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual IAsyncCursor<TProjection> FindSync<TProjection>(FilterDefinition<MongoQueueItem<TestPayload>> filter,
                                                                       FindOptions<MongoQueueItem<TestPayload>, TProjection> options,
                                                                       CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual IAsyncCursor<TProjection> FindSync<TProjection>(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter,
                                                                       FindOptions<MongoQueueItem<TestPayload>, TProjection> options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public virtual void InsertMany(IEnumerable<MongoQueueItem<TestPayload>> documents, InsertManyOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public virtual void InsertMany(IClientSessionHandle session, IEnumerable<MongoQueueItem<TestPayload>> documents, InsertManyOptions options,
                                       CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual Task InsertManyAsync(IEnumerable<MongoQueueItem<TestPayload>> documents, InsertManyOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public virtual Task InsertManyAsync(IClientSessionHandle session, IEnumerable<MongoQueueItem<TestPayload>> documents, InsertManyOptions options,
                                            CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual void InsertOne(MongoQueueItem<TestPayload> document, InsertOneOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual void InsertOne(IClientSessionHandle session, MongoQueueItem<TestPayload> document, InsertOneOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public virtual Task InsertOneAsync(MongoQueueItem<TestPayload> document, CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual Task InsertOneAsync(MongoQueueItem<TestPayload> document, InsertOneOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public virtual Task InsertOneAsync(IClientSessionHandle session, MongoQueueItem<TestPayload> document, InsertOneOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public virtual IAsyncCursor<TResult> MapReduce<TResult>(BsonJavaScript map, BsonJavaScript reduce, MapReduceOptions<MongoQueueItem<TestPayload>, TResult> options,
                                                                CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual IAsyncCursor<TResult> MapReduce<TResult>(IClientSessionHandle session, BsonJavaScript map, BsonJavaScript reduce,
                                                                MapReduceOptions<MongoQueueItem<TestPayload>, TResult> options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public virtual Task<IAsyncCursor<TResult>> MapReduceAsync<TResult>(BsonJavaScript map, BsonJavaScript reduce,
                                                                           MapReduceOptions<MongoQueueItem<TestPayload>, TResult> options,
                                                                           CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual Task<IAsyncCursor<TResult>> MapReduceAsync<TResult>(IClientSessionHandle session, BsonJavaScript map, BsonJavaScript reduce,
                                                                           MapReduceOptions<MongoQueueItem<TestPayload>, TResult> options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public virtual ReplaceOneResult ReplaceOne(FilterDefinition<MongoQueueItem<TestPayload>> filter, MongoQueueItem<TestPayload> replacement, ReplaceOptions options,
                                                   CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual ReplaceOneResult ReplaceOne(FilterDefinition<MongoQueueItem<TestPayload>> filter, MongoQueueItem<TestPayload> replacement, UpdateOptions options,
                                                   CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual ReplaceOneResult ReplaceOne(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter, MongoQueueItem<TestPayload> replacement,
                                                   ReplaceOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual ReplaceOneResult ReplaceOne(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter, MongoQueueItem<TestPayload> replacement,
                                                   UpdateOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual Task<ReplaceOneResult> ReplaceOneAsync(FilterDefinition<MongoQueueItem<TestPayload>> filter, MongoQueueItem<TestPayload> replacement, ReplaceOptions options,
                                                              CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual Task<ReplaceOneResult> ReplaceOneAsync(FilterDefinition<MongoQueueItem<TestPayload>> filter, MongoQueueItem<TestPayload> replacement, UpdateOptions options,
                                                              CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual Task<ReplaceOneResult> ReplaceOneAsync(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter,
                                                              MongoQueueItem<TestPayload> replacement,
                                                              ReplaceOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual Task<ReplaceOneResult> ReplaceOneAsync(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter,
                                                              MongoQueueItem<TestPayload> replacement,
                                                              UpdateOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual UpdateResult UpdateMany(FilterDefinition<MongoQueueItem<TestPayload>> filter, UpdateDefinition<MongoQueueItem<TestPayload>> update, UpdateOptions options,
                                               CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual UpdateResult UpdateMany(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter,
                                               UpdateDefinition<MongoQueueItem<TestPayload>> update,
                                               UpdateOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual Task<UpdateResult> UpdateManyAsync(FilterDefinition<MongoQueueItem<TestPayload>> filter, UpdateDefinition<MongoQueueItem<TestPayload>> update,
                                                          UpdateOptions options,
                                                          CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual Task<UpdateResult> UpdateManyAsync(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter,
                                                          UpdateDefinition<MongoQueueItem<TestPayload>> update, UpdateOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public virtual UpdateResult UpdateOne(FilterDefinition<MongoQueueItem<TestPayload>> filter, UpdateDefinition<MongoQueueItem<TestPayload>> update, UpdateOptions options,
                                              CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual UpdateResult UpdateOne(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter,
                                              UpdateDefinition<MongoQueueItem<TestPayload>> update,
                                              UpdateOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual Task<UpdateResult> UpdateOneAsync(FilterDefinition<MongoQueueItem<TestPayload>> filter, UpdateDefinition<MongoQueueItem<TestPayload>> update,
                                                         UpdateOptions options,
                                                         CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual Task<UpdateResult> UpdateOneAsync(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter,
                                                         UpdateDefinition<MongoQueueItem<TestPayload>> update, UpdateOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public virtual IChangeStreamCursor<TResult> Watch<TResult>(PipelineDefinition<ChangeStreamDocument<MongoQueueItem<TestPayload>>, TResult> pipeline,
                                                                   ChangeStreamOptions options,
                                                                   CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual IChangeStreamCursor<TResult> Watch<TResult>(IClientSessionHandle session,
                                                                   PipelineDefinition<ChangeStreamDocument<MongoQueueItem<TestPayload>>, TResult> pipeline,
                                                                   ChangeStreamOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public virtual Task<IChangeStreamCursor<TResult>> WatchAsync<TResult>(PipelineDefinition<ChangeStreamDocument<MongoQueueItem<TestPayload>>, TResult> pipeline,
                                                                              ChangeStreamOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public virtual Task<IChangeStreamCursor<TResult>> WatchAsync<TResult>(IClientSessionHandle session,
                                                                              PipelineDefinition<ChangeStreamDocument<MongoQueueItem<TestPayload>>, TResult> pipeline,
                                                                              ChangeStreamOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public virtual IMongoCollection<MongoQueueItem<TestPayload>> WithReadConcern(ReadConcern readConcern) => throw new NotSupportedException();

        public IMongoCollection<MongoQueueItem<TestPayload>> WithReadPreference(ReadPreference readPreference)
        {
            RequestedReadPreference = readPreference;
            return this;
        }

        public virtual IMongoCollection<MongoQueueItem<TestPayload>> WithWriteConcern(WriteConcern writeConcern) => throw new NotSupportedException();

        IFilteredMongoCollection<TDerivedDocument> IMongoCollection<MongoQueueItem<TestPayload>>.OfType<TDerivedDocument>() => throw new NotSupportedException();
    }

    /// <summary>
    /// Hand-crafted collection double that records the rendered filter and update/delete definition, plus the
    /// cancellation token, passed to its completion/release write. Lets tests assert the exact persisted change
    /// (not just that some write happened) and that the write used the unlinked grace-period token rather than
    /// the caller's possibly already-cancelled ambient token. <see cref="FindOneAndUpdateAsync{TProjection}"/>
    /// returns the queue item with the given ID for lock acquisition.
    /// </summary>
    private sealed class RecordingQueueItemCollection : QueueItemCollectionTestDoubleBase
    {
        private readonly DateTime _createdUtc;
        private readonly ObjectId _queueItemId;

        public RecordingQueueItemCollection(ObjectId queueItemId, DateTime createdUtc)
        {
            _queueItemId = queueItemId;
            _createdUtc = createdUtc;
        }

        public Int32 DeleteOneCallCount { get; private set; }
        public Int64 DeletedCount { get; set; } = 1;
        public ReadPreference? FindReadPreference { get; private set; }
        public CancellationToken LastDeleteCancellationToken { get; private set; }
        public BsonDocument? LastDeleteFilter { get; private set; }
        public CancellationToken LastUpdateCancellationToken { get; private set; }
        public BsonDocument? LastUpdateDefinition { get; private set; }
        public BsonDocument? LastUpdateFilter { get; private set; }
        public Int64 ModifiedCount { get; set; } = 1;
        public Int32 UpdateOneCallCount { get; private set; }

        public override Task<DeleteResult> DeleteOneAsync(FilterDefinition<MongoQueueItem<TestPayload>> filter, CancellationToken cancellationToken)
        {
            DeleteOneCallCount++;
            LastDeleteFilter = RenderFilter(filter);
            LastDeleteCancellationToken = cancellationToken;
            return Task.FromResult<DeleteResult>(new DeleteResult.Acknowledged(DeletedCount));
        }

        public override Task<IAsyncCursor<TProjection>> FindAsync<TProjection>(FilterDefinition<MongoQueueItem<TestPayload>> filter,
                                                                               FindOptions<MongoQueueItem<TestPayload>, TProjection> options,
                                                                               CancellationToken cancellationToken)
        {
            FindReadPreference = RequestedReadPreference;
            var queueItem = new MongoQueueItem<TestPayload>
            {
                Id = _queueItemId,
                CreatedUtc = _createdUtc,
                Payload = new TestPayload()
            };
            return Task.FromResult<IAsyncCursor<TProjection>>(new SingleItemAsyncCursor<TProjection>((TProjection)(Object)queueItem));
        }

        public override Task<TProjection> FindOneAndUpdateAsync<TProjection>(FilterDefinition<MongoQueueItem<TestPayload>> filter,
                                                                             UpdateDefinition<MongoQueueItem<TestPayload>> update,
                                                                             FindOneAndUpdateOptions<MongoQueueItem<TestPayload>, TProjection> options,
                                                                             CancellationToken cancellationToken)
        {
            var queueItem = new MongoQueueItem<TestPayload>
            {
                Id = _queueItemId,
                CreatedUtc = _createdUtc,
                Payload = new TestPayload()
            };
            return Task.FromResult((TProjection)(Object)queueItem);
        }

        public override Task<UpdateResult> UpdateOneAsync(FilterDefinition<MongoQueueItem<TestPayload>> filter, UpdateDefinition<MongoQueueItem<TestPayload>> update,
                                                          UpdateOptions options,
                                                          CancellationToken cancellationToken)
        {
            UpdateOneCallCount++;
            LastUpdateFilter = RenderFilter(filter);
            LastUpdateDefinition = RenderUpdate(update);
            LastUpdateCancellationToken = cancellationToken;
            return Task.FromResult<UpdateResult>(new UpdateResult.Acknowledged(ModifiedCount, ModifiedCount, null));
        }

        private static BsonDocument RenderFilter(FilterDefinition<MongoQueueItem<TestPayload>> filter)
            => filter.Render(new RenderArgs<MongoQueueItem<TestPayload>>(BsonSerializer.SerializerRegistry.GetSerializer<MongoQueueItem<TestPayload>>(),
                                                                         BsonSerializer.SerializerRegistry));

        private static BsonDocument RenderUpdate(UpdateDefinition<MongoQueueItem<TestPayload>> update)
            => update.Render(new RenderArgs<MongoQueueItem<TestPayload>>(BsonSerializer.SerializerRegistry.GetSerializer<MongoQueueItem<TestPayload>>(),
                                                                         BsonSerializer.SerializerRegistry)).AsBsonDocument;
    }

    private sealed class ScopedDependency;

    private sealed class ScopedTestPayloadHandler : IMongoQueuePayloadHandler<TestPayload>, IAsyncDisposable
    {
        private readonly DisposalTracker _tracker;

        public ScopedTestPayloadHandler(ScopedDependency scopedDependency, DisposalTracker tracker)
        {
            _tracker = tracker;
            _tracker.ResolvedScopedDependency = scopedDependency;
        }

        public ValueTask DisposeAsync()
        {
            _tracker.WasDisposed = true;
            return ValueTask.CompletedTask;
        }

        public Task HandlePayloadAsync(TestPayload payload, CancellationToken cancellationToken = default)
        {
            if (_tracker.ShouldCancel)
            {
                throw new OperationCanceledException();
            }

            if (_tracker.ShouldThrow)
            {
                throw new InvalidOperationException("boom");
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Minimal <see cref="IAsyncCursor{TDocument}"/> double yielding exactly one item, for stubbing the typed
    /// secondary read <c>ProcessQueueItemAsync</c> performs after a successful acquire.
    /// </summary>
    private sealed class SingleItemAsyncCursor<TItem> : IAsyncCursor<TItem>
    {
        private readonly TItem _item;
        private Boolean _moved;

        public SingleItemAsyncCursor(TItem item) => _item = item;

        public IEnumerable<TItem> Current => _moved ? [_item] : [];

        public Boolean MoveNext(CancellationToken cancellationToken = default)
        {
            if (_moved)
            {
                return false;
            }

            _moved = true;
            return true;
        }

        public Task<Boolean> MoveNextAsync(CancellationToken cancellationToken = default) => Task.FromResult(MoveNext(cancellationToken));

        public void Dispose() { }
    }

    /// <summary>
    /// Payload handler double that delegates to the given callback.
    /// </summary>
    private sealed class StubPayloadHandler : IMongoQueuePayloadHandler<TestPayload>
    {
        private readonly Func<Task> _handle;

        public StubPayloadHandler(Func<Task> handle) => _handle = handle;

        public Task HandlePayloadAsync(TestPayload payload, CancellationToken cancellationToken = default) => _handle();
    }

    /// <summary>
    /// Payload handler factory double that creates a <see cref="StubPayloadHandler"/> running the given callback.
    /// </summary>
    private sealed class StubPayloadHandlerFactory : IMongoQueuePayloadHandlerFactory
    {
        private readonly IMongoQueuePayloadHandler<TestPayload> _handler;

        public StubPayloadHandlerFactory(Func<Task> handle) => _handler = new StubPayloadHandler(handle);

        public IMongoQueuePayloadHandler<TPayload> CreateHandler<TPayload>(IServiceProvider serviceProvider) where TPayload : class, new()
            => (IMongoQueuePayloadHandler<TPayload>)_handler;
    }

    /// <summary>
    /// Payload handler factory double whose <see cref="CreateHandler{TPayload}"/> always throws, simulating a
    /// persistent DI resolution failure (for example an unregistered or misconfigured handler dependency) that
    /// happens before any payload is ever read or handled.
    /// </summary>
    private sealed class ThrowingPayloadHandlerFactory : IMongoQueuePayloadHandlerFactory
    {
        public IMongoQueuePayloadHandler<TPayload> CreateHandler<TPayload>(IServiceProvider serviceProvider) where TPayload : class, new()
            => throw new InvalidOperationException("Simulated DI resolution failure");
    }
}
