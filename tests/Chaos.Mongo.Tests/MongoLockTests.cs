// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests;

using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Driver;
using NUnit.Framework;

public class MongoLockTests
{
    private static readonly TimeSpan DefaultLeaseTime = TimeSpan.FromMinutes(5);

    [Test]
    public void Constructor_WithEmptyId_ShouldThrowArgumentException()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var validUntil = timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime;
        Func<DateTime, Task> releaseAction = _ => Task.CompletedTask;
        Func<DateTime, TimeSpan, CancellationToken, Task<DateTime?>> extendAction = (_, _, _) => Task.FromResult<DateTime?>(validUntil);

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
                                      _ => Task.CompletedTask,
                                      (_, _, _) => Task.FromResult<DateTime?>(validUntil));

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
                                      _ => Task.CompletedTask,
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
        Func<DateTime, Task> releaseAction = _ => Task.CompletedTask;
        Func<DateTime, TimeSpan, CancellationToken, Task<DateTime?>> extendAction = (_, _, _) => Task.FromResult<DateTime?>(validUntil);

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
                                      (_, _, _) => Task.FromResult<DateTime?>(validUntil));

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
        Func<DateTime, Task> releaseAction = _ => Task.CompletedTask;

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
        Func<DateTime, Task> releaseAction = _ => Task.CompletedTask;
        Func<DateTime, TimeSpan, CancellationToken, Task<DateTime?>> extendAction = (_, _, _) => Task.FromResult<DateTime?>(validUntil);

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
        Func<DateTime, Task> releaseAction = _ =>
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
        Func<DateTime, Task> releaseAction = _ =>
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
    public async Task DisposeAsync_WhenExtensionIsInFlight_ShouldReleaseExtendedExpiryWithoutResurrectingLock()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var originalExpiry = timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime;
        var extendedExpiry = timeProvider.GetUtcNow().AddMinutes(10).UtcDateTime;
        var extensionStarted = new TaskCompletionSource<Boolean>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completeExtension = new TaskCompletionSource<DateTime?>(TaskCreationOptions.RunContinuationsAsynchronously);
        DateTime? releasedExpiry = null;
        var mongoLock = CreateMongoLock(
            "test-lock",
            originalExpiry,
            timeProvider,
            validUntilUtc =>
            {
                releasedExpiry = validUntilUtc;
                return Task.CompletedTask;
            },
            async (_, _, _) =>
            {
                extensionStarted.SetResult(true);
                return await completeExtension.Task;
            });

        var extensionTask = mongoLock.TryExtendAsync();
        await extensionStarted.Task;

        // Act
        var disposalTask = mongoLock.DisposeAsync().AsTask();
        completeExtension.SetResult(extendedExpiry);

        // Assert
        (await extensionTask).Should().BeTrue();
        await disposalTask;
        releasedExpiry.Should().Be(extendedExpiry);
        mongoLock.ValidUntilUtc.Should().Be(extendedExpiry);
        mongoLock.IsValid.Should().BeFalse();
    }

    [Test]
    public async Task DisposeAsync_WhenReleaseActionThrows_ShouldSuppressException()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        Func<DateTime, Task> releaseAction = _ => throw new InvalidOperationException("Release failed");
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
        Func<DateTime, Task> releaseAction = _ =>
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
    public async Task TryExtendAsync_AfterSuccessfulExtension_ShouldPassUpdatedExpiryToDelegate()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var originalExpiry = timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime;
        var firstExtendedExpiry = timeProvider.GetUtcNow().AddMinutes(10).UtcDateTime;
        var secondExtendedExpiry = timeProvider.GetUtcNow().AddMinutes(15).UtcDateTime;
        var expectedExpiries = new List<DateTime>();
        var mongoLock = CreateMongoLock(
            "test-lock",
            originalExpiry,
            timeProvider,
            extendAction: (validUntilUtc, _, _) =>
            {
                expectedExpiries.Add(validUntilUtc);
                return Task.FromResult<DateTime?>(expectedExpiries.Count == 1 ? firstExtendedExpiry : secondExtendedExpiry);
            });

        // Act
        var firstResult = await mongoLock.TryExtendAsync();
        var secondResult = await mongoLock.TryExtendAsync();

        // Assert
        firstResult.Should().BeTrue();
        secondResult.Should().BeTrue();
        expectedExpiries.Should().Equal(originalExpiry, firstExtendedExpiry);
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
            extendAction: (_, _, _) =>
            {
                extendInvoked = true;
                return Task.FromResult<DateTime?>(originalExpiry);
            });
        using var cancellationTokenSource = new CancellationTokenSource();
        await cancellationTokenSource.CancelAsync();

        // Act
        var act = async () => await mongoLock.TryExtendAsync(cancellationToken: cancellationTokenSource.Token);

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
            extendAction: (_, _, _) => Task.FromResult<DateTime?>(null));

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
            extendAction: (_, _, _) =>
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
            extendAction: (_, _, _) => Task.FromResult<DateTime?>(null));

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
            extendAction: (_, _, _) => Task.FromException<DateTime?>(expectedException));

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
            extendAction: (_, _, _) =>
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
        DateTime? expectedExpiry = null;
        TimeSpan? requestedLeaseTime = null;
        var mongoLock = CreateMongoLock(
            "test-lock",
            originalExpiry,
            timeProvider,
            extendAction: (validUntilUtc, leaseTime, _) =>
            {
                expectedExpiry = validUntilUtc;
                requestedLeaseTime = leaseTime;
                return Task.FromResult<DateTime?>(extendedExpiry);
            });

        // Act
        var result = await mongoLock.TryExtendAsync();

        // Assert
        result.Should().BeTrue();
        expectedExpiry.Should().Be(originalExpiry);
        requestedLeaseTime.Should().Be(DefaultLeaseTime);
        mongoLock.ValidUntilUtc.Should().Be(extendedExpiry);
        mongoLock.IsValid.Should().BeTrue();
    }

    private static MongoLock CreateMongoLock(String id,
                                             DateTime validUntilUtc,
                                             TimeProvider timeProvider,
                                             Func<DateTime, Task>? releaseAction = null,
                                             Func<DateTime, TimeSpan, CancellationToken, Task<DateTime?>>? extendAction = null,
                                             TimeSpan? leaseTime = null)
    {
        return new MongoLock(id,
                             validUntilUtc,
                             leaseTime ?? DefaultLeaseTime,
                             timeProvider,
                             releaseAction ?? (_ => Task.CompletedTask),
                             extendAction ?? ((_, duration, _) => Task.FromResult<DateTime?>(timeProvider.GetUtcNow().Add(duration).UtcDateTime)));
    }
}
