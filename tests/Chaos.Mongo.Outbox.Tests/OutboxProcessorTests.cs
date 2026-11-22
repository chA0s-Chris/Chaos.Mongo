// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox.Tests;

using Chaos.Mongo.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Moq;
using NUnit.Framework;

public class OutboxProcessorTests
{
    private Mock<IMongoCollection<OutboxMessage>> _collectionMock;
    private Mock<IMongoDatabase> _databaseMock;
    private IServiceScopeFactory? _handCraftedScopeFactory;
    private Mock<ILogger<OutboxProcessor>> _loggerMock;
    private Mock<IMongoHelper> _mongoHelperMock;
    private OutboxOptions _options;
    private Mock<IOutboxPublisher> _publisherMock;
    private Mock<IServiceScopeFactory> _scopeFactoryMock;
    private FakeTimeProvider _timeProvider;

    /// <summary>
    /// Rendering resolves the <see cref="OutboxMessage"/> serializer through the driver, which registers a plain class
    /// map if the outbox's own registration has not run yet. Fixtures run in parallel, so no other fixture can be relied
    /// on to have registered it first.
    /// </summary>
    [OneTimeSetUp]
    public void RegisterOutboxClassMaps() => OutboxSerializationSetup.RegisterClassMaps(new Dictionary<Type, String>());

    [SetUp]
    public void SetUp()
    {
        _options = new OutboxOptions
        {
            CollectionName = "TestOutbox",
            BatchSize = 10,
            MaxRetries = 3,
            PollingInterval = TimeSpan.FromMilliseconds(50),
            LockLeaseTime = TimeSpan.FromMinutes(5),
            RetryBackoffInitialDelay = TimeSpan.FromSeconds(1),
            RetryBackoffMaxDelay = TimeSpan.FromSeconds(30)
        };

        _timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        _loggerMock = new Mock<ILogger<OutboxProcessor>>();

        _collectionMock = new Mock<IMongoCollection<OutboxMessage>>();
        _databaseMock = new Mock<IMongoDatabase>();
        _databaseMock
            .Setup(d => d.GetCollection<OutboxMessage>(_options.CollectionName, null))
            .Returns(_collectionMock.Object);

        _mongoHelperMock = new Mock<IMongoHelper>();
        _mongoHelperMock.Setup(h => h.Database).Returns(_databaseMock.Object);

        _publisherMock = new Mock<IOutboxPublisher>();

        var serviceScopeMock = new Mock<IServiceScope>();
        var services = new ServiceCollection();
        services.AddSingleton(_publisherMock.Object);
        serviceScopeMock.Setup(s => s.ServiceProvider).Returns(services.BuildServiceProvider());

        _scopeFactoryMock = new Mock<IServiceScopeFactory>();
        _scopeFactoryMock.Setup(f => f.CreateScope()).Returns(serviceScopeMock.Object);
        _handCraftedScopeFactory = null;
    }

