// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests;

using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using NUnit.Framework;
using System.Net;

public class MongoLockTests
{
    private static readonly TimeSpan DefaultLeaseTime = TimeSpan.FromMinutes(5);

    [Test]
    public void Constructor_WithEmptyId_ShouldThrowArgumentException()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var validUntil = timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime;
        var releaseAction = () => Task.CompletedTask;
        Func<TimeSpan, CancellationToken, Task<DateTime?>> extendAction = (_, _) => Task.FromResult<DateTime?>(validUntil);

        // Act
        var act = () => new MongoLock(String.Empty, validUntil, DefaultLeaseTime, timeProvider, releaseAction, extendAction);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [TestCase(-1)]
    [TestCase(0)]
    [TestCase(0.5)]
    public void Constructor_WithLeaseTimeShorterThanOneMillisecond_ShouldThrowArgumentOutOfRangeException(Double milliseconds)
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var validUntil = timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime;
        var leaseTime = TimeSpan.FromMilliseconds(milliseconds);

        // Act
        var act = () => new MongoLock("test-lock",
                                      validUntil,
                                      leaseTime,
                                      timeProvider,
                                      () => Task.CompletedTask,
                                      (_, _) => Task.FromResult<DateTime?>(validUntil));

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void Constructor_WithNullExtendAction_ShouldThrowArgumentNullException()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var validUntil = timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime;

        // Act
        var act = () => new MongoLock("test-lock",
                                      validUntil,
                                      DefaultLeaseTime,
                                      timeProvider,
                                      () => Task.CompletedTask,
                                      null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Constructor_WithNullId_ShouldThrowArgumentException()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var validUntil = timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime;
        var releaseAction = () => Task.CompletedTask;
        Func<TimeSpan, CancellationToken, Task<DateTime?>> extendAction = (_, _) => Task.FromResult<DateTime?>(validUntil);

        // Act
        var act = () => new MongoLock(null!, validUntil, DefaultLeaseTime, timeProvider, releaseAction, extendAction);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void Constructor_WithNullReleaseAction_ShouldThrowArgumentNullException()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var lockId = "test-lock";
        var validUntil = timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime;

        // Act
        var act = () => new MongoLock(lockId,
                                      validUntil,
                                      DefaultLeaseTime,
                                      timeProvider,
                                      null!,
                                      (_, _) => Task.FromResult<DateTime?>(validUntil));

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Constructor_WithValidParameters_ShouldInitializeProperties()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var lockId = "test-lock";
        var validUntil = timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime;
        var releaseAction = () => Task.CompletedTask;

        // Act
        var mongoLock = CreateMongoLock(lockId, validUntil, timeProvider, releaseAction);

        // Assert
        mongoLock.Id.Should().Be(lockId);
        mongoLock.ValidUntilUtc.Should().Be(validUntil);
    }

    [Test]
    public void Constructor_WithWhitespaceId_ShouldThrowArgumentException()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var validUntil = timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime;
        var releaseAction = () => Task.CompletedTask;
        Func<TimeSpan, CancellationToken, Task<DateTime?>> extendAction = (_, _) => Task.FromResult<DateTime?>(validUntil);

        // Act
        var act = () => new MongoLock("   ", validUntil, DefaultLeaseTime, timeProvider, releaseAction, extendAction);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public async Task DisposeAsync_ShouldInvokeReleaseAction()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var releaseInvoked = false;
        var releaseAction = () =>
        {
            releaseInvoked = true;
            return Task.CompletedTask;
        };
        var mongoLock = CreateMongoLock("test-lock", timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime, timeProvider, releaseAction);

        // Act
        await mongoLock.DisposeAsync();

