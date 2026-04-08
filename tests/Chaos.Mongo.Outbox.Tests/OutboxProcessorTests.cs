// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox.Tests;

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
    private Mock<ILogger<OutboxProcessor>> _loggerMock;
    private Mock<IMongoHelper> _mongoHelperMock;
    private OutboxOptions _options;
    private Mock<IOutboxPublisher> _publisherMock;
    private Mock<IServiceScopeFactory> _scopeFactoryMock;
    private FakeTimeProvider _timeProvider;

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
                    throw new MongoException("Transient error");

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
                    throw new MongoException("Transient error");

                retryObserved.TrySetResult(true);

                return Task.FromResult(CreateEmptyAsyncCursor());
            });

        var sut = CreateSut();
        await sut.StartAsync();

        // Advance time past the 5-second retry delay
        _timeProvider.Advance(TimeSpan.FromSeconds(6));
        await WaitForSignalAsync(retryObserved);
        await sut.StopAsync();

        // The processor should have survived the exception (no unhandled throw)
        VerifyLoggedError("Transient MongoDB error");
    }

    [Test]
    public async Task ProcessLoop_UnexpectedException_ContinuesAfterDelay()
    {
        var callCount = 0;
        var retryObserved = CreateSignal();

        _collectionMock
            .Setup(c => c.FindSync(
                       It.IsAny<FilterDefinition<OutboxMessage>>(),
                       It.IsAny<FindOptions<OutboxMessage, OutboxMessage>>(),
                       It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                if (callCount++ == 0)
                    throw new InvalidOperationException("Something unexpected");

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
                    throw new InvalidOperationException("Something unexpected");

                retryObserved.TrySetResult(true);

                return Task.FromResult(CreateEmptyAsyncCursor());
            });

        var sut = CreateSut();
        await sut.StartAsync();

        _timeProvider.Advance(TimeSpan.FromSeconds(6));
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
    public async Task ProcessMessage_PublishFails_MarksAsPermanentlyFailedWhenRetriesExhausted()
    {
        var message = CreatePendingMessage(_options.MaxRetries - 1);
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
        _options = new()
        {
            CollectionName = "TestOutbox",
            BatchSize = 10,
            MaxRetries = 40,
            PollingInterval = TimeSpan.FromMilliseconds(50),
            LockTimeout = TimeSpan.FromMinutes(5),
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

    [SetUp]
    public void SetUp()
    {
        _options = new()
        {
            CollectionName = "TestOutbox",
            BatchSize = 10,
            MaxRetries = 3,
            PollingInterval = TimeSpan.FromMilliseconds(50),
            LockTimeout = TimeSpan.FromMinutes(5),
            RetryBackoffInitialDelay = TimeSpan.FromSeconds(1),
            RetryBackoffMaxDelay = TimeSpan.FromSeconds(30)
        };

        _timeProvider = new(DateTimeOffset.UtcNow);
        _loggerMock = new();

        _collectionMock = new();
        _databaseMock = new();
        _databaseMock
            .Setup(d => d.GetCollection<OutboxMessage>(_options.CollectionName, null))
            .Returns(_collectionMock.Object);

        _mongoHelperMock = new();
        _mongoHelperMock.Setup(h => h.Database).Returns(_databaseMock.Object);

        _publisherMock = new();

        var serviceScopeMock = new Mock<IServiceScope>();
        var services = new ServiceCollection();
        services.AddSingleton(_publisherMock.Object);
        serviceScopeMock.Setup(s => s.ServiceProvider).Returns(services.BuildServiceProvider());

        _scopeFactoryMock = new();
        _scopeFactoryMock.Setup(f => f.CreateScope()).Returns(serviceScopeMock.Object);
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
        var act = () => sut.StopAsync(cts.Token);
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

    private static OutboxMessage CreatePendingMessage(Int32 retryCount = 0)
    {
        return new()
        {
            Id = ObjectId.GenerateNewId(),
            State = OutboxMessageState.Pending,
            Type = "TestPayload",
            Payload = new("Name", "Test"),
            RetryCount = retryCount,
            IsLocked = false
        };
    }

    private static TaskCompletionSource<Boolean> CreateSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitForSignalAsync(TaskCompletionSource<Boolean> signal)
        => await signal.Task.WaitAsync(TimeSpan.FromSeconds(1));

    private OutboxProcessor CreateSut()
    {
        return new(
            _mongoHelperMock.Object,
            _options,
            _scopeFactoryMock.Object,
            _timeProvider,
            _loggerMock.Object);
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
                It.Is<It.IsAnyType>((o, _) => o.ToString()!.Contains(messageSubstring)),
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
                It.Is<It.IsAnyType>((o, _) => o.ToString()!.Contains(messageSubstring)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, String>>()),
            Times.AtLeastOnce);
    }
}