    [Test]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        var act = () => new OutboxProcessor(
            _mongoHelperMock.Object, _options, _scopeFactoryMock.Object, _timeProvider, null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    [Test]
    public void Constructor_NullMongoHelper_ThrowsArgumentNullException()
    {
        var act = () => new OutboxProcessor(
            null!, _options, _scopeFactoryMock.Object, _timeProvider, _loggerMock.Object);

        act.Should().Throw<ArgumentNullException>().WithParameterName("mongoHelper");
    }

    [Test]
    public void Constructor_NullOptions_ThrowsArgumentNullException()
    {
        var act = () => new OutboxProcessor(
            _mongoHelperMock.Object, null!, _scopeFactoryMock.Object, _timeProvider, _loggerMock.Object);

        act.Should().Throw<ArgumentNullException>().WithParameterName("options");
    }

    [Test]
    public void Constructor_NullServiceScopeFactory_ThrowsArgumentNullException()
    {
        var act = () => new OutboxProcessor(
            _mongoHelperMock.Object, _options, null!, _timeProvider, _loggerMock.Object);

        act.Should().Throw<ArgumentNullException>().WithParameterName("serviceScopeFactory");
    }

    [Test]
    public void Constructor_NullTimeProvider_ThrowsArgumentNullException()
    {
        var act = () => new OutboxProcessor(
            _mongoHelperMock.Object, _options, _scopeFactoryMock.Object, null!, _loggerMock.Object);

        act.Should().Throw<ArgumentNullException>().WithParameterName("timeProvider");
    }

    [Test]
    public async Task HandleFailure_OwnershipLostDuringUpdate_LogsWarning()
    {
        var message = CreatePendingMessage();
        var processingCompleted = CreateSignal();
        SetupFindReturns(message);
        SetupClaimReturns(message);

        _publisherMock
            .Setup(p => p.PublishAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Broker down"));

        // Failure update returns ModifiedCount == 0 → ownership was lost
        SetupUpdateOneResult(0, () => processingCompleted.TrySetResult(true));

        var sut = CreateSut();
        await sut.StartAsync();
        await WaitForSignalAsync(processingCompleted);
        await sut.StopAsync();

        VerifyLoggedWarning("failure update skipped");
    }

    [Test]
    public async Task ProcessLoop_MongoException_ContinuesAfterDelay()
    {
        var callCount = 0;
        var exceptionThrown = CreateSignal();
        var retryObserved = CreateSignal();

        // First call to Find throws MongoException, subsequent calls return empty
        _collectionMock
            .Setup(c => c.FindSync(
                       It.IsAny<FilterDefinition<OutboxMessage>>(),
                       It.IsAny<FindOptions<OutboxMessage, OutboxMessage>>(),
                       It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                if (callCount++ == 0)
                {
                    exceptionThrown.TrySetResult(true);
                    throw new MongoException("Transient error");
                }

                retryObserved.TrySetResult(true);
            })
            .Returns(CreateEmptyCursor());

        // Also set up the async path — the driver may use either
        _collectionMock
            .Setup(c => c.FindAsync(
                       It.IsAny<FilterDefinition<OutboxMessage>>(),
                       It.IsAny<FindOptions<OutboxMessage, OutboxMessage>>(),
                       It.IsAny<CancellationToken>()))
            .Returns((FilterDefinition<OutboxMessage> _, FindOptions<OutboxMessage, OutboxMessage> _, CancellationToken _) =>
            {
                if (callCount++ == 0)
                {
                    exceptionThrown.TrySetResult(true);
                    throw new MongoException("Transient error");
                }

                retryObserved.TrySetResult(true);

                return Task.FromResult(CreateEmptyAsyncCursor());
            });

        var sut = CreateSut();
        await sut.StartAsync();

        // Wait until the exception is thrown, then advance fake time in small steps.
        // This gives the background task a chance to register its Task.Delay timer
        // before each advance, avoiding the race where Advance() fires before the
        // timer is created.
        await WaitForSignalAsync(exceptionThrown);
        for (var i = 0; i < 12 && !retryObserved.Task.IsCompleted; i++)
        {
            await Task.Yield();
            _timeProvider.Advance(TimeSpan.FromMilliseconds(500));
        }

        await WaitForSignalAsync(retryObserved);
        await sut.StopAsync();

        // The processor should have survived the exception (no unhandled throw)
        VerifyLoggedError("Transient MongoDB error");
    }

    [Test]
    public async Task ProcessLoop_UnexpectedException_ContinuesAfterDelay()
    {
        var callCount = 0;
        var exceptionThrown = CreateSignal();
        var retryObserved = CreateSignal();

        _collectionMock
            .Setup(c => c.FindSync(
                       It.IsAny<FilterDefinition<OutboxMessage>>(),
                       It.IsAny<FindOptions<OutboxMessage, OutboxMessage>>(),
                       It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                if (callCount++ == 0)
                {
                    exceptionThrown.TrySetResult(true);
                    throw new InvalidOperationException("Something unexpected");
                }

                retryObserved.TrySetResult(true);
            })
            .Returns(CreateEmptyCursor());

        _collectionMock
            .Setup(c => c.FindAsync(
                       It.IsAny<FilterDefinition<OutboxMessage>>(),
                       It.IsAny<FindOptions<OutboxMessage, OutboxMessage>>(),
                       It.IsAny<CancellationToken>()))
            .Returns((FilterDefinition<OutboxMessage> _, FindOptions<OutboxMessage, OutboxMessage> _, CancellationToken _) =>
            {
                if (callCount++ == 0)
                {
                    exceptionThrown.TrySetResult(true);
                    throw new InvalidOperationException("Something unexpected");
                }

                retryObserved.TrySetResult(true);

                return Task.FromResult(CreateEmptyAsyncCursor());
            });

        var sut = CreateSut();
        await sut.StartAsync();

        // Wait until the exception is thrown, then advance fake time in small steps.
        // This gives the background task a chance to register its Task.Delay timer
        // before each advance, avoiding the race where Advance() fires before the
        // timer is created.
        await WaitForSignalAsync(exceptionThrown);
        for (var i = 0; i < 12 && !retryObserved.Task.IsCompleted; i++)
        {
            await Task.Yield();
            _timeProvider.Advance(TimeSpan.FromMilliseconds(500));
        }

        await WaitForSignalAsync(retryObserved);
        await sut.StopAsync();

        VerifyLoggedError("Unexpected error");
    }

    [Test]
    public async Task ProcessMessage_CancellationDuringPublish_ReleasesLockAndStops()
    {
        var message = CreatePendingMessage();
        var lockReleased = CreateSignal();
        SetupFindReturns(message);
        SetupClaimReturns(message);

        using var cts = new CancellationTokenSource();

        _publisherMock
            .Setup(p => p.PublishAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback(cts.Cancel)
            .ThrowsAsync(new OperationCanceledException());

        // Lock release update — should be attempted
        SetupUpdateOneResult(1, () => lockReleased.TrySetResult(true));

        var sut = CreateSut();
        await sut.StartAsync(cts.Token);

        // Wait for the loop to exit after cancellation
        await WaitForSignalAsync(lockReleased);
        await sut.StopAsync(cts.Token);

        // Verify UpdateOneAsync was called (for the lock release)
        _collectionMock.Verify(
            c => c.UpdateOneAsync(
                It.IsAny<FilterDefinition<OutboxMessage>>(),
                It.IsAny<UpdateDefinition<OutboxMessage>>(),
                It.IsAny<UpdateOptions>(),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Test]
    public async Task ProcessMessage_ClaimFails_SkipsMessageWithoutPublishing()
    {
        var message = CreatePendingMessage();
        var claimAttempted = CreateSignal();
        SetupFindReturns(message);

        // FindOneAndUpdate returns null → claim failed
        SetupClaimReturns(null, () => claimAttempted.TrySetResult(true));

        var sut = CreateSut();
        await sut.StartAsync();
        await WaitForSignalAsync(claimAttempted);
        await sut.StopAsync();

        _publisherMock.Verify(
            p => p.PublishAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task ProcessMessage_FinalizationTimesOut_ReleasesLockWithoutCountingFailure()
    {
        var message = CreatePendingMessage();
        var finalizing = CreateSignal();
        var lockReleased = CreateSignal();
        var updates = new List<UpdateDefinition<OutboxMessage>>();
        SetupFindReturns(message);
        SetupClaimReturns(message);

        _publisherMock
            .Setup(p => p.PublishAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var resultMock = new Mock<UpdateResult>();
        resultMock.Setup(r => r.ModifiedCount).Returns(1);

        _collectionMock
            .Setup(c => c.UpdateOneAsync(
                       It.IsAny<FilterDefinition<OutboxMessage>>(),
                       It.IsAny<UpdateDefinition<OutboxMessage>>(),
                       It.IsAny<UpdateOptions>(),
                       It.IsAny<CancellationToken>()))
            .Returns(async (FilterDefinition<OutboxMessage> _, UpdateDefinition<OutboxMessage> update, UpdateOptions _, CancellationToken token) =>
            {
                updates.Add(update);
                if (updates.Count == 1)
                {
                    // The completion update never answers; it has to give up when the finalization timeout expires.
                    finalizing.TrySetResult(true);
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }

                lockReleased.TrySetResult(true);
                return resultMock.Object;
            });

        var sut = CreateSut();
        await sut.StartAsync();
        await WaitForSignalAsync(finalizing);

        // While the processor runs, elapsed time alone must not abandon the completion update.
        _timeProvider.Advance(TimeSpan.FromSeconds(4));
        await Task.Delay(100);
        lockReleased.Task.IsCompleted.Should().BeFalse("finalization is only bounded once a stop is requested");

        // The grace period is armed by the stop request; advance in steps so the registration can run first.
        var stopping = sut.StopAsync();
        for (var i = 0; i < 20 && !lockReleased.Task.IsCompleted; i++)
        {
            await Task.Delay(10);
            _timeProvider.Advance(TimeSpan.FromSeconds(1));
        }

        await WaitForSignalAsync(lockReleased);
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));

        updates.Should().HaveCount(2);
        var serializerRegistry = BsonSerializer.SerializerRegistry;
        var renderedRelease = updates[1].Render(new RenderArgs<OutboxMessage>(serializerRegistry.GetSerializer<OutboxMessage>(), serializerRegistry));
        renderedRelease["$set"].AsBsonDocument.Names.Should().BeEquivalentTo([nameof(OutboxMessage.IsLocked)]);
        renderedRelease["$unset"].AsBsonDocument.Names.Should().Contain(nameof(OutboxMessage.LockId));
        VerifyLoggedWarning("timed out");
    }

    [Test]
    public async Task ProcessMessage_PublishFails_MarksAsPermanentlyFailedWhenRetriesExhausted()
    {
        // The claim already incremented AttemptCount atomically before HandleFailureAsync ever runs, so the
        // seeded message must represent the already-claimed attempt count (MaxRetries + 1) that exceeds the
        // budget, not a pre-increment count HandleFailureAsync used to bump itself.
        var message = CreatePendingMessage(_options.MaxRetries!.Value + 1);
        var failureRecorded = CreateSignal();
        SetupFindReturns(message);
        SetupClaimReturns(message);

        _publisherMock
            .Setup(p => p.PublishAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Permanent failure"));

        SetupUpdateOneResult(1, () => failureRecorded.TrySetResult(true));

        var sut = CreateSut();
        await sut.StartAsync();
        await WaitForSignalAsync(failureRecorded);
        await sut.StopAsync();

        VerifyLoggedWarning("permanently failed");
    }

    [Test]
    public async Task ProcessMessage_PublishFails_SchedulesRetryAtMaxDelayWhenBackoffWouldOverflow()
    {
        _options = new OutboxOptions
        {
            CollectionName = "TestOutbox",
            BatchSize = 10,
            MaxRetries = 40,
            PollingInterval = TimeSpan.FromMilliseconds(50),
            LockLeaseTime = TimeSpan.FromMinutes(5),
            RetryBackoffInitialDelay = TimeSpan.FromHours(8),
            RetryBackoffMaxDelay = TimeSpan.FromDays(2)
        };

        _databaseMock
            .Setup(d => d.GetCollection<OutboxMessage>(_options.CollectionName, null))
            .Returns(_collectionMock.Object);

        var message = CreatePendingMessage(30);
        SetupFindReturns(message);
        SetupClaimReturns(message);

        _publisherMock
            .Setup(p => p.PublishAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Broker down"));

        var retryScheduled = CreateSignal();
        UpdateDefinition<OutboxMessage>? capturedUpdate = null;
        var resultMock = new Mock<UpdateResult>();
        resultMock.Setup(r => r.ModifiedCount).Returns(1);

        _collectionMock
            .Setup(c => c.UpdateOneAsync(
                       It.IsAny<FilterDefinition<OutboxMessage>>(),
                       It.IsAny<UpdateDefinition<OutboxMessage>>(),
                       It.IsAny<UpdateOptions>(),
                       It.IsAny<CancellationToken>()))
            .Callback<FilterDefinition<OutboxMessage>, UpdateDefinition<OutboxMessage>, UpdateOptions, CancellationToken>((_, update, _, _) =>
            {
                capturedUpdate = update;
                retryScheduled.TrySetResult(true);
            })
            .ReturnsAsync(resultMock.Object);

        var expectedNextAttemptUtc = _timeProvider.GetUtcNow().UtcDateTime.Add(_options.RetryBackoffMaxDelay);
        var expectedStoredNextAttemptUtc = new DateTime(
            expectedNextAttemptUtc.Ticks - (expectedNextAttemptUtc.Ticks % TimeSpan.TicksPerMillisecond),
            DateTimeKind.Utc);
        var sut = CreateSut();

        await sut.StartAsync();
        await WaitForSignalAsync(retryScheduled);
        await sut.StopAsync();

        capturedUpdate.Should().NotBeNull();

        var serializerRegistry = BsonSerializer.SerializerRegistry;
        var documentSerializer = serializerRegistry.GetSerializer<OutboxMessage>();
        var renderContext = new RenderArgs<OutboxMessage>(documentSerializer, serializerRegistry);
        var renderedUpdate = capturedUpdate!.Render(renderContext);
        var scheduledNextAttemptUtc = renderedUpdate["$set"][nameof(OutboxMessage.NextAttemptUtc)].AsBsonDateTime.ToUniversalTime();

        scheduledNextAttemptUtc.Should().Be(expectedStoredNextAttemptUtc);
    }

    [Test]
    public async Task ProcessMessage_PublishFails_SchedulesRetryWhenRetriesNotExhausted()
    {
        var message = CreatePendingMessage();
        var retryScheduled = CreateSignal();
        SetupFindReturns(message);
        SetupClaimReturns(message);

        _publisherMock
            .Setup(p => p.PublishAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Broker down"));

        // HandleFailureAsync update succeeds
        SetupUpdateOneResult(1, () => retryScheduled.TrySetResult(true));

        var sut = CreateSut();
        await sut.StartAsync();
        await WaitForSignalAsync(retryScheduled);
        await sut.StopAsync();

        _collectionMock.Verify(
            c => c.UpdateOneAsync(
                It.IsAny<FilterDefinition<OutboxMessage>>(),
                It.IsAny<UpdateDefinition<OutboxMessage>>(),
                It.IsAny<UpdateOptions>(),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Test]
    public async Task ProcessMessage_PublishSucceeds_ButOwnershipLost_LogsWarning()
    {
        var message = CreatePendingMessage();
        var completionUpdateAttempted = CreateSignal();
        SetupFindReturns(message);
        SetupClaimReturns(message);

        _publisherMock
            .Setup(p => p.PublishAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Success update returns ModifiedCount == 0 → ownership lost
        SetupUpdateOneResult(0, () => completionUpdateAttempted.TrySetResult(true));

        var sut = CreateSut();
        await sut.StartAsync();
        await WaitForSignalAsync(completionUpdateAttempted);
        await sut.StopAsync();

        _publisherMock.Verify(
            p => p.PublishAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()),
            Times.Once);

        VerifyLoggedWarning("ownership was lost");
    }

    [Test]
    public async Task StartAsync_WhenAlreadyRunning_DoesNotStartAgain()
    {
        SetupFindReturnsEmpty();
        var sut = CreateSut();

        await sut.StartAsync();
        await sut.StartAsync(); // second call should be a no-op

        await sut.StopAsync();

        // The Find call proves the loop ran; we just verify no exception was thrown.
    }

    [Test]
    public async Task StopAsync_WhenNotStarted_DoesNotThrow()
    {
        var sut = CreateSut();

        var act = () => sut.StopAsync();

        await act.Should().NotThrowAsync();
    }

    [Test]
    public async Task StopAsync_CallbackThrows_LogsTheFailureAndStillStops()
    {
        var message = CreatePendingMessage();
        var publishing = CreateSignal();
        SetupFindReturns(message);
        SetupClaimReturns(message);
        SetupUpdateOneResult(1);
        var logger = new RecordingLogger<OutboxProcessor>();
        UsePublisher(new DelegatePublisher((_, token) =>
        {
            token.Register(() => throw new InvalidOperationException("callback failed"));
            publishing.TrySetResult(true);
            return Task.Delay(Timeout.InfiniteTimeSpan, token);
        }));
        var sut = CreateSut(logger);
        await sut.StartAsync();
        await WaitForSignalAsync(publishing);

        var act = () => sut.StopAsync();

        await act.Should().NotThrowAsync();
        var warning = logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Warning && entry.Exception != null).Subject;
        warning.Exception.Should().BeOfType<AggregateException>().Which.InnerException.Should().BeOfType<InvalidOperationException>()
               .Which.Message.Should().Be("callback failed");
        await sut.StartAsync();
        await sut.StopAsync();
    }

    [Test]
    public async Task StopAsync_StoppingLogThrows_StillCancelsJoinsAndSettlesTheStop()
    {
        var message = CreatePendingMessage();
        var publishing = CreateSignal();
        var publisherCancelled = CreateSignal();
        SetupFindReturns(message);
        SetupClaimReturns(message);
        SetupUpdateOneResult(1);
        var failuresLeft = 1;
        var logger = new RecordingLogger<OutboxProcessor>
        {
            OnLog = entry =>
            {
                if (entry.Message.StartsWith("Stopping outbox processor", StringComparison.Ordinal) && Interlocked.Decrement(ref failuresLeft) >= 0)
                {
                    throw new InvalidOperationException("logger failed");
                }
            }
        };
        UsePublisher(new DelegatePublisher(async (_, token) =>
        {
            token.Register(() => publisherCancelled.TrySetResult(true));
            publishing.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }));
        var sut = CreateSut(logger);
        await sut.StartAsync();
        await WaitForSignalAsync(publishing);

        var act = async () => await sut.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

        // The stop settles, with the logging failure, instead of never cancelling the loop and hanging its callers.
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("logger failed");
        await WaitForSignalAsync(publisherCancelled);
        await sut.StartAsync();
        await WaitForSignalAsync(publishing);
        await sut.StopAsync();
    }

    [Test]
    public async Task StartAsync_StartingLogThrows_NeverReachesThePublisherAndALaterStartSucceeds()
    {
        var message = CreatePendingMessage();
        var publishing = CreateSignal();
        var publishCalls = 0;
        SetupFindReturns(message);
        SetupClaimReturns(message);
        SetupUpdateOneResult(1);
        var failuresLeft = 1;
        var logger = new RecordingLogger<OutboxProcessor>
        {
            OnLog = entry =>
            {
                if (entry.Message.StartsWith("Starting outbox processor", StringComparison.Ordinal) && Interlocked.Decrement(ref failuresLeft) >= 0)
                {
                    throw new InvalidOperationException("logger failed");
                }
            }
        };
        UsePublisher(new DelegatePublisher(async (_, token) =>
        {
            Interlocked.Increment(ref publishCalls);
            publishing.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }));
        var sut = CreateSut(logger);

        var start = () => sut.StartAsync();

        await start.Should().ThrowAsync<InvalidOperationException>().WithMessage("logger failed");
        _timeProvider.Advance(_options.PollingInterval * 2);
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        publishCalls.Should().Be(0, "a start that failed must not process messages");
        await sut.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

        await sut.StartAsync();
        await WaitForSignalAsync(publishing);
        await sut.StopAsync();
    }

    [Test]
    public async Task StartAsync_StartingLogThrowsWhileAStopIsAdmitted_SettlesTheStopWithoutRunningTheLoop()
    {
        var message = CreatePendingMessage();
        var publishing = CreateSignal();
        var publishCalls = 0;
        SetupFindReturns(message);
        SetupClaimReturns(message);
        SetupUpdateOneResult(1);
        var processors = new List<OutboxProcessor>();
        var admittedStops = new List<Task>();
        var failuresLeft = 1;
        var logger = new RecordingLogger<OutboxProcessor>
        {
            OnLog = entry =>
            {
                if (entry.Message.StartsWith("Starting outbox processor", StringComparison.Ordinal) && Interlocked.Decrement(ref failuresLeft) >= 0)
                {
                    // The run is already published, so this stop is admitted while the start is still failing.
                    admittedStops.Add(processors.Single().StopAsync());
                    throw new InvalidOperationException("logger failed");
                }
            }
        };
        UsePublisher(new DelegatePublisher(async (_, token) =>
        {
            Interlocked.Increment(ref publishCalls);
            publishing.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }));
        var sut = CreateSut(logger);
        processors.Add(sut);

        var start = () => sut.StartAsync();

        await start.Should().ThrowAsync<InvalidOperationException>().WithMessage("logger failed");
        await admittedStops.Should().ContainSingle().Subject.WaitAsync(TimeSpan.FromSeconds(5));
        publishCalls.Should().Be(0, "the failed start never ran the loop");

        // The stop released the run, so a later start works.
        await sut.StartAsync();
        await WaitForSignalAsync(publishing);
        await sut.StopAsync();
    }

    [TestCase("Stopping outbox processor")]
    [TestCase("Outbox processor stopped")]
    public async Task StopAsync_StopLogThrows_DisposesTheLoggingScopeOnceAndReportsTheLogFailureAfterTheJoin(String failingMessage)
    {
        var message = CreatePendingMessage();
        var publishing = CreateSignal();
        var publisherCancelled = CreateSignal();
        SetupFindReturns(message);
        SetupClaimReturns(message);
        SetupUpdateOneResult(1);
        var scopesBegun = 0;
        var scopesDisposed = 0;
        var failuresLeft = 1;
        var logger = new RecordingLogger<OutboxProcessor>
        {
            OnBeginScope = () =>
            {
                Interlocked.Increment(ref scopesBegun);
                return new ActionDisposable(() => Interlocked.Increment(ref scopesDisposed));
            },
            OnLog = entry =>
            {
                if (entry.Message.StartsWith(failingMessage, StringComparison.Ordinal) && Interlocked.Decrement(ref failuresLeft) >= 0)
                {
                    throw new InvalidOperationException("logger failed");
                }
            }
        };
        UsePublisher(new DelegatePublisher(async (_, token) =>
        {
            token.Register(() => publisherCancelled.TrySetResult(true));
            publishing.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }));
        var sut = CreateSut(logger);
        await sut.StartAsync();
        await WaitForSignalAsync(publishing);

        var act = async () => await sut.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("logger failed");
        await WaitForSignalAsync(publisherCancelled);
        scopesBegun.Should().BeGreaterThanOrEqualTo(2, "the start and the stop each begin a scope");
        scopesDisposed.Should().Be(scopesBegun, "every logging scope is disposed exactly once, also on the error path");
    }

    [Test]
    public async Task StopAsync_StoppingLogBlocked_ReturnsTheFirstCallerWhoseTokenEndedAndKeepsTheStopSharedWithTheOverlappingCaller()
    {
        var message = CreatePendingMessage();
        var publishing = CreateSignal();
        SetupFindReturns(message);
        SetupClaimReturns(message);
        SetupUpdateOneResult(1);
        var stoppingLogEntered = CreateSignal();
        var releaseStoppingLog = CreateSignal();
        var logger = new RecordingLogger<OutboxProcessor>
        {
            OnLog = entry =>
            {
                if (entry.Message.StartsWith("Stopping outbox processor", StringComparison.Ordinal))
                {
                    stoppingLogEntered.TrySetResult(true);
                    releaseStoppingLog.Task.Wait(TimeSpan.FromSeconds(10));
                }
            }
        };
        UsePublisher(new DelegatePublisher(async (_, token) =>
        {
            publishing.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }));
        var sut = CreateSut(logger);
        await sut.StartAsync();
        await WaitForSignalAsync(publishing);
        using var firstCallerTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        Task first;
        Task second;

        try
        {
            // The log is provider code that blocks. It runs for every stop, always off the caller's stack, so the first
            // caller returns once its own token ended, however long the provider holds the log.
            first = sut.StopAsync(firstCallerTimeout.Token);
            await stoppingLogEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await first.WaitAsync(TimeSpan.FromSeconds(5));

            second = sut.StopAsync();
            await sut.StartAsync();

            (await Task.WhenAny(second, Task.Delay(TimeSpan.FromMilliseconds(200))) == second).Should()
                                                                                              .BeFalse("the stop is still pending, so the overlapping caller waits for it");
            logger.Entries.Should().Contain(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("is already running"));
        }
        finally
        {
            releaseStoppingLog.TrySetResult(true);
        }

        await second.WaitAsync(TimeSpan.FromSeconds(5));
        await sut.StartAsync();
        await sut.StopAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task StopAsync_FinalLogBlocked_KeepsAnOverlappingStopAndAStartWaitingForTheFirstStopsResult(Boolean firstCallerExpires)
    {
        var message = CreatePendingMessage();
        var publishing = CreateSignal();
        var publishCalls = 0;
        SetupFindReturns(message);
        SetupClaimReturns(message);
        SetupUpdateOneResult(1);
        var finalLogEntered = CreateSignal();
        var releaseFinalLog = CreateSignal();
        var failuresLeft = 1;
        var logger = new RecordingLogger<OutboxProcessor>
        {
            OnLog = entry =>
            {
                if (entry.Message.StartsWith("Outbox processor stopped", StringComparison.Ordinal) && Interlocked.Decrement(ref failuresLeft) >= 0)
                {
                    finalLogEntered.TrySetResult(true);
                    releaseFinalLog.Task.Wait(TimeSpan.FromSeconds(10));
                    throw new InvalidOperationException("logger failed");
                }
            }
        };
        UsePublisher(new DelegatePublisher(async (_, token) =>
        {
            Interlocked.Increment(ref publishCalls);
            publishing.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }));
        var sut = CreateSut(logger);
        await sut.StartAsync();
        await WaitForSignalAsync(publishing);
        Task first;
        Task second;
        using var firstCallerTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        try
        {
            // The log is provider code that blocks. It must not run on the first caller's stack, so the call returns
            // while the log is held: when its own token ends the wait, and otherwise only when the stop settles.
            first = sut.StopAsync(firstCallerExpires ? firstCallerTimeout.Token : CancellationToken.None);
            await finalLogEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            second = sut.StopAsync();
            await sut.StartAsync();

            (await Task.WhenAny(second, Task.Delay(TimeSpan.FromMilliseconds(200))) == second).Should()
                                                                                              .BeFalse("the first stop is still pending, so an overlapping stop must wait for it");
            (await Task.WhenAny(first, Task.Delay(TimeSpan.FromMilliseconds(500))) == first).Should()
                                                                                            .Be(firstCallerExpires,
                                                                                                "only a caller whose token ended returns while the stop is pending");
            publishCalls.Should().Be(1, "a start while the stop is pending must not admit a second run");
            logger.Entries.Should().Contain(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("is already running"));
        }
        finally
        {
            releaseFinalLog.TrySetResult(true);
        }

        // The stops that waited for the operation observe its one result; a caller that gave up on waiting does not.
        if (firstCallerExpires)
        {
            await first.WaitAsync(TimeSpan.FromSeconds(5));
        }
        else
        {
            await first.Invoking(task => task.WaitAsync(TimeSpan.FromSeconds(5))).Should().ThrowAsync<InvalidOperationException>().WithMessage("logger failed");
        }

        await second.Invoking(task => task.WaitAsync(TimeSpan.FromSeconds(5))).Should().ThrowAsync<InvalidOperationException>().WithMessage("logger failed");
        await sut.StartAsync();
        await sut.StopAsync();
    }

    [Test]
    public async Task StopAsync_OverlappingStopWithCancelledToken_DoesNotDropCallbacksOrThrow()
    {
        var message = CreatePendingMessage();
        var publishing = CreateSignal();
        SetupFindReturns(message);
        SetupClaimReturns(message);
        SetupUpdateOneResult(1);
        var barrier = new CancellationCallbackBarrier();
        UsePublisher(new DelegatePublisher((_, token) =>
        {
            barrier.RegisterOn(token);
            publishing.TrySetResult(true);
            return Task.Delay(Timeout.InfiniteTimeSpan, token);
        }));
        var sut = CreateSut();
        await sut.StartAsync();
        await WaitForSignalAsync(publishing);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        try
        {
            var first = sut.StopAsync();
            await barrier.BlockerEntered.WaitAsync(TimeSpan.FromSeconds(5));
            var second = sut.StopAsync(cancelled.Token);

            await second.WaitAsync(TimeSpan.FromSeconds(5));
            first.IsCompleted.Should().BeFalse("the first stop is held by its callback");
            barrier.Release();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            await barrier.MarkerRan.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            barrier.Release();
        }

        await sut.StartAsync();
        await sut.StopAsync();
    }

    [Test]
    public async Task StopAsync_StartedAgainAfterSynchronouslyCompletedStop_StopsTheNewRun()
    {
        SetupFindReturnsEmpty();
        var sut = CreateSut();

        // A loop that never runs and a source that is already cancelled let the first stop complete synchronously.
        using var cancelledAtStart = new CancellationTokenSource();
        await cancelledAtStart.CancelAsync();
        await sut.StartAsync(cancelledAtStart.Token);
        await sut.StopAsync();
        _collectionMock.Invocations.Clear();
        await sut.StartAsync();
        SpinWait.SpinUntil(() => _collectionMock.Invocations.Count > 0, TimeSpan.FromSeconds(5)).Should().BeTrue("the second run polls");

        await sut.StopAsync();
        _collectionMock.Invocations.Clear();
        _timeProvider.Advance(_options.PollingInterval * 2);
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        _collectionMock.Invocations.Should().BeEmpty("the stop of the second run must stop its loop instead of reusing the first run's completed stop");
    }

    [Test]
    public async Task StopAsync_TimedOutStopWhilePublisherIgnoresCancellation_KeepsTheProcessorStoppingUntilTheLoopEnds()
    {
        var message = CreatePendingMessage();
        var publishing = CreateSignal();
        var releasePublisher = CreateSignal();
        var markerRan = CreateSignal();
        var publishCalls = 0;
        SetupFindReturns(message);
        SetupClaimReturns(message);
        SetupUpdateOneResult(1);
        UsePublisher(new DelegatePublisher(async (_, token) =>
        {
            Interlocked.Increment(ref publishCalls);
            token.Register(() => markerRan.TrySetResult(true));
            publishing.TrySetResult(true);
            await releasePublisher.Task;
        }));
        var sut = CreateSut();
        await sut.StartAsync();
        await WaitForSignalAsync(publishing);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        Task uncancelledStop;

        try
        {
            var timedOutStop = sut.StopAsync(cancelled.Token);
            await timedOutStop.WaitAsync(TimeSpan.FromSeconds(5));
            await markerRan.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await sut.StartAsync();
            uncancelledStop = sut.StopAsync();
            var stillPending = await Task.WhenAny(uncancelledStop, Task.Delay(TimeSpan.FromMilliseconds(200))) != uncancelledStop;

            stillPending.Should().BeTrue("the loop is still running, so the processor is still stopping");
            publishCalls.Should().Be(1, "a start during the stop must not run a second loop beside the old one");
            VerifyLoggedWarning("is already running");
        }
        finally
        {
            releasePublisher.TrySetResult(true);
        }

        await uncancelledStop.WaitAsync(TimeSpan.FromSeconds(5));
        await sut.StartAsync();
        await sut.StopAsync();
    }

    [Test]
    public async Task TryReleaseLock_WhenUpdateThrows_SwallowsExceptionAndLogsWarning()
    {
        var message = CreatePendingMessage();
        var lockReleaseAttempted = CreateSignal();
        SetupFindReturns(message);
        SetupClaimReturns(message);

        using var cts = new CancellationTokenSource();

        _publisherMock
            .Setup(p => p.PublishAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback(cts.Cancel)
            .ThrowsAsync(new OperationCanceledException());

        // Lock release throws
        _collectionMock
            .Setup(c => c.UpdateOneAsync(
                       It.IsAny<FilterDefinition<OutboxMessage>>(),
                       It.IsAny<UpdateDefinition<OutboxMessage>>(),
                       It.IsAny<UpdateOptions>(),
                       It.IsAny<CancellationToken>()))
            .Callback(() => lockReleaseAttempted.TrySetResult(true))
            .ThrowsAsync(new MongoException("Connection lost during lock release"));

        var sut = CreateSut();
        await sut.StartAsync(cts.Token);

        await WaitForSignalAsync(lockReleaseAttempted);

        // The processor should not throw despite the lock release failure
        var cancellationToken = cts.Token;
        var act = () => sut.StopAsync(cancellationToken);
        await act.Should().NotThrowAsync();

        VerifyLoggedWarning("Failed to release lock");
    }

    private static IAsyncCursor<OutboxMessage> CreateCursorWith(params OutboxMessage[] messages)
    {
        var returned = false;
        var cursorMock = new Mock<IAsyncCursor<OutboxMessage>>();
        cursorMock.Setup(c => c.MoveNext(It.IsAny<CancellationToken>()))
                  .Returns(() =>
                  {
                      if (returned) return false;
                      returned = true;
                      return true;
                  });
        cursorMock.Setup(c => c.MoveNextAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(() =>
                  {
                      if (returned) return false;
                      returned = true;
                      return true;
                  });
        cursorMock.Setup(c => c.Current).Returns(messages);
        return cursorMock.Object;
    }

    private static IAsyncCursor<OutboxMessage> CreateEmptyAsyncCursor()
    {
        var cursorMock = new Mock<IAsyncCursor<OutboxMessage>>();
        cursorMock.Setup(c => c.MoveNext(It.IsAny<CancellationToken>())).Returns(false);
        cursorMock.Setup(c => c.MoveNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        cursorMock.Setup(c => c.Current).Returns([]);
        return cursorMock.Object;
    }

    private static IAsyncCursor<OutboxMessage> CreateEmptyCursor() => CreateEmptyAsyncCursor();

    private static OutboxMessage CreatePendingMessage(Int64 attemptCount = 0)
    {
        return new OutboxMessage
        {
            Id = ObjectId.GenerateNewId(),
            State = OutboxMessageState.Pending,
            Type = "TestPayload",
            Payload = new BsonDocument("Name", "Test"),
            AttemptCount = attemptCount,
            IsLocked = false
        };
    }

    private static TaskCompletionSource<Boolean> CreateSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitForSignalAsync(TaskCompletionSource<Boolean> signal)
        => await signal.Task.WaitAsync(TimeSpan.FromSeconds(1));

    private OutboxProcessor CreateSut(ILogger<OutboxProcessor>? logger = null)
    {
        return new OutboxProcessor(
            _mongoHelperMock.Object,
            _options,
            _handCraftedScopeFactory ?? _scopeFactoryMock.Object,
            _timeProvider,
            logger ?? _loggerMock.Object);
    }

    /// <summary>
    /// Replaces the fixture's mocked publisher with a hand-crafted one behind a real scope factory, for tests that need to control the publisher's
    /// reaction to its cancellation token.
    /// </summary>
    private void UsePublisher(IOutboxPublisher publisher)
    {
        var services = new ServiceCollection();
        services.AddSingleton(publisher);
        _handCraftedScopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private void SetupClaimReturns(OutboxMessage? result, Action? callback = null)
    {
        _collectionMock
            .Setup(c => c.FindOneAndUpdateAsync(
                       It.IsAny<FilterDefinition<OutboxMessage>>(),
                       It.IsAny<UpdateDefinition<OutboxMessage>>(),
                       It.IsAny<FindOneAndUpdateOptions<OutboxMessage, OutboxMessage>>(),
                       It.IsAny<CancellationToken>()))
            .Callback<FilterDefinition<OutboxMessage>, UpdateDefinition<OutboxMessage>, FindOneAndUpdateOptions<OutboxMessage, OutboxMessage>,
                CancellationToken>((_, _, _, _) => callback?.Invoke())
            .Returns(() => Task.FromResult(result!));
    }

    private void SetupFindReturns(params OutboxMessage[] messages)
    {
        // The processor uses Find().Sort().Limit().ToListAsync() which goes through
        // IFindFluent. We mock FindAsync which is the underlying call.
        _collectionMock
            .Setup(c => c.FindAsync(
                       It.IsAny<FilterDefinition<OutboxMessage>>(),
                       It.IsAny<FindOptions<OutboxMessage, OutboxMessage>>(),
                       It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateCursorWith(messages));
    }

    private void SetupFindReturnsEmpty()
    {
        _collectionMock
            .Setup(c => c.FindAsync(
                       It.IsAny<FilterDefinition<OutboxMessage>>(),
                       It.IsAny<FindOptions<OutboxMessage, OutboxMessage>>(),
                       It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateEmptyAsyncCursor());
    }

    private void SetupUpdateOneResult(Int64 modifiedCount, Action? callback = null)
    {
        var resultMock = new Mock<UpdateResult>();
        resultMock.Setup(r => r.ModifiedCount).Returns(modifiedCount);

        _collectionMock
            .Setup(c => c.UpdateOneAsync(
                       It.IsAny<FilterDefinition<OutboxMessage>>(),
                       It.IsAny<UpdateDefinition<OutboxMessage>>(),
                       It.IsAny<UpdateOptions>(),
                       It.IsAny<CancellationToken>()))
            .Callback<FilterDefinition<OutboxMessage>, UpdateDefinition<OutboxMessage>, UpdateOptions, CancellationToken>((_, _, _, _) => callback?.Invoke())
            .ReturnsAsync(resultMock.Object);
    }

    private void VerifyLoggedError(String messageSubstring)
    {
        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, _) => o!.ToString()!.Contains(messageSubstring)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, String>>()),
            Times.AtLeastOnce);
    }

    private void VerifyLoggedWarning(String messageSubstring)
    {
        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, _) => o!.ToString()!.Contains(messageSubstring)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, String>>()),
            Times.AtLeastOnce);
    }
}

/// <summary>
/// A disposable that runs an action, for a logging scope whose disposal a test counts.
/// </summary>
public sealed class ActionDisposable : IDisposable
{
    private readonly Action _dispose;

    public ActionDisposable(Action dispose)
    {
        ArgumentNullException.ThrowIfNull(dispose);
        _dispose = dispose;
    }

    public void Dispose() => _dispose();
}

/// <summary>
/// A publisher whose behavior is a delegate, so a test controls how it reacts to the processor's cancellation token.
/// </summary>
public sealed class DelegatePublisher : IOutboxPublisher
{
    private readonly Func<OutboxMessage, CancellationToken, Task> _publish;

    public DelegatePublisher(Func<OutboxMessage, CancellationToken, Task> publish)
    {
        ArgumentNullException.ThrowIfNull(publish);
        _publish = publish;
    }

    public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken = default)
        => _publish(message, cancellationToken);
}
