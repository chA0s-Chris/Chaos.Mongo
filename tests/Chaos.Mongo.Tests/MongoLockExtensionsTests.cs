// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests;

using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Driver;
using NUnit.Framework;

public class MongoLockExtensionsTests
{
    private static readonly TimeSpan DefaultLeaseTime = TimeSpan.FromMinutes(5);

    [Test]
    public async Task EnsureValid_AfterDisposal_ShouldThrowInvalidOperationException()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var mongoLock = CreateMongoLock("disposed-lock", timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime, timeProvider);
        await mongoLock.DisposeAsync();

        // Act
        var act = () => mongoLock.EnsureValid();

        // Assert
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*disposed-lock*");
    }

    [Test]
    public void EnsureValid_BeforeAndAfterExpiration_ShouldTransitionBehavior()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var mongoLock = CreateMongoLock("transition-lock", timeProvider.GetUtcNow().AddMilliseconds(100).UtcDateTime, timeProvider);

        // Act & Assert - Valid before expiration
        mongoLock.EnsureValid().Should().BeSameAs(mongoLock);

        // Wait for expiration
        timeProvider.Advance(TimeSpan.FromMilliseconds(150));

        // Act & Assert - Invalid after expiration
        var act = () => mongoLock.EnsureValid();
        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void EnsureValid_MultipleLocksWithDifferentStates_ShouldValidateIndependently()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var validLock = CreateMongoLock("valid-lock", timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime, timeProvider);
        var expiredLock = CreateMongoLock("expired-lock", timeProvider.GetUtcNow().AddMilliseconds(-100).UtcDateTime, timeProvider);

        // Act & Assert
        validLock.EnsureValid().Should().BeSameAs(validLock);
        var act = () => expiredLock.EnsureValid();
        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void EnsureValid_WithExpiredLock_ShouldThrowInvalidOperationException()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var mongoLock = CreateMongoLock("expired-lock", timeProvider.GetUtcNow().AddMilliseconds(-100).UtcDateTime, timeProvider);

        // Act
        var act = () => mongoLock.EnsureValid();

        // Assert
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*expired-lock*");
    }

    [Test]
    public void EnsureValid_WithFluentStyle_ShouldAllowChaining()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var mongoLock = CreateMongoLock("fluent-lock", timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime, timeProvider);

        // Act
        var result = mongoLock.EnsureValid().EnsureValid().EnsureValid();

        // Assert
        result.Should().BeSameAs(mongoLock);
        result.IsValid.Should().BeTrue();
    }

    [Test]
    public void EnsureValid_WithNullLock_ShouldThrowArgumentNullException()
    {
        // Arrange
        IMongoLock? mongoLock = null;

        // Act
        var act = () => mongoLock!.EnsureValid();

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void EnsureValid_WithValidLock_ShouldReturnLock()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var mongoLock = CreateMongoLock("test-lock", timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime, timeProvider);

        // Act
        var result = mongoLock.EnsureValid();

        // Assert
        result.Should().BeSameAs(mongoLock);
    }

    [Test]
    public async Task ExtendAsync_WhenCancelled_ShouldPropagateCancellation()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var mongoLock = CreateMongoLock("test-lock", timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime, timeProvider);
        using var cancellationTokenSource = new CancellationTokenSource();
        await cancellationTokenSource.CancelAsync();

        // Act
        var cancellationToken = cancellationTokenSource.Token;
        var act = async () => await mongoLock.ExtendAsync(cancellationToken: cancellationToken);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        mongoLock.IsValid.Should().BeTrue();
    }

    [Test]
    public async Task ExtendAsync_WhenExtensionIsRefused_ShouldThrowInvalidOperationException()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var mongoLock = CreateMongoLock(
            "refused-lock",
            timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime,
            timeProvider,
            (_, _, _) => Task.FromResult<DateTime?>(null));

        // Act
        var act = async () => await mongoLock.ExtendAsync();

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
                 .WithMessage("*refused-lock*");
    }

    [Test]
    public async Task ExtendAsync_WhenExtensionSucceeds_ShouldReturnSameLock()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var extendedExpiry = timeProvider.GetUtcNow().AddMinutes(10).UtcDateTime;
        var mongoLock = CreateMongoLock(
            "test-lock",
            timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime,
            timeProvider,
            (_, _, _) => Task.FromResult<DateTime?>(extendedExpiry));

        // Act
        var result = await mongoLock.ExtendAsync();

        // Assert
        result.Should().BeSameAs(mongoLock);
        result.ValidUntilUtc.Should().Be(extendedExpiry);
    }

    [Test]
    public async Task ExtendAsync_WhenMongoDbOperationThrows_ShouldPropagateException()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var expectedException = new MongoException("Extension failed");
        var mongoLock = CreateMongoLock(
            "test-lock",
            timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime,
            timeProvider,
            (_, _, _) => Task.FromException<DateTime?>(expectedException));

        // Act
        var act = async () => await mongoLock.ExtendAsync();

        // Assert
        var exception = await act.Should().ThrowAsync<MongoException>();
        exception.Which.Should().BeSameAs(expectedException);
    }

    [Test]
    public async Task ExtendAsync_WithNullLock_ShouldThrowArgumentNullException()
    {
        // Arrange
        IMongoLock? mongoLock = null;

        // Act
        var act = async () => await mongoLock!.ExtendAsync();

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    private static MongoLock CreateMongoLock(
        String id,
        DateTime validUntilUtc,
        TimeProvider timeProvider,
        Func<DateTime, TimeSpan, CancellationToken, Task<DateTime?>>? extendAction = null)
    {
        return new MongoLock(id,
                             validUntilUtc,
                             DefaultLeaseTime,
                             timeProvider,
                             _ => Task.CompletedTask,
                             extendAction ?? ((_, duration, _) => Task.FromResult<DateTime?>(timeProvider.GetUtcNow().Add(duration).UtcDateTime)));
    }
}
