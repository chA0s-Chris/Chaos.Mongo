// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests.Integration;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;
using NUnit.Framework;
using Testcontainers.MongoDb;

public class MongoHelperLockIntegrationTests
{
    private MongoDbContainer _container;

    [OneTimeSetUp]
    public async Task GetMongoDbContainer() => _container = await MongoDbTestContainer.StartContainerAsync();

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
        await using var successorLock = laterLock;

        // Act
        await staleLock.DisposeAsync();

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
        await using var originalLock = firstLock;
        timeProvider.Advance(TimeSpan.FromMinutes(10));

        // Act
        var extensionTask = originalLock.TryExtendAsync(TimeSpan.FromMinutes(20));
        var acquisitionTask = helper2.TryAcquireLockAsync("at-expiry-race", TimeSpan.FromMinutes(5));
        await Task.WhenAll(extensionTask, acquisitionTask);

        // Assert
        (await extensionTask).Should().BeFalse();
        var acquiredLock = await acquisitionTask;
        acquiredLock.Should().NotBeNull();
        await using var successorLock = acquiredLock;
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
        await using var lockInstance = acquiredLock;
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
        var acquisitionToken = (await lockCollection.Find(x => x.Id == "direct-release-lock").SingleAsync()).AcquisitionToken;

        // Act
        await mongoHelper.ReleaseLockAsync("direct-release-lock", "direct-release-holder", acquisitionToken!);