        // Assert
        releaseInvoked.Should().BeTrue();
    }

    [Test]
    public async Task DisposeAsync_WhenCalledMultipleTimes_ShouldInvokeReleaseActionOnlyOnce()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var releaseCount = 0;
        var releaseAction = () =>
        {
            releaseCount++;
            return Task.CompletedTask;
        };
        var mongoLock = CreateMongoLock("test-lock", timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime, timeProvider, releaseAction);

        // Act
        await mongoLock.DisposeAsync();
        await mongoLock.DisposeAsync();
        await mongoLock.DisposeAsync();

        // Assert
        releaseCount.Should().Be(1);
    }

    [Test]
    public async Task DisposeAsync_WhenExtensionIsInFlight_ShouldWaitForExtensionBeforeReleasing()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var originalExpiry = timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime;
        var extendedExpiry = timeProvider.GetUtcNow().AddMinutes(10).UtcDateTime;
        var extensionStarted = new TaskCompletionSource<Boolean>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completeExtension = new TaskCompletionSource<DateTime?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInvoked = false;
        var mongoLock = CreateMongoLock(
            "test-lock",
            originalExpiry,
            timeProvider,
            () =>
            {
                releaseInvoked = true;
                return Task.CompletedTask;
            },
            async (_, _) =>
            {
                extensionStarted.SetResult(true);
                return await completeExtension.Task;
            });

        var extensionTask = mongoLock.TryExtendAsync();
        await extensionStarted.Task;

        // Act
        var disposalTask = mongoLock.DisposeAsync().AsTask();
        releaseInvoked.Should().BeFalse();
        completeExtension.SetResult(extendedExpiry);

        // Assert
        (await extensionTask).Should().BeTrue();
        await disposalTask;
        releaseInvoked.Should().BeTrue();
        mongoLock.ValidUntilUtc.Should().Be(extendedExpiry);
        mongoLock.IsValid.Should().BeFalse();
    }

    [Test]
    public async Task DisposeAsync_WhenReleaseActionThrows_ShouldSuppressException()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        Func<Task> releaseAction = () => throw new InvalidOperationException("Release failed");
        var mongoLock = CreateMongoLock("test-lock", timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime, timeProvider, releaseAction);

        // Act
        var act = async () => await mongoLock.DisposeAsync();

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Test]
    public async Task DisposeAsync_WithAsyncReleaseAction_ShouldAwaitCompletion()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var releaseCompleted = false;
        var releaseAction = () =>
        {
            releaseCompleted = true;
            return Task.CompletedTask;
        };
        var mongoLock = CreateMongoLock("test-lock", timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime, timeProvider, releaseAction);

        // Act
        await mongoLock.DisposeAsync();

        // Assert
        releaseCompleted.Should().BeTrue();
    }

    [Test]
    public async Task IsValid_AfterDisposal_ShouldReturnFalse()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var mongoLock = CreateMongoLock("test-lock", timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime, timeProvider);
        mongoLock.IsValid.Should().BeTrue();

        // Act
        await mongoLock.DisposeAsync();

        // Assert
        mongoLock.IsValid.Should().BeFalse();
    }

    [Test]
    public async Task IsValid_AfterMultipleDisposals_ShouldRemainFalse()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var mongoLock = CreateMongoLock("test-lock", timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime, timeProvider);

        // Act
        await mongoLock.DisposeAsync();
        await mongoLock.DisposeAsync();
        await mongoLock.DisposeAsync();

        // Assert
        mongoLock.IsValid.Should().BeFalse();
    }

    [Test]
    public void IsValid_WhenExpiringDuringTest_ShouldTransitionToFalse()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var mongoLock = CreateMongoLock("test-lock", timeProvider.GetUtcNow().AddMilliseconds(100).UtcDateTime, timeProvider);
        mongoLock.IsValid.Should().BeTrue();

        // Act - Wait for expiration
        timeProvider.Advance(TimeSpan.FromMilliseconds(150));

        // Assert
        mongoLock.IsValid.Should().BeFalse();
    }

    [Test]
    public void IsValid_WhenLockHasExpired_ShouldReturnFalse()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var mongoLock = CreateMongoLock("test-lock", timeProvider.GetUtcNow().AddMilliseconds(-100).UtcDateTime, timeProvider);

        // Act & Assert
        mongoLock.IsValid.Should().BeFalse();
    }

    [Test]
    public void IsValid_WhenLockIsNotExpiredAndNotDisposed_ShouldReturnTrue()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var mongoLock = CreateMongoLock("test-lock", timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime, timeProvider);

        // Act & Assert
        mongoLock.IsValid.Should().BeTrue();
    }

    [Test]
    public async Task TryExtendAsync_WhenCalledMultipleTimes_ShouldUpdateExpiryFromDelegateResultEachTime()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var originalExpiry = timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime;
        var firstExtendedExpiry = timeProvider.GetUtcNow().AddMinutes(10).UtcDateTime;
        var secondExtendedExpiry = timeProvider.GetUtcNow().AddMinutes(15).UtcDateTime;
        var invocationCount = 0;
        var mongoLock = CreateMongoLock(
            "test-lock",
            originalExpiry,
            timeProvider,
            extendAction: (_, _) =>
            {
                invocationCount++;
                return Task.FromResult<DateTime?>(invocationCount == 1 ? firstExtendedExpiry : secondExtendedExpiry);
            });

        // Act
        var firstResult = await mongoLock.TryExtendAsync();
        var secondResult = await mongoLock.TryExtendAsync();

        // Assert
        firstResult.Should().BeTrue();
        secondResult.Should().BeTrue();
        invocationCount.Should().Be(2);
        mongoLock.ValidUntilUtc.Should().Be(secondExtendedExpiry);
    }

    [Test]
    public async Task TryExtendAsync_WhenCancelledBeforeInvocation_ShouldLeaveStateUnchanged()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var originalExpiry = timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime;
        var extendInvoked = false;
        var mongoLock = CreateMongoLock(
            "test-lock",
            originalExpiry,
            timeProvider,
            extendAction: (_, _) =>
            {
                extendInvoked = true;
                return Task.FromResult<DateTime?>(originalExpiry);
            });
        using var cancellationTokenSource = new CancellationTokenSource();
        await cancellationTokenSource.CancelAsync();

        // Act
        var cancellationToken = cancellationTokenSource.Token;
        var act = async () => await mongoLock.TryExtendAsync(cancellationToken: cancellationToken);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        extendInvoked.Should().BeFalse();
        mongoLock.ValidUntilUtc.Should().Be(originalExpiry);
        mongoLock.IsValid.Should().BeTrue();
    }

    [Test]
    public async Task TryExtendAsync_WhenDelegateRefuses_ShouldMarkLockAsLost()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var originalExpiry = timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime;
        var mongoLock = CreateMongoLock(
            "test-lock",
            originalExpiry,
            timeProvider,
            extendAction: (_, _) => Task.FromResult<DateTime?>(null));

        // Act
        var result = await mongoLock.TryExtendAsync();

        // Assert
        result.Should().BeFalse();
        mongoLock.ValidUntilUtc.Should().Be(originalExpiry);
        mongoLock.IsValid.Should().BeFalse();
        var ensureValid = () => mongoLock.EnsureValid();
        ensureValid.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public async Task TryExtendAsync_WhenDisposed_ShouldRefuseWithoutInvokingDelegate()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var extendInvoked = false;
        var mongoLock = CreateMongoLock(
            "test-lock",
            timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime,
            timeProvider,
            extendAction: (_, _) =>
            {
                extendInvoked = true;
                return Task.FromResult<DateTime?>(timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime);
            });
        await mongoLock.DisposeAsync();

        // Act
        var result = await mongoLock.TryExtendAsync();

        // Assert
        result.Should().BeFalse();
        extendInvoked.Should().BeFalse();
        mongoLock.IsValid.Should().BeFalse();
    }

    [Test]
    public async Task TryExtendAsync_WhenLeaseExpired_ShouldMarkLockAsLostAfterDelegateRefuses()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var mongoLock = CreateMongoLock(
            "test-lock",
            timeProvider.GetUtcNow().AddMilliseconds(-1).UtcDateTime,
            timeProvider,
            extendAction: (_, _) => Task.FromResult<DateTime?>(null));

        // Act
        var result = await mongoLock.TryExtendAsync();

        // Assert
        result.Should().BeFalse();
        mongoLock.IsValid.Should().BeFalse();
    }

    [Test]
    public async Task TryExtendAsync_WhenMongoDbOperationThrows_ShouldLeaveStateUnchanged()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var originalExpiry = timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime;
        var expectedException = new MongoException("Extension failed");
        var mongoLock = CreateMongoLock(
            "test-lock",
            originalExpiry,
            timeProvider,
            extendAction: (_, _) => Task.FromException<DateTime?>(expectedException));

        // Act
        var act = async () => await mongoLock.TryExtendAsync();

        // Assert
        var exception = await act.Should().ThrowAsync<MongoException>();
        exception.Which.Should().BeSameAs(expectedException);
        mongoLock.ValidUntilUtc.Should().Be(originalExpiry);
        mongoLock.IsValid.Should().BeTrue();
    }

    [TestCase(-1)]
    [TestCase(0)]
    [TestCase(0.5)]
    public async Task TryExtendAsync_WithLeaseTimeShorterThanOneMillisecond_ShouldThrowBeforeInvokingDelegate(Double milliseconds)
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var extendInvoked = false;
        var mongoLock = CreateMongoLock(
            "test-lock",
            timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime,
            timeProvider,
            extendAction: (_, _) =>
            {
                extendInvoked = true;
                return Task.FromResult<DateTime?>(timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime);
            });

        // Act
        var act = async () => await mongoLock.TryExtendAsync(TimeSpan.FromMilliseconds(milliseconds));

        // Assert
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        extendInvoked.Should().BeFalse();
        mongoLock.IsValid.Should().BeTrue();
    }

    [Test]
    public async Task TryExtendAsync_WithoutLeaseTime_ShouldUseOriginalLeaseAndUpdateExpiry()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var originalExpiry = timeProvider.GetUtcNow().AddMinutes(1).UtcDateTime;
        var extendedExpiry = timeProvider.GetUtcNow().Add(DefaultLeaseTime).UtcDateTime;
        TimeSpan? requestedLeaseTime = null;
        var mongoLock = CreateMongoLock(
            "test-lock",
            originalExpiry,
            timeProvider,
            extendAction: (leaseTime, _) =>
            {
                requestedLeaseTime = leaseTime;
                return Task.FromResult<DateTime?>(extendedExpiry);
            });

        // Act
        var result = await mongoLock.TryExtendAsync();

        // Assert
        result.Should().BeTrue();
        requestedLeaseTime.Should().Be(DefaultLeaseTime);
        mongoLock.ValidUntilUtc.Should().Be(extendedExpiry);
        mongoLock.IsValid.Should().BeTrue();
    }

    [Test]
    public async Task TryEnsureHeldAsync_WhenDelegateConfirms_ShouldPassSessionAndKeepLockValid()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var validUntil = timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime;
        var session = new FakeClientSessionHandle();
        IClientSessionHandle? receivedSession = null;
        var mongoLock = CreateMongoLock("test-lock",
                                        validUntil,
                                        timeProvider,
                                        fenceAction: (fenceSession, _) =>
                                        {
                                            receivedSession = fenceSession;
                                            return Task.FromResult(true);
                                        });

        // Act
        var result = await mongoLock.TryEnsureHeldAsync(session);

        // Assert
        result.Should().BeTrue();
        receivedSession.Should().BeSameAs(session);
        mongoLock.IsValid.Should().BeTrue();
    }

    [Test]
    public async Task TryEnsureHeldAsync_WhenDelegateRefuses_ShouldMarkLockAsLost()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var validUntil = timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime;
        var extendInvoked = false;
        var mongoLock = CreateMongoLock("test-lock",
                                        validUntil,
                                        timeProvider,
                                        extendAction: (_, _) =>
                                        {
                                            extendInvoked = true;
                                            return Task.FromResult<DateTime?>(validUntil);
                                        },
                                        fenceAction: (_, _) => Task.FromResult(false));

        // Act
        var result = await mongoLock.TryEnsureHeldAsync(new FakeClientSessionHandle());
        var extended = await mongoLock.TryExtendAsync();

        // Assert
        result.Should().BeFalse();
        mongoLock.IsValid.Should().BeFalse("the lease has not expired, but the lock is known to be lost");
        extended.Should().BeFalse();
        extendInvoked.Should().BeFalse();
    }

    [Test]
    public async Task TryEnsureHeldAsync_WhenDelegateThrowsWriteConflict_ShouldReportLostAndMarkLockAsLost()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var validUntil = timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime;
        var mongoLock = CreateMongoLock("test-lock",
                                        validUntil,
                                        timeProvider,
                                        fenceAction: (_, _) => throw CreateCommandException(112, "TransientTransactionError"));

        // Act
        var result = await mongoLock.TryEnsureHeldAsync(new FakeClientSessionHandle());

        // Assert
        result.Should().BeFalse();
        mongoLock.IsValid.Should().BeFalse();
    }

    [Test]
    public async Task TryEnsureHeldAsync_WhenDelegateThrowsOtherTransientError_ShouldPropagateAndLeaveStateUnchanged()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var validUntil = timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime;
        var failure = CreateCommandException(189, "TransientTransactionError");
        var mongoLock = CreateMongoLock("test-lock", validUntil, timeProvider, fenceAction: (_, _) => throw failure);

        // Act
        var act = async () => await mongoLock.TryEnsureHeldAsync(new FakeClientSessionHandle());

        // Assert
        (await act.Should().ThrowAsync<MongoCommandException>()).Which.Should().BeSameAs(failure);
        mongoLock.IsValid.Should().BeTrue("a primary step-down says nothing about who owns the lock");
    }

    [Test]
    public async Task TryEnsureHeldAsync_WhenDisposed_ShouldRefuseWithoutInvokingDelegate()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var validUntil = timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime;
        var fenceInvoked = false;
        var mongoLock = CreateMongoLock("test-lock",
                                        validUntil,
                                        timeProvider,
                                        fenceAction: (_, _) =>
                                        {
                                            fenceInvoked = true;
                                            return Task.FromResult(true);
                                        });
        await mongoLock.DisposeAsync();

        // Act
        var result = await mongoLock.TryEnsureHeldAsync(new FakeClientSessionHandle());

        // Assert
        result.Should().BeFalse();
        fenceInvoked.Should().BeFalse();
    }

    [Test]
    public async Task TryEnsureHeldAsync_WhenAlreadyLost_ShouldRefuseWithoutInvokingDelegate()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var validUntil = timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime;
        var fenceInvoked = false;
        var mongoLock = CreateMongoLock("test-lock",
                                        validUntil,
                                        timeProvider,
                                        extendAction: (_, _) => Task.FromResult<DateTime?>(null),
                                        fenceAction: (_, _) =>
                                        {
                                            fenceInvoked = true;
                                            return Task.FromResult(true);
                                        });
        await mongoLock.TryExtendAsync();

        // Act
        var result = await mongoLock.TryEnsureHeldAsync(new FakeClientSessionHandle());

        // Assert
        result.Should().BeFalse();
        fenceInvoked.Should().BeFalse();
    }

    [Test]
    public async Task TryEnsureHeldAsync_WithoutDelegate_ShouldConfirmWithoutServerOperation()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var mongoLock = CreateMongoLock("test-lock", timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime, timeProvider);

        // Act
        var result = await mongoLock.TryEnsureHeldAsync(new FakeClientSessionHandle());

        // Assert
        result.Should().BeTrue();
    }

    [Test]
    public async Task TryEnsureHeldAsync_WhenExtensionIsInFlight_ShouldWaitForExtensionBeforeWriting()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var originalExpiry = timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime;
        var extendedExpiry = timeProvider.GetUtcNow().AddMinutes(10).UtcDateTime;
        var extensionStarted = new TaskCompletionSource<Boolean>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completeExtension = new TaskCompletionSource<DateTime?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fenceInvoked = false;
        var mongoLock = CreateMongoLock("test-lock",
                                        originalExpiry,
                                        timeProvider,
                                        extendAction: async (_, _) =>
                                        {
                                            extensionStarted.SetResult(true);
                                            return await completeExtension.Task;
                                        },
                                        fenceAction: (_, _) =>
                                        {
                                            fenceInvoked = true;
                                            return Task.FromResult(true);
                                        });
        var extensionTask = mongoLock.TryExtendAsync();
        await extensionStarted.Task;

        // Act
        var fenceTask = mongoLock.TryEnsureHeldAsync(new FakeClientSessionHandle());
        await Task.Delay(50);
        var fenceInvokedWhileExtending = fenceInvoked;
        completeExtension.SetResult(extendedExpiry);

        // Assert
        fenceInvokedWhileExtending.Should().BeFalse("the write must not interleave with an extension of the same lock");
        (await extensionTask).Should().BeTrue();
        (await fenceTask.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
        fenceInvoked.Should().BeTrue();
    }

    [Test]
    public async Task TryExtendAsync_WhenWriteIsInFlight_ShouldWaitForWriteBeforeExtending()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var originalExpiry = timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime;
        var fenceStarted = new TaskCompletionSource<Boolean>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completeFence = new TaskCompletionSource<Boolean>(TaskCreationOptions.RunContinuationsAsynchronously);
        var extendInvoked = false;
        var mongoLock = CreateMongoLock("test-lock",
                                        originalExpiry,
                                        timeProvider,
                                        extendAction: (duration, _) =>
                                        {
                                            extendInvoked = true;
                                            return Task.FromResult<DateTime?>(timeProvider.GetUtcNow().Add(duration).UtcDateTime);
                                        },
                                        fenceAction: async (_, _) =>
                                        {
                                            fenceStarted.SetResult(true);
                                            return await completeFence.Task;
                                        });
        var fenceTask = mongoLock.TryEnsureHeldAsync(new FakeClientSessionHandle());
        await fenceStarted.Task;

        // Act
        var extensionTask = mongoLock.TryExtendAsync();
        await Task.Delay(50);
        var extendInvokedWhileWriting = extendInvoked;
        completeFence.SetResult(true);

        // Assert
        extendInvokedWhileWriting.Should().BeFalse();
        (await fenceTask).Should().BeTrue();
        (await extensionTask.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
    }

    [Test]
    public async Task TryEnsureHeldAsync_WhenCancelledWhileWaiting_ShouldThrowWithoutInvokingDelegate()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var extensionStarted = new TaskCompletionSource<Boolean>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completeExtension = new TaskCompletionSource<DateTime?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fenceInvoked = false;
        var mongoLock = CreateMongoLock("test-lock",
                                        timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime,
                                        timeProvider,
                                        extendAction: async (_, _) =>
                                        {
                                            extensionStarted.SetResult(true);
                                            return await completeExtension.Task;
                                        },
                                        fenceAction: (_, _) =>
                                        {
                                            fenceInvoked = true;
                                            return Task.FromResult(true);
                                        });
        var extensionTask = mongoLock.TryExtendAsync();
        await extensionStarted.Task;
        using var cts = new CancellationTokenSource();

        // Act
        var fenceTask = mongoLock.TryEnsureHeldAsync(new FakeClientSessionHandle(), cts.Token);
        await cts.CancelAsync();
        var act = async () => await fenceTask.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        fenceInvoked.Should().BeFalse();
        completeExtension.SetResult(timeProvider.GetUtcNow().AddMinutes(10).UtcDateTime);
        await extensionTask;
    }

    [Test]
    public async Task TryEnsureHeldAsync_WithNullSession_ShouldThrowArgumentNullException()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var mongoLock = CreateMongoLock("test-lock", timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime, timeProvider);

        // Act
        var act = async () => await mongoLock.TryEnsureHeldAsync(null!);

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    private static MongoCommandException CreateCommandException(Int32 code, String errorLabel)
    {
        var exception = new MongoCommandException(new ConnectionId(new ServerId(new ClusterId(), new DnsEndPoint("localhost", 27017))),
                                                  "Command failed",
                                                  new BsonDocument("update", "lock"),
                                                  new BsonDocument
                                                  {
                                                      { "ok", 0 },
                                                      { "code", code }
                                                  });
        exception.AddErrorLabel(errorLabel);
        return exception;
    }

    private static MongoLock CreateMongoLock(String id,
                                             DateTime validUntilUtc,
                                             TimeProvider timeProvider,
                                             Func<Task>? releaseAction = null,
                                             Func<TimeSpan, CancellationToken, Task<DateTime?>>? extendAction = null,
                                             TimeSpan? leaseTime = null,
                                             Func<IClientSessionHandle, CancellationToken, Task<Boolean>>? fenceAction = null)
    {
        return new MongoLock(id,
                             validUntilUtc,
                             leaseTime ?? DefaultLeaseTime,
                             timeProvider,
                             releaseAction ?? (() => Task.CompletedTask),
                             extendAction ?? ((duration, _) => Task.FromResult<DateTime?>(timeProvider.GetUtcNow().Add(duration).UtcDateTime)),
                             fenceAction);
    }
}
