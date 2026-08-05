// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests.Integration;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Driver;
using NUnit.Framework;
using Testcontainers.MongoDb;

public class MongoHelperLockIntegrationTests
{
    private MongoDbContainer _container;

    [Test]
    public async Task DisposeAsync_WhenSameHolderReacquiredExpiredLock_ShouldNotDeleteSuccessor()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"LockTestDb_{Guid.NewGuid():N}";
        var timeProvider = new FakeTimeProvider();
        var mongoHelper = CreateMongoHelper(url, uniqueDbName, "shared-holder", timeProvider);
        var lockCollection = mongoHelper.Database.GetCollection<MongoLockDocument>("_locks");
        var staleLock = await mongoHelper.TryAcquireLockAsync("reacquired-lock", TimeSpan.FromMinutes(1));
        staleLock.Should().NotBeNull();
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        var laterLock = await mongoHelper.TryAcquireLockAsync("reacquired-lock", TimeSpan.FromMinutes(5));
        laterLock.Should().NotBeNull();
        await using var successorLock = laterLock!;

        // Act
        await staleLock!.DisposeAsync();

        // Assert
        var lockDocument = await lockCollection.Find(x => x.Id == "reacquired-lock").SingleAsync();
        lockDocument.Holder.Should().Be("shared-holder");
        lockDocument.LeaseUntilUtc.Should().Be(successorLock.ValidUntilUtc);
    }

    [Test]
    public async Task ExtensionAndAcquisition_AtExpiryWithSharedClock_AcquisitionShouldWin()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"LockTestDb_{Guid.NewGuid():N}";
        var timeProvider = new FakeTimeProvider();
        var helper1 = CreateMongoHelper(url, uniqueDbName, "holder-1", timeProvider);
        var helper2 = CreateMongoHelper(url, uniqueDbName, "holder-2", timeProvider);
        var lockCollection = helper1.Database.GetCollection<MongoLockDocument>("_locks");
        var firstLock = await helper1.TryAcquireLockAsync("at-expiry-race", TimeSpan.FromMinutes(10));
        firstLock.Should().NotBeNull();
        await using var originalLock = firstLock!;
        timeProvider.Advance(TimeSpan.FromMinutes(10));

        // Act
        var extensionTask = originalLock.TryExtendAsync(TimeSpan.FromMinutes(20));
        var acquisitionTask = helper2.TryAcquireLockAsync("at-expiry-race", TimeSpan.FromMinutes(5));
        await Task.WhenAll(extensionTask, acquisitionTask);

        // Assert
        (await extensionTask).Should().BeFalse();
        var acquiredLock = await acquisitionTask;
        acquiredLock.Should().NotBeNull();
        await using var successorLock = acquiredLock!;
        var lockDocument = await lockCollection.Find(x => x.Id == "at-expiry-race").SingleAsync();
        lockDocument.Holder.Should().Be("holder-2");
        lockDocument.LeaseUntilUtc.Should().Be(successorLock.ValidUntilUtc);
    }

    [Test]
    public async Task ExtensionAndAcquisition_BeforeExpiryWithSharedClock_ExtensionShouldWin()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"LockTestDb_{Guid.NewGuid():N}";
        var timeProvider = new FakeTimeProvider();
        var helper1 = CreateMongoHelper(url, uniqueDbName, "holder-1", timeProvider);
        var helper2 = CreateMongoHelper(url, uniqueDbName, "holder-2", timeProvider);
        var lockCollection = helper1.Database.GetCollection<MongoLockDocument>("_locks");
        var acquiredLock = await helper1.TryAcquireLockAsync("before-expiry-race", TimeSpan.FromMinutes(10));
        acquiredLock.Should().NotBeNull();
        await using var lockInstance = acquiredLock!;
        timeProvider.Advance(TimeSpan.FromMinutes(1));

        // Act
        var extensionTask = lockInstance.TryExtendAsync(TimeSpan.FromMinutes(20));
        var acquisitionTask = helper2.TryAcquireLockAsync("before-expiry-race", TimeSpan.FromMinutes(5));
        await Task.WhenAll(extensionTask, acquisitionTask);

        // Assert
        (await extensionTask).Should().BeTrue();
        (await acquisitionTask).Should().BeNull();
        var lockDocument = await lockCollection.Find(x => x.Id == "before-expiry-race").SingleAsync();
        lockDocument.Holder.Should().Be("holder-1");
        lockDocument.LeaseUntilUtc.Should().Be(lockInstance.ValidUntilUtc);
    }

    [OneTimeSetUp]
    public async Task GetMongoDbContainer() => _container = await MongoDbTestContainer.StartContainerAsync();

    [Test]
    public async Task ReleaseLockAsync_WhenCalledDirectly_ShouldDeleteLockForMatchingHolder()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"LockTestDb_{Guid.NewGuid():N}";
        var serviceProvider = new ServiceCollection()
                              .AddMongo(url, configure: options =>
                              {
                                  options.DefaultDatabase = uniqueDbName;
                                  options.HolderId = "direct-release-holder";
                              })
                              .Services
                              .BuildServiceProvider();

        var mongoHelper = (MongoHelper)serviceProvider.GetRequiredService<IMongoHelper>();
        var lockCollection = mongoHelper.Database.GetCollection<MongoLockDocument>("_locks");

        var lockInstance = await mongoHelper.TryAcquireLockAsync("direct-release-lock");
        lockInstance.Should().NotBeNull();

        // Act
        await mongoHelper.ReleaseLockAsync("direct-release-lock", "direct-release-holder", lockInstance!.ValidUntilUtc);

        // Assert
        var lockDoc = await lockCollection.Find(x => x.Id == "direct-release-lock").FirstOrDefaultAsync();
        lockDoc.Should().BeNull();
    }

    [Test]
    public async Task ReleaseLockAsync_WhenCalledWithWrongHolder_ShouldNotDeleteLock()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"LockTestDb_{Guid.NewGuid():N}";
        var serviceProvider = new ServiceCollection()
                              .AddMongo(url, configure: options =>
                              {
                                  options.DefaultDatabase = uniqueDbName;
                                  options.HolderId = "actual-holder";
                              })
                              .Services
                              .BuildServiceProvider();

        var mongoHelper = (MongoHelper)serviceProvider.GetRequiredService<IMongoHelper>();
        var lockCollection = mongoHelper.Database.GetCollection<MongoLockDocument>("_locks");

        var lockInstance = await mongoHelper.TryAcquireLockAsync("protected-lock");
        lockInstance.Should().NotBeNull();

        // Act - Try to release with wrong holder ID
        await mongoHelper.ReleaseLockAsync("protected-lock", "wrong-holder", lockInstance!.ValidUntilUtc);

        // Assert - Lock should still exist
        var lockDoc = await lockCollection.Find(x => x.Id == "protected-lock").FirstOrDefaultAsync();
        lockDoc.Should().NotBeNull();
        lockDoc.Holder.Should().Be("actual-holder");
    }

    [Test]
    public async Task ReleaseLockAsync_WhenLockIsHeld_ShouldDeleteLockDocument()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"LockTestDb_{Guid.NewGuid():N}";
        var serviceProvider = new ServiceCollection()
                              .AddMongo(url, configure: options =>
                              {
                                  options.DefaultDatabase = uniqueDbName;
                                  options.HolderId = "test-holder";
                              })
                              .Services
                              .BuildServiceProvider();

        var mongoHelper = (MongoHelper)serviceProvider.GetRequiredService<IMongoHelper>();
        var lockCollection = mongoHelper.Database.GetCollection<MongoLockDocument>("_locks");

        await using (var lockInstance = await mongoHelper.TryAcquireLockAsync("release-test-lock"))
        {
            lockInstance.Should().NotBeNull();

            // Verify lock exists in database
            var lockDoc = await lockCollection.Find(x => x.Id == "release-test-lock").FirstOrDefaultAsync();
            lockDoc.Should().NotBeNull();
            lockDoc.Holder.Should().Be("test-holder");
        }

        // Act - DisposeAsync calls ReleaseLockAsync

        // Assert - Lock should be deleted
        var deletedLock = await lockCollection.Find(x => x.Id == "release-test-lock").FirstOrDefaultAsync();
        deletedLock.Should().BeNull();
    }

    [Test]
    public async Task TryAcquireLockAsync_AfterLockRelease_ShouldAllowReacquisition()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"LockTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        // Acquire and release lock
        await using (var firstLock = await mongoHelper.TryAcquireLockAsync("reacquire-lock"))
        {
            firstLock.Should().NotBeNull();
        }

        // Act - Try to acquire again
        await using var secondLock = await mongoHelper.TryAcquireLockAsync("reacquire-lock");

        // Assert
        secondLock.Should().NotBeNull();
        secondLock.Id.Should().Be("reacquire-lock");
    }

    [Test]
    public async Task TryAcquireLockAsync_MultipleDifferentLocks_ShouldAcquireIndependently()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"LockTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        // Act
        await using var lock1 = await mongoHelper.TryAcquireLockAsync("lock-1");
        await using var lock2 = await mongoHelper.TryAcquireLockAsync("lock-2");
        await using var lock3 = await mongoHelper.TryAcquireLockAsync("lock-3");

        // Assert
        lock1.Should().NotBeNull();
        lock2.Should().NotBeNull();
        lock3.Should().NotBeNull();
        lock1.Id.Should().Be("lock-1");
        lock2.Id.Should().Be("lock-2");
        lock3.Id.Should().Be("lock-3");
    }

    [Test]
    public async Task TryAcquireLockAsync_WhenLockDoesNotExist_ShouldAcquireLock()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"LockTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        // Act
        await using var lockInstance = await mongoHelper.TryAcquireLockAsync("test-lock");

        // Assert
        lockInstance.Should().NotBeNull();
        lockInstance.Id.Should().Be("test-lock");
        lockInstance.ValidUntilUtc.Should().BeAfter(DateTime.UtcNow);
    }

    [Test]
    public async Task TryAcquireLockAsync_WhenLockHasExpired_ShouldAcquireLock()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"LockTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        // Acquire lock with very short lease time
        await using (var expiredLock = await mongoHelper.TryAcquireLockAsync("expired-lock", TimeSpan.FromMilliseconds(1)))
        {
            expiredLock.Should().NotBeNull();
        }

        // Wait for lock to expire
        await Task.Delay(100);

        // Act
        await using var newLock = await mongoHelper.TryAcquireLockAsync("expired-lock");

        // Assert
        newLock.Should().NotBeNull();
        newLock.Id.Should().Be("expired-lock");
    }

    [Test]
    public async Task TryAcquireLockAsync_WhenLockIsHeldByDifferentHolder_ShouldReturnNull()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"LockTestDb_{Guid.NewGuid():N}";
        var uniqueLockName = $"contended-lock-{Guid.NewGuid()}";

        var helper1 = new ServiceCollection()
                      .AddMongo(url, configure: options =>
                      {
                          options.DefaultDatabase = uniqueDbName;
                          options.HolderId = "holder-1";
                      })
                      .Services
                      .BuildServiceProvider()
                      .GetRequiredService<IMongoHelper>();

        var helper2 = new ServiceCollection()
                      .AddMongo(url, configure: options =>
                      {
                          options.DefaultDatabase = uniqueDbName;
                          options.HolderId = "holder-2";
                      })
                      .Services
                      .BuildServiceProvider()
                      .GetRequiredService<IMongoHelper>();

        // Act
        await using var lock1 = await helper1.TryAcquireLockAsync(uniqueLockName);
        var lock2 = await helper2.TryAcquireLockAsync(uniqueLockName);

        // Assert
        lock1.Should().NotBeNull();
        lock2.Should().BeNull();
    }

    [Test]
    public async Task TryAcquireLockAsync_WithCancellationToken_ShouldRespectCancellation()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"LockTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Act
        var act = async () => await mongoHelper.TryAcquireLockAsync("cancel-lock", cancellationToken: cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task TryAcquireLockAsync_WithCustomLeaseTime_ShouldSetCorrectExpiry()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"LockTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();
        var leaseTime = TimeSpan.FromMinutes(10);
        var beforeAcquire = DateTime.UtcNow;

        // Act
        await using var lockInstance = await mongoHelper.TryAcquireLockAsync("custom-lease-lock", leaseTime);

        // Assert
        lockInstance.Should().NotBeNull();
        lockInstance.ValidUntilUtc.Should().BeCloseTo(beforeAcquire.Add(leaseTime), TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task TryAcquireLockAsync_WithEmptyLockName_ShouldThrowArgumentException()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"LockTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        // Act
        var act = async () => await mongoHelper.TryAcquireLockAsync(String.Empty);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Test]
    public async Task TryAcquireLockAsync_WithNullLockName_ShouldThrowArgumentException()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"LockTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        // Act
        var act = async () => await mongoHelper.TryAcquireLockAsync(null!);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Test]
    public async Task TryAcquireLockAsync_WithSubMillisecondLease_ShouldThrowBeforeWritingDocument()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"LockTestDb_{Guid.NewGuid():N}";
        var mongoHelper = CreateMongoHelper(url, uniqueDbName, "test-holder", new FakeTimeProvider());
        var lockCollection = mongoHelper.Database.GetCollection<MongoLockDocument>("_locks");

        // Act
        var act = async () => await mongoHelper.TryAcquireLockAsync("short-lease", TimeSpan.FromTicks(1));

        // Assert
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        (await lockCollection.CountDocumentsAsync(FilterDefinition<MongoLockDocument>.Empty)).Should().Be(0);
    }

    [Test]
    public async Task TryExtendAsync_WhenAnotherHolderAcquiredExpiredLock_ShouldRefuseWithoutModifyingSuccessor()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"LockTestDb_{Guid.NewGuid():N}";
        var timeProvider = new FakeTimeProvider();
        var helper1 = CreateMongoHelper(url, uniqueDbName, "holder-1", timeProvider);
        var helper2 = CreateMongoHelper(url, uniqueDbName, "holder-2", timeProvider);
        var lockCollection = helper1.Database.GetCollection<MongoLockDocument>("_locks");
        var firstLock = await helper1.TryAcquireLockAsync("stolen-lock", TimeSpan.FromMinutes(1));
        firstLock.Should().NotBeNull();
        await using var originalLock = firstLock!;
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        var secondLock = await helper2.TryAcquireLockAsync("stolen-lock", TimeSpan.FromMinutes(5));
        secondLock.Should().NotBeNull();
        await using var successorLock = secondLock!;

        // Act
        var result = await originalLock.TryExtendAsync();

        // Assert
        result.Should().BeFalse();
        originalLock.IsValid.Should().BeFalse();
        var lockDocument = await lockCollection.Find(x => x.Id == "stolen-lock").SingleAsync();
        lockDocument.Holder.Should().Be("holder-2");
        lockDocument.LeaseUntilUtc.Should().Be(successorLock.ValidUntilUtc);
    }

    [Test]
    public async Task TryExtendAsync_WhenExpiredAndUnclaimed_ShouldRefuseWithoutModifyingDocument()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"LockTestDb_{Guid.NewGuid():N}";
        var timeProvider = new FakeTimeProvider();
        var mongoHelper = CreateMongoHelper(url, uniqueDbName, "holder-1", timeProvider);
        var lockCollection = mongoHelper.Database.GetCollection<MongoLockDocument>("_locks");
        var acquiredLock = await mongoHelper.TryAcquireLockAsync("expired-unclaimed-lock", TimeSpan.FromMinutes(1));
        acquiredLock.Should().NotBeNull();
        await using var lockInstance = acquiredLock!;
        var originalExpiry = lockInstance.ValidUntilUtc;
        timeProvider.Advance(TimeSpan.FromMinutes(1));

        // Act
        var result = await lockInstance.TryExtendAsync();

        // Assert
        result.Should().BeFalse();
        lockInstance.IsValid.Should().BeFalse();
        lockInstance.ValidUntilUtc.Should().Be(originalExpiry);
        var lockDocument = await lockCollection.Find(x => x.Id == "expired-unclaimed-lock").SingleAsync();
        lockDocument.Holder.Should().Be("holder-1");
        lockDocument.LeaseUntilUtc.Should().Be(originalExpiry);
    }

    [Test]
    public async Task TryExtendAsync_WhenSameHolderReacquiredExpiredLock_ShouldRefuseWithoutModifyingSuccessor()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"LockTestDb_{Guid.NewGuid():N}";
        var timeProvider = new FakeTimeProvider();
        var mongoHelper = CreateMongoHelper(url, uniqueDbName, "shared-holder", timeProvider);
        var lockCollection = mongoHelper.Database.GetCollection<MongoLockDocument>("_locks");
        var acquiredStaleLock = await mongoHelper.TryAcquireLockAsync("stale-extension-lock", TimeSpan.FromMinutes(1));
        acquiredStaleLock.Should().NotBeNull();
        await using var staleLock = acquiredStaleLock!;
        var staleExpiry = staleLock.ValidUntilUtc;
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        var acquiredSuccessorLock = await mongoHelper.TryAcquireLockAsync("stale-extension-lock", TimeSpan.FromMinutes(5));
        acquiredSuccessorLock.Should().NotBeNull();
        await using var successorLock = acquiredSuccessorLock!;
        var successorExpiry = successorLock.ValidUntilUtc;

        // Act
        var result = await staleLock.TryExtendAsync(TimeSpan.FromMinutes(10));

        // Assert
        result.Should().BeFalse();
        staleLock.IsValid.Should().BeFalse();
        staleLock.ValidUntilUtc.Should().Be(staleExpiry);
        var lockDocument = await lockCollection.Find(x => x.Id == "stale-extension-lock").SingleAsync();
        lockDocument.Holder.Should().Be("shared-holder");
        lockDocument.LeaseUntilUtc.Should().Be(successorExpiry);
    }

    [Test]
    public async Task TryExtendAsync_WhenSuccessful_ShouldUpdateLockAndStoredDocument()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"LockTestDb_{Guid.NewGuid():N}";
        var timeProvider = new FakeTimeProvider();
        var mongoHelper = CreateMongoHelper(url, uniqueDbName, "holder-1", timeProvider);
        var lockCollection = mongoHelper.Database.GetCollection<MongoLockDocument>("_locks");
        var acquiredLock = await mongoHelper.TryAcquireLockAsync("extend-lock", TimeSpan.FromMinutes(5));
        acquiredLock.Should().NotBeNull();
        await using var lockInstance = acquiredLock!;
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        var expectedExpiry = timeProvider.GetUtcNow().AddMinutes(10).UtcDateTime;

        // Act
        var result = await lockInstance.TryExtendAsync(TimeSpan.FromMinutes(10));

        // Assert
        result.Should().BeTrue();
        lockInstance.ValidUntilUtc.Should().Be(expectedExpiry);
        var lockDocument = await lockCollection.Find(x => x.Id == "extend-lock").SingleAsync();
        lockDocument.Holder.Should().Be("holder-1");
        lockDocument.LeaseUntilUtc.Should().Be(expectedExpiry);
    }

    private static MongoHelper CreateMongoHelper(MongoUrl url,
                                                 String databaseName,
                                                 String holderId,
                                                 TimeProvider timeProvider)
    {
        var services = new ServiceCollection();
        services.AddSingleton(timeProvider);

        return (MongoHelper)services
                            .AddMongo(url, configure: options =>
                            {
                                options.DefaultDatabase = databaseName;
                                options.HolderId = holderId;
                            })
                            .Services
                            .BuildServiceProvider()
                            .GetRequiredService<IMongoHelper>();
    }
}