        // Assert
        var lockDoc = await lockCollection.Find(x => x.Id == "direct-release-lock").FirstOrDefaultAsync();
        lockDoc.Should().BeNull();
    }

    [Test]
    public async Task ReleaseLockAsync_WhenCalledWithStaleTokenFromAcquisitionSharingSameExpiry_ShouldNotDeleteSuccessor()
    {
        // Arrange: the frozen clock makes both acquisitions below compute the exact same LeaseUntilUtc, so only
        // the per-acquisition token - not the expiry - can tell them apart.
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"LockTestDb_{Guid.NewGuid():N}";
        var timeProvider = new FakeTimeProvider();
        var mongoHelper = CreateMongoHelper(url, uniqueDbName, "shared-expiry-holder", timeProvider);
        var lockCollection = mongoHelper.Database.GetCollection<MongoLockDocument>("_locks");

        var firstLock = await mongoHelper.TryAcquireLockAsync("same-expiry-lock", TimeSpan.FromMinutes(5));
        firstLock.Should().NotBeNull();
        var firstToken = (await lockCollection.Find(x => x.Id == "same-expiry-lock").SingleAsync()).AcquisitionToken;
        await firstLock.DisposeAsync();

        var secondLock = await mongoHelper.TryAcquireLockAsync("same-expiry-lock", TimeSpan.FromMinutes(5));
        secondLock.Should().NotBeNull();
        await using var successorLock = secondLock;
        successorLock.ValidUntilUtc.Should().Be(firstLock.ValidUntilUtc);
        var secondToken = (await lockCollection.Find(x => x.Id == "same-expiry-lock").SingleAsync()).AcquisitionToken;
        secondToken.Should().NotBe(firstToken);

        // Act - release using the first acquisition's now-stale token, as a lingering delegate would.
        await mongoHelper.ReleaseLockAsync("same-expiry-lock", "shared-expiry-holder", firstToken!);

        // Assert
        var lockDocument = await lockCollection.Find(x => x.Id == "same-expiry-lock").SingleAsync();
        lockDocument.Holder.Should().Be("shared-expiry-holder");
        lockDocument.AcquisitionToken.Should().Be(secondToken);
        lockDocument.LeaseUntilUtc.Should().Be(successorLock.ValidUntilUtc);
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
        var acquisitionToken = (await lockCollection.Find(x => x.Id == "protected-lock").SingleAsync()).AcquisitionToken;

        // Act - Try to release with wrong holder ID
        await mongoHelper.ReleaseLockAsync("protected-lock", "wrong-holder", acquisitionToken!);

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
    public async Task TryAcquireLockAsync_WhenLegacyDocumentWithoutAcquisitionTokenExists_ShouldTakeOverOnlyAfterExpiry()
    {
        // Arrange: seed the exact shape written before AcquisitionToken existed, to prove documents left behind by
        // an older release still expire and can be taken over normally.
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"LockTestDb_{Guid.NewGuid():N}";
        var timeProvider = new FakeTimeProvider();
        var mongoHelper = CreateMongoHelper(url, uniqueDbName, "legacy-holder", timeProvider);
        var lockCollection = mongoHelper.Database.GetCollection<MongoLockDocument>("_locks");
        var seededExpiry = timeProvider.GetUtcNow().AddMinutes(5).UtcDateTime;
        var legacyDocument = new BsonDocument
        {
            { "_id", "legacy-lock" },
            { "Holder", "previous-holder" },
            { "LeaseUntilUtc", seededExpiry }
        };
        await mongoHelper.Database.GetCollection<BsonDocument>("_locks").InsertOneAsync(legacyDocument);

        // Act - refused while the frozen clock is still before the seeded expiry
        var refusedLock = await mongoHelper.TryAcquireLockAsync("legacy-lock", TimeSpan.FromMinutes(1));
        refusedLock.Should().BeNull();

        timeProvider.Advance(TimeSpan.FromMinutes(5));
        var acquiredLock = await mongoHelper.TryAcquireLockAsync("legacy-lock", TimeSpan.FromMinutes(1));

        // Assert
        acquiredLock.Should().NotBeNull();
        var lockInstance = acquiredLock;
        var lockDocument = await lockCollection.Find(x => x.Id == "legacy-lock").SingleAsync();
        lockDocument.Holder.Should().Be("legacy-holder");
        lockDocument.AcquisitionToken.Should().NotBeNullOrEmpty();

        (await lockInstance.TryExtendAsync(TimeSpan.FromMinutes(10))).Should().BeTrue();

        await lockInstance.DisposeAsync();
        var finalDocument = await lockCollection.Find(x => x.Id == "legacy-lock").FirstOrDefaultAsync();
        finalDocument.Should().BeNull();
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
    public async Task TryAcquireLockAsync_WhenOlderInstanceTakesOverExpiredTokenedLockSharingHolderId_ShouldLeaveStaleTokenMatchable()
    {
        // Arrange: a newer instance acquires the lock and lets its lease expire. An older (pre-this-change)
        // instance sharing the same configured HolderId then takes it over the way the old TryAcquireLockAsync
        // did: $set only Holder/LeaseUntilUtc, with no AcquisitionToken field to write. This reproduces the
        // documented rolling-upgrade boundary (docs/migrating-from-0.x.md): sharing a HolderId across old and
        // new binaries is unsupported, because the stale token from the newer instance's acquisition survives
        // the older instance's takeover unchanged.
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"LockTestDb_{Guid.NewGuid():N}";
        var timeProvider = new FakeTimeProvider();
        const String holder = "rollout-shared-holder";
        var mongoHelper = CreateMongoHelper(url, uniqueDbName, holder, timeProvider);
        var lockCollection = mongoHelper.Database.GetCollection<MongoLockDocument>("_locks");

        var newerInstanceLock = await mongoHelper.TryAcquireLockAsync("rollout-lock", TimeSpan.FromMinutes(1));
        newerInstanceLock.Should().NotBeNull();
        var originalToken = (await lockCollection.Find(x => x.Id == "rollout-lock").SingleAsync()).AcquisitionToken;
        originalToken.Should().NotBeNullOrEmpty();
        timeProvider.Advance(TimeSpan.FromMinutes(1));

        // Act - replay the pre-this-change TryAcquireLockAsync upsert: it only ever $set Holder/LeaseUntilUtc.
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var legacyTakeoverFilter = Builders<MongoLockDocument>.Filter.Eq(x => x.Id, "rollout-lock") &
                                   Builders<MongoLockDocument>.Filter.Lte(x => x.LeaseUntilUtc, now);
        var legacyTakeoverUpdate = Builders<MongoLockDocument>.Update
                                                              .Set(x => x.Holder, holder)
                                                              .Set(x => x.LeaseUntilUtc, now.AddMinutes(5));
        var legacyTakeoverResult = await lockCollection.UpdateOneAsync(legacyTakeoverFilter, legacyTakeoverUpdate);

        // Assert - the older instance's takeover succeeded but left the newer instance's token in place...
        legacyTakeoverResult.ModifiedCount.Should().Be(1);
        var tookOverDocument = await lockCollection.Find(x => x.Id == "rollout-lock").SingleAsync();
        tookOverDocument.AcquisitionToken.Should().Be(originalToken);

        // ...so the original newer-instance handle's release still matches and deletes the lease the older
        // instance now believes it owns, exactly the behavior the documented boundary warns against.
        await newerInstanceLock.DisposeAsync();
        var afterDispose = await lockCollection.Find(x => x.Id == "rollout-lock").FirstOrDefaultAsync();
        afterDispose.Should().BeNull();
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
        var cancellationToken = cts.Token;
        var act = async () => await mongoHelper.TryAcquireLockAsync("cancel-lock", cancellationToken: cancellationToken);

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
        await using var originalLock = firstLock;
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        var secondLock = await helper2.TryAcquireLockAsync("stolen-lock", TimeSpan.FromMinutes(5));
        secondLock.Should().NotBeNull();
        await using var successorLock = secondLock;

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
    public async Task TryExtendAsync_WhenCancelledAfterServerAppliedRenewal_ShouldReleaseViaTokenAndAllowImmediateReacquisition()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"LockTestDb_{Guid.NewGuid():N}";
        var timeProvider = new FakeTimeProvider();
        const String lockName = "cancelled-renewal-lock";
        const String holder = "cancelled-renewal-holder";
        var mongoHelper = CreateMongoHelper(url, uniqueDbName, holder, timeProvider);
        var lockCollection = mongoHelper.Database.GetCollection<MongoLockDocument>("_locks");
        var acquiredLock = await mongoHelper.TryAcquireLockAsync(lockName, TimeSpan.FromMinutes(5));
        acquiredLock.Should().NotBeNull();
        var acquisitionToken = (await lockCollection.Find(x => x.Id == lockName).SingleAsync()).AcquisitionToken;
        acquisitionToken.Should().NotBeNullOrEmpty();

        var cancellationTokenSource = new CancellationTokenSource();

        // The shadow lock stands in for the caller's real MongoLock instance: its extendAction lets the server
        // genuinely apply the new LeaseUntilUtc before the caller observes cancellation, reproducing a renewal
        // cancelled after the server-side effect but before the response reached the client.
        var shadowLock = new MongoLock(
            lockName,
            acquiredLock.ValidUntilUtc,
            TimeSpan.FromMinutes(5),
            timeProvider,
            () => mongoHelper.ReleaseLockAsync(lockName, holder, acquisitionToken),
            async (leaseTime, cancellationToken) =>
            {
                var serverAppliedExpiry = await mongoHelper.ExtendLockAsync(lockName, holder, acquisitionToken, leaseTime, CancellationToken.None);
                serverAppliedExpiry.Should().NotBeNull();
                await cancellationTokenSource.CancelAsync();
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            });

        // Act
        var act = async () => await shadowLock.TryExtendAsync(TimeSpan.FromMinutes(10), cancellationTokenSource.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        shadowLock.ValidUntilUtc.Should().Be(acquiredLock.ValidUntilUtc);

        await shadowLock.DisposeAsync();
        var releasedDocument = await lockCollection.Find(x => x.Id == lockName).FirstOrDefaultAsync();
        releasedDocument.Should().BeNull();

        await using var reacquiredLock = await mongoHelper.TryAcquireLockAsync(lockName, TimeSpan.FromMinutes(5));
        reacquiredLock.Should().NotBeNull();
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
        await using var lockInstance = acquiredLock;
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
    public async Task TryExtendAsync_WhenRetriedAfterServerAppliedPriorRenewal_ShouldSucceed()
    {
        // Arrange: a renewal applied directly through the helper, bypassing lockInstance, stands in for a renewal
        // whose result the caller never observed - lockInstance's cached expiry stays at the pre-renewal value.
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"LockTestDb_{Guid.NewGuid():N}";
        var timeProvider = new FakeTimeProvider();
        var mongoHelper = CreateMongoHelper(url, uniqueDbName, "retry-holder", timeProvider);
        var lockCollection = mongoHelper.Database.GetCollection<MongoLockDocument>("_locks");
        var acquiredLock = await mongoHelper.TryAcquireLockAsync("retry-lock", TimeSpan.FromMinutes(5));
        acquiredLock.Should().NotBeNull();
        await using var lockInstance = acquiredLock;
        var acquisitionToken = (await lockCollection.Find(x => x.Id == "retry-lock").SingleAsync()).AcquisitionToken;

        timeProvider.Advance(TimeSpan.FromMinutes(1));
        var serverAppliedExpiry = await mongoHelper.ExtendLockAsync("retry-lock", "retry-holder", acquisitionToken!, TimeSpan.FromMinutes(10));
        serverAppliedExpiry.Should().NotBeNull();

        // Act
        var result = await lockInstance.TryExtendAsync(TimeSpan.FromMinutes(20));

        // Assert
        result.Should().BeTrue();
        lockInstance.IsValid.Should().BeTrue();
        var lockDocument = await lockCollection.Find(x => x.Id == "retry-lock").SingleAsync();
        lockDocument.LeaseUntilUtc.Should().Be(lockInstance.ValidUntilUtc);
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
        await using var staleLock = acquiredStaleLock;
        var staleExpiry = staleLock.ValidUntilUtc;
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        var acquiredSuccessorLock = await mongoHelper.TryAcquireLockAsync("stale-extension-lock", TimeSpan.FromMinutes(5));
        acquiredSuccessorLock.Should().NotBeNull();
        await using var successorLock = acquiredSuccessorLock;
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
        await using var lockInstance = acquiredLock;
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
