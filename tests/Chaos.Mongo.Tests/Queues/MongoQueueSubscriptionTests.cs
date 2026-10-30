// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests.Queues;

using Chaos.Mongo.Queues;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
                Payload = new TestPayload(),
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
                Payload = new TestPayload(),
                RetryCount = 2
            });
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
        payloadHandlerFactoryMock.Setup(x => x.CreateHandler<TestPayload>(It.IsAny<IServiceProvider>())).Returns(payloadHandlerMock.Object);
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
                Payload = new TestPayload(),
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
    public async Task ProcessQueueItemAsync_WhenHandlerIsCanceled_StillDisposesItemScope()
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
        tracker.WasDisposed.Should().BeTrue("the per-item scope must dispose the handler even when handling observes cancellation");
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
                       It.IsAny<FindOneAndUpdateOptions<MongoQueueItem<TestPayload>, MongoQueueItem<TestPayload>>>(),
                       It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MongoQueueItem<TestPayload>
            {
                Id = queueItemId,
                CreatedUtc = DateTime.UtcNow.AddMinutes(-1),
                Payload = new TestPayload(),
                RetryCount = 1
            });
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
                       It.IsAny<FindOneAndUpdateOptions<MongoQueueItem<TestPayload>, MongoQueueItem<TestPayload>>>(),
                       It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MongoQueueItem<TestPayload>
            {
                Id = queueItemId,
                CreatedUtc = createdUtc,
                IsLocked = true,
                LockedUtc = recoveredLockUtc,
                Payload = new TestPayload(),
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
    /// Hand-crafted collection double simulating a successful acquire-then-complete sequence for a single queue
    /// item:
    /// <see
    ///     cref="FindOneAndUpdateAsync{TProjection}(FilterDefinition{MongoQueueItem{TestPayload}}, UpdateDefinition{MongoQueueItem{TestPayload}}, FindOneAndUpdateOptions{MongoQueueItem{TestPayload}, TProjection}, CancellationToken)"/>
    /// returns the queue item with the given ID, and
    /// <see
    ///     cref="UpdateOneAsync(FilterDefinition{MongoQueueItem{TestPayload}}, UpdateDefinition{MongoQueueItem{TestPayload}}, UpdateOptions, CancellationToken)"/>
    /// reports it as modified. Every other member is unreachable for the per-item-scope tests that use this
    /// double and throws deliberately.
    /// </summary>
    private sealed class CompletingQueueItemCollection : IMongoCollection<MongoQueueItem<TestPayload>>
    {
        private readonly ObjectId _queueItemId;

        public CompletingQueueItemCollection(ObjectId queueItemId) => _queueItemId = queueItemId;

        public CollectionNamespace CollectionNamespace => throw new NotSupportedException();
        public IMongoDatabase Database => throw new NotSupportedException();
        public IBsonSerializer<MongoQueueItem<TestPayload>> DocumentSerializer => throw new NotSupportedException();
        public IMongoIndexManager<MongoQueueItem<TestPayload>> Indexes => throw new NotSupportedException();
        public IMongoSearchIndexManager SearchIndexes => throw new NotSupportedException();
        public MongoCollectionSettings Settings => throw new NotSupportedException();

        public IAsyncCursor<TResult> Aggregate<TResult>(PipelineDefinition<MongoQueueItem<TestPayload>, TResult> pipeline, AggregateOptions options,
                                                        CancellationToken cancellationToken) => throw new NotSupportedException();

        public IAsyncCursor<TResult> Aggregate<TResult>(IClientSessionHandle session, PipelineDefinition<MongoQueueItem<TestPayload>, TResult> pipeline, AggregateOptions options,
                                                        CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IAsyncCursor<TResult>> AggregateAsync<TResult>(PipelineDefinition<MongoQueueItem<TestPayload>, TResult> pipeline, AggregateOptions options,
                                                                   CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IAsyncCursor<TResult>> AggregateAsync<TResult>(IClientSessionHandle session, PipelineDefinition<MongoQueueItem<TestPayload>, TResult> pipeline,
                                                                   AggregateOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void AggregateToCollection<TResult>(PipelineDefinition<MongoQueueItem<TestPayload>, TResult> pipeline, AggregateOptions options,
                                                   CancellationToken cancellationToken) => throw new NotSupportedException();

        public void AggregateToCollection<TResult>(IClientSessionHandle session, PipelineDefinition<MongoQueueItem<TestPayload>, TResult> pipeline, AggregateOptions options,
                                                   CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task AggregateToCollectionAsync<TResult>(PipelineDefinition<MongoQueueItem<TestPayload>, TResult> pipeline, AggregateOptions options,
                                                        CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task AggregateToCollectionAsync<TResult>(IClientSessionHandle session, PipelineDefinition<MongoQueueItem<TestPayload>, TResult> pipeline,
                                                        AggregateOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public BulkWriteResult<MongoQueueItem<TestPayload>> BulkWrite(IEnumerable<WriteModel<MongoQueueItem<TestPayload>>> requests, BulkWriteOptions options,
                                                                      CancellationToken cancellationToken) => throw new NotSupportedException();

        public BulkWriteResult<MongoQueueItem<TestPayload>> BulkWrite(IClientSessionHandle session, IEnumerable<WriteModel<MongoQueueItem<TestPayload>>> requests,
                                                                      BulkWriteOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<BulkWriteResult<MongoQueueItem<TestPayload>>> BulkWriteAsync(IEnumerable<WriteModel<MongoQueueItem<TestPayload>>> requests, BulkWriteOptions options,
                                                                                 CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<BulkWriteResult<MongoQueueItem<TestPayload>>> BulkWriteAsync(IClientSessionHandle session, IEnumerable<WriteModel<MongoQueueItem<TestPayload>>> requests,
                                                                                 BulkWriteOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Int64 Count(FilterDefinition<MongoQueueItem<TestPayload>> filter, CountOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Int64 Count(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter, CountOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Int64> CountAsync(FilterDefinition<MongoQueueItem<TestPayload>> filter, CountOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Int64> CountAsync(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter, CountOptions options,
                                      CancellationToken cancellationToken) => throw new NotSupportedException();

        public Int64 CountDocuments(FilterDefinition<MongoQueueItem<TestPayload>> filter, CountOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Int64 CountDocuments(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter, CountOptions options,
                                    CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Int64> CountDocumentsAsync(FilterDefinition<MongoQueueItem<TestPayload>> filter, CountOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Int64> CountDocumentsAsync(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter, CountOptions options,
                                               CancellationToken cancellationToken) => throw new NotSupportedException();

        public DeleteResult DeleteMany(FilterDefinition<MongoQueueItem<TestPayload>> filter, CancellationToken cancellationToken) => throw new NotSupportedException();

        public DeleteResult DeleteMany(FilterDefinition<MongoQueueItem<TestPayload>> filter, DeleteOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public DeleteResult DeleteMany(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter, DeleteOptions options,
                                       CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DeleteResult> DeleteManyAsync(FilterDefinition<MongoQueueItem<TestPayload>> filter, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DeleteResult> DeleteManyAsync(FilterDefinition<MongoQueueItem<TestPayload>> filter, DeleteOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DeleteResult> DeleteManyAsync(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter, DeleteOptions options,
                                                  CancellationToken cancellationToken) => throw new NotSupportedException();

        public DeleteResult DeleteOne(FilterDefinition<MongoQueueItem<TestPayload>> filter, CancellationToken cancellationToken) => throw new NotSupportedException();

        public DeleteResult DeleteOne(FilterDefinition<MongoQueueItem<TestPayload>> filter, DeleteOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public DeleteResult DeleteOne(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter, DeleteOptions options,
                                      CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DeleteResult> DeleteOneAsync(FilterDefinition<MongoQueueItem<TestPayload>> filter, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DeleteResult> DeleteOneAsync(FilterDefinition<MongoQueueItem<TestPayload>> filter, DeleteOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DeleteResult> DeleteOneAsync(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter, DeleteOptions options,
                                                 CancellationToken cancellationToken) => throw new NotSupportedException();

        public IAsyncCursor<TField> Distinct<TField>(FieldDefinition<MongoQueueItem<TestPayload>, TField> field, FilterDefinition<MongoQueueItem<TestPayload>> filter,
                                                     DistinctOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public IAsyncCursor<TField> Distinct<TField>(IClientSessionHandle session, FieldDefinition<MongoQueueItem<TestPayload>, TField> field,
                                                     FilterDefinition<MongoQueueItem<TestPayload>> filter, DistinctOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IAsyncCursor<TField>> DistinctAsync<TField>(FieldDefinition<MongoQueueItem<TestPayload>, TField> field, FilterDefinition<MongoQueueItem<TestPayload>> filter,
                                                                DistinctOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IAsyncCursor<TField>> DistinctAsync<TField>(IClientSessionHandle session, FieldDefinition<MongoQueueItem<TestPayload>, TField> field,
                                                                FilterDefinition<MongoQueueItem<TestPayload>> filter, DistinctOptions options,
                                                                CancellationToken cancellationToken) => throw new NotSupportedException();

        public IAsyncCursor<TItem> DistinctMany<TItem>(FieldDefinition<MongoQueueItem<TestPayload>, IEnumerable<TItem>> field,
                                                       FilterDefinition<MongoQueueItem<TestPayload>> filter, DistinctOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public IAsyncCursor<TItem> DistinctMany<TItem>(IClientSessionHandle session, FieldDefinition<MongoQueueItem<TestPayload>, IEnumerable<TItem>> field,
                                                       FilterDefinition<MongoQueueItem<TestPayload>> filter, DistinctOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IAsyncCursor<TItem>> DistinctManyAsync<TItem>(FieldDefinition<MongoQueueItem<TestPayload>, IEnumerable<TItem>> field,
                                                                  FilterDefinition<MongoQueueItem<TestPayload>> filter, DistinctOptions options,
                                                                  CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IAsyncCursor<TItem>> DistinctManyAsync<TItem>(IClientSessionHandle session, FieldDefinition<MongoQueueItem<TestPayload>, IEnumerable<TItem>> field,
                                                                  FilterDefinition<MongoQueueItem<TestPayload>> filter, DistinctOptions options,
                                                                  CancellationToken cancellationToken) => throw new NotSupportedException();

        public Int64 EstimatedDocumentCount(EstimatedDocumentCountOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Int64> EstimatedDocumentCountAsync(EstimatedDocumentCountOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IAsyncCursor<TProjection>> FindAsync<TProjection>(FilterDefinition<MongoQueueItem<TestPayload>> filter,
                                                                      FindOptions<MongoQueueItem<TestPayload>, TProjection> options,
                                                                      CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IAsyncCursor<TProjection>> FindAsync<TProjection>(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter,
                                                                      FindOptions<MongoQueueItem<TestPayload>, TProjection> options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public TProjection FindOneAndDelete<TProjection>(FilterDefinition<MongoQueueItem<TestPayload>> filter,
                                                         FindOneAndDeleteOptions<MongoQueueItem<TestPayload>, TProjection> options,
                                                         CancellationToken cancellationToken) => throw new NotSupportedException();

        public TProjection FindOneAndDelete<TProjection>(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter,
                                                         FindOneAndDeleteOptions<MongoQueueItem<TestPayload>, TProjection> options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TProjection> FindOneAndDeleteAsync<TProjection>(FilterDefinition<MongoQueueItem<TestPayload>> filter,
                                                                    FindOneAndDeleteOptions<MongoQueueItem<TestPayload>, TProjection> options,
                                                                    CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TProjection> FindOneAndDeleteAsync<TProjection>(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter,
                                                                    FindOneAndDeleteOptions<MongoQueueItem<TestPayload>, TProjection> options,
                                                                    CancellationToken cancellationToken) => throw new NotSupportedException();

        public TProjection FindOneAndReplace<TProjection>(FilterDefinition<MongoQueueItem<TestPayload>> filter, MongoQueueItem<TestPayload> replacement,
                                                          FindOneAndReplaceOptions<MongoQueueItem<TestPayload>, TProjection> options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public TProjection FindOneAndReplace<TProjection>(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter,
                                                          MongoQueueItem<TestPayload> replacement, FindOneAndReplaceOptions<MongoQueueItem<TestPayload>, TProjection> options,
                                                          CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TProjection> FindOneAndReplaceAsync<TProjection>(FilterDefinition<MongoQueueItem<TestPayload>> filter, MongoQueueItem<TestPayload> replacement,
                                                                     FindOneAndReplaceOptions<MongoQueueItem<TestPayload>, TProjection> options,
                                                                     CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TProjection> FindOneAndReplaceAsync<TProjection>(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter,
                                                                     MongoQueueItem<TestPayload> replacement,
                                                                     FindOneAndReplaceOptions<MongoQueueItem<TestPayload>, TProjection> options,
                                                                     CancellationToken cancellationToken) => throw new NotSupportedException();

        public TProjection FindOneAndUpdate<TProjection>(FilterDefinition<MongoQueueItem<TestPayload>> filter, UpdateDefinition<MongoQueueItem<TestPayload>> update,
                                                         FindOneAndUpdateOptions<MongoQueueItem<TestPayload>, TProjection> options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public TProjection FindOneAndUpdate<TProjection>(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter,
                                                         UpdateDefinition<MongoQueueItem<TestPayload>> update,
                                                         FindOneAndUpdateOptions<MongoQueueItem<TestPayload>, TProjection> options,
                                                         CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TProjection> FindOneAndUpdateAsync<TProjection>(FilterDefinition<MongoQueueItem<TestPayload>> filter, UpdateDefinition<MongoQueueItem<TestPayload>> update,
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

        public Task<TProjection> FindOneAndUpdateAsync<TProjection>(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter,
                                                                    UpdateDefinition<MongoQueueItem<TestPayload>> update,
                                                                    FindOneAndUpdateOptions<MongoQueueItem<TestPayload>, TProjection> options,
                                                                    CancellationToken cancellationToken) => throw new NotSupportedException();

        public IAsyncCursor<TProjection> FindSync<TProjection>(FilterDefinition<MongoQueueItem<TestPayload>> filter, FindOptions<MongoQueueItem<TestPayload>, TProjection> options,
                                                               CancellationToken cancellationToken) => throw new NotSupportedException();

        public IAsyncCursor<TProjection> FindSync<TProjection>(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter,
                                                               FindOptions<MongoQueueItem<TestPayload>, TProjection> options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void InsertMany(IEnumerable<MongoQueueItem<TestPayload>> documents, InsertManyOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void InsertMany(IClientSessionHandle session, IEnumerable<MongoQueueItem<TestPayload>> documents, InsertManyOptions options,
                               CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task InsertManyAsync(IEnumerable<MongoQueueItem<TestPayload>> documents, InsertManyOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task InsertManyAsync(IClientSessionHandle session, IEnumerable<MongoQueueItem<TestPayload>> documents, InsertManyOptions options,
                                    CancellationToken cancellationToken) => throw new NotSupportedException();

        public void InsertOne(MongoQueueItem<TestPayload> document, InsertOneOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void InsertOne(IClientSessionHandle session, MongoQueueItem<TestPayload> document, InsertOneOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task InsertOneAsync(MongoQueueItem<TestPayload> document, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task InsertOneAsync(MongoQueueItem<TestPayload> document, InsertOneOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task InsertOneAsync(IClientSessionHandle session, MongoQueueItem<TestPayload> document, InsertOneOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public IAsyncCursor<TResult> MapReduce<TResult>(BsonJavaScript map, BsonJavaScript reduce, MapReduceOptions<MongoQueueItem<TestPayload>, TResult> options,
                                                        CancellationToken cancellationToken) => throw new NotSupportedException();

        public IAsyncCursor<TResult> MapReduce<TResult>(IClientSessionHandle session, BsonJavaScript map, BsonJavaScript reduce,
                                                        MapReduceOptions<MongoQueueItem<TestPayload>, TResult> options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IAsyncCursor<TResult>> MapReduceAsync<TResult>(BsonJavaScript map, BsonJavaScript reduce, MapReduceOptions<MongoQueueItem<TestPayload>, TResult> options,
                                                                   CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IAsyncCursor<TResult>> MapReduceAsync<TResult>(IClientSessionHandle session, BsonJavaScript map, BsonJavaScript reduce,
                                                                   MapReduceOptions<MongoQueueItem<TestPayload>, TResult> options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ReplaceOneResult ReplaceOne(FilterDefinition<MongoQueueItem<TestPayload>> filter, MongoQueueItem<TestPayload> replacement, ReplaceOptions options,
                                           CancellationToken cancellationToken) => throw new NotSupportedException();

        public ReplaceOneResult ReplaceOne(FilterDefinition<MongoQueueItem<TestPayload>> filter, MongoQueueItem<TestPayload> replacement, UpdateOptions options,
                                           CancellationToken cancellationToken) => throw new NotSupportedException();

        public ReplaceOneResult ReplaceOne(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter, MongoQueueItem<TestPayload> replacement,
                                           ReplaceOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ReplaceOneResult ReplaceOne(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter, MongoQueueItem<TestPayload> replacement,
                                           UpdateOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ReplaceOneResult> ReplaceOneAsync(FilterDefinition<MongoQueueItem<TestPayload>> filter, MongoQueueItem<TestPayload> replacement, ReplaceOptions options,
                                                      CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ReplaceOneResult> ReplaceOneAsync(FilterDefinition<MongoQueueItem<TestPayload>> filter, MongoQueueItem<TestPayload> replacement, UpdateOptions options,
                                                      CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ReplaceOneResult> ReplaceOneAsync(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter, MongoQueueItem<TestPayload> replacement,
                                                      ReplaceOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ReplaceOneResult> ReplaceOneAsync(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter, MongoQueueItem<TestPayload> replacement,
                                                      UpdateOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public UpdateResult UpdateMany(FilterDefinition<MongoQueueItem<TestPayload>> filter, UpdateDefinition<MongoQueueItem<TestPayload>> update, UpdateOptions options,
                                       CancellationToken cancellationToken) => throw new NotSupportedException();

        public UpdateResult UpdateMany(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter, UpdateDefinition<MongoQueueItem<TestPayload>> update,
                                       UpdateOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<UpdateResult> UpdateManyAsync(FilterDefinition<MongoQueueItem<TestPayload>> filter, UpdateDefinition<MongoQueueItem<TestPayload>> update, UpdateOptions options,
                                                  CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<UpdateResult> UpdateManyAsync(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter,
                                                  UpdateDefinition<MongoQueueItem<TestPayload>> update, UpdateOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public UpdateResult UpdateOne(FilterDefinition<MongoQueueItem<TestPayload>> filter, UpdateDefinition<MongoQueueItem<TestPayload>> update, UpdateOptions options,
                                      CancellationToken cancellationToken) => throw new NotSupportedException();

        public UpdateResult UpdateOne(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter, UpdateDefinition<MongoQueueItem<TestPayload>> update,
                                      UpdateOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<UpdateResult> UpdateOneAsync(FilterDefinition<MongoQueueItem<TestPayload>> filter, UpdateDefinition<MongoQueueItem<TestPayload>> update, UpdateOptions options,
                                                 CancellationToken cancellationToken)
            => Task.FromResult<UpdateResult>(new UpdateResult.Acknowledged(1, 1, null));

        public Task<UpdateResult> UpdateOneAsync(IClientSessionHandle session, FilterDefinition<MongoQueueItem<TestPayload>> filter,
                                                 UpdateDefinition<MongoQueueItem<TestPayload>> update, UpdateOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public IChangeStreamCursor<TResult> Watch<TResult>(PipelineDefinition<ChangeStreamDocument<MongoQueueItem<TestPayload>>, TResult> pipeline, ChangeStreamOptions options,
                                                           CancellationToken cancellationToken) => throw new NotSupportedException();

        public IChangeStreamCursor<TResult> Watch<TResult>(IClientSessionHandle session, PipelineDefinition<ChangeStreamDocument<MongoQueueItem<TestPayload>>, TResult> pipeline,
                                                           ChangeStreamOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IChangeStreamCursor<TResult>> WatchAsync<TResult>(PipelineDefinition<ChangeStreamDocument<MongoQueueItem<TestPayload>>, TResult> pipeline,
                                                                      ChangeStreamOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IChangeStreamCursor<TResult>> WatchAsync<TResult>(IClientSessionHandle session,
                                                                      PipelineDefinition<ChangeStreamDocument<MongoQueueItem<TestPayload>>, TResult> pipeline,
                                                                      ChangeStreamOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public IMongoCollection<MongoQueueItem<TestPayload>> WithReadConcern(ReadConcern readConcern) => throw new NotSupportedException();
        public IMongoCollection<MongoQueueItem<TestPayload>> WithReadPreference(ReadPreference readPreference) => throw new NotSupportedException();
        public IMongoCollection<MongoQueueItem<TestPayload>> WithWriteConcern(WriteConcern writeConcern) => throw new NotSupportedException();

        IFilteredMongoCollection<TDerivedDocument> IMongoCollection<MongoQueueItem<TestPayload>>.OfType<TDerivedDocument>() => throw new NotSupportedException();
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
}
