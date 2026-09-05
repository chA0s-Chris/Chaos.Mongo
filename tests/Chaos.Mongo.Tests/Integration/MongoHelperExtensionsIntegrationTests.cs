// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests.Integration;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using NUnit.Framework;
using Testcontainers.MongoDb;

public class MongoHelperExtensionsIntegrationTests
{
    private MongoDbContainer _container;

    [Test]
    public async Task AcquireLockAsync_WhenLockBecomesAvailable_ShouldRetryAndAcquire()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"AcquireLockTestDb_{Guid.NewGuid():N}";
        var uniqueLockName = $"retry-lock-{Guid.NewGuid()}";

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

        // Acquire lock with helper1 and release it after a short delay
        var lock1 = await helper1.AcquireLockAsync(uniqueLockName, TimeSpan.FromMinutes(5));

        // Start helper2's acquisition attempt in background (will retry)
        var acquireTask = Task.Run(async () => await helper2.AcquireLockAsync(uniqueLockName, retryDelay: TimeSpan.FromMilliseconds(50)));

        // Wait a bit to ensure helper2 is retrying
        await Task.Delay(200);

        // Release lock1
        await lock1.DisposeAsync();

        // Act - helper2 should eventually acquire the lock
        await using var lock2 = await acquireTask;

        // Assert
        lock2.Should().NotBeNull();
        lock2.Id.Should().Be(uniqueLockName);
    }

    [Test]
    public async Task AcquireLockAsync_WhenLockIsAvailable_ShouldAcquireLock()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"AcquireLockTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        // Act
        await using var lockInstance = await mongoHelper.AcquireLockAsync("available-lock");

        // Assert
        lockInstance.Should().NotBeNull();
        lockInstance.Id.Should().Be("available-lock");
        lockInstance.ValidUntilUtc.Should().BeAfter(DateTime.UtcNow);
    }

    [Test]
    public async Task AcquireLockAsync_WithCancellationDuringRetry_ShouldThrowOperationCanceledException()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"AcquireLockTestDb_{Guid.NewGuid():N}";
        var uniqueLockName = $"cancel-retry-lock-{Guid.NewGuid()}";

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

        await using var lock1 = await helper1.AcquireLockAsync(uniqueLockName);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        // Act - helper2 tries to acquire but should be cancelled during retry
        var cancellationToken = cts.Token;
        var act = async () => await helper2.AcquireLockAsync(
            uniqueLockName,
            retryDelay: TimeSpan.FromMilliseconds(50),
            cancellationToken: cancellationToken);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task AcquireLockAsync_WithCustomLeaseTime_ShouldSetCorrectExpiry()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"AcquireLockTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();
        var leaseTime = TimeSpan.FromMinutes(15);
        var beforeAcquire = DateTime.UtcNow;

        // Act
        await using var lockInstance = await mongoHelper.AcquireLockAsync("custom-lease", leaseTime);

        // Assert
        lockInstance.Should().NotBeNull();
        lockInstance.ValidUntilUtc.Should().BeCloseTo(beforeAcquire.Add(leaseTime), TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task AcquireLockAsync_WithCustomRetryDelay_ShouldUseSpecifiedDelay()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"AcquireLockTestDb_{Guid.NewGuid():N}";
        var uniqueLockName = $"custom-delay-lock-{Guid.NewGuid()}";

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

        await using var lock1 = await helper1.AcquireLockAsync(uniqueLockName);

        var startTime = DateTime.UtcNow;
        var customDelay = TimeSpan.FromMilliseconds(300);

        // Start acquisition in background with custom delay
        var acquireTask = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await helper2.AcquireLockAsync(uniqueLockName, retryDelay: customDelay, cancellationToken: cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected - lock won't be released
            }
        });

        // Wait for a couple of retry attempts
        await Task.Delay(700);

        var elapsed = DateTime.UtcNow - startTime;

        // Assert - should have waited at least 2 retry delays (2 * 300ms = 600ms)
        elapsed.Should().BeGreaterOrEqualTo(TimeSpan.FromMilliseconds(600));

        await acquireTask;
    }

    [Test]
    public async Task AcquireLockAsync_WithNullLockName_ShouldThrowArgumentException()
    {
        // Arrange
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"AcquireLockTestDb_{Guid.NewGuid():N}";
        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        // Act
        var act = async () => await mongoHelper.AcquireLockAsync(null!);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Test]
    public async Task ExecuteInTransaction_ShouldRetryOnTransientErrorAndEventuallyCommitChanges()
    {
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"ExecuteInTransactionDb_{Guid.NewGuid():N}";

        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                              options.AddMapping<TestDocument>("TestDocuments");
                              options.AddMapping<Counter>("Counters");
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var counters = mongoHelper.GetCollection<Counter>();
        var testDocuments = mongoHelper.GetCollection<TestDocument>();

        await counters.InsertOneAsync(new Counter
        {
            Id = "Test",
            Value = 42
        });

        await testDocuments.InsertOneAsync(new TestDocument
        {
            Id = ObjectId.GenerateNewId(),
            Value = 0
        });

        var pass = 1;

        await mongoHelper.ExecuteInTransaction(async (_, session, token) =>
        {
            var currentCounter = await counters.Find(session, x => x.Id == "Test")
                                               .Project(x => x.Value)
                                               .FirstOrDefaultAsync(token);

            currentCounter.Should().Be(42);

            await counters.UpdateOneAsync(session, x => x.Id == "Test",
                                          Builders<Counter>.Update
                                                           .Inc(x => x.Value, 1), cancellationToken: token);

            var documentsToAdd = Enumerable.Range(1, 10)
                                           .Select(x => new TestDocument
                                           {
                                               Id = ObjectId.GenerateNewId(),
                                               Value = x
                                           });

            await testDocuments.InsertManyAsync(session, documentsToAdd, cancellationToken: token);

            pass++;
            if (pass < 5)
            {
                // simulate a transient error that should be retried
                var mongoException = new MongoException("Something went wrong...");
                mongoException.AddErrorLabel("TransientTransactionError");
                throw mongoException;
            }
        });


        var counter = await counters.Find(x => x.Id == "Test")
                                    .Project(x => x.Value)
                                    .FirstAsync();

        counter.Should().Be(43);

        var documentCount = await testDocuments.CountDocumentsAsync(FilterDefinition<TestDocument>.Empty);
        documentCount.Should().Be(11);
    }

    [Test]
    public async Task ExecuteInTransaction_ShouldStopOnApplicationErrorAndRollbackChanges()
    {
        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"ExecuteInTransactionDb_{Guid.NewGuid():N}";

        var mongoHelper = new ServiceCollection()
                          .AddMongo(url, configure: options =>
                          {
                              options.DefaultDatabase = uniqueDbName;
                              options.AddMapping<TestDocument>("TestDocuments");
                          })
                          .Services
                          .BuildServiceProvider()
                          .GetRequiredService<IMongoHelper>();

        var counters = mongoHelper.GetCollection<Counter>();
        await counters.InsertOneAsync(new Counter
        {
            Id = "Test",
            Value = 42
        });

        try
        {
            _ = await mongoHelper.ExecuteInTransaction(async (_, session, token) =>
            {
                var result = await counters.UpdateOneAsync(session, x => x.Id == "Test",
                                                           Builders<Counter>.Update
                                                                            .Inc(x => x.Value, 100), cancellationToken: token);

                result.ModifiedCount.Should().Be(1);

                throw new InvalidOperationException("Something went wrong...");
#pragma warning disable CS0162 // Unreachable code detected
                return 0;
#pragma warning restore CS0162 // Unreachable code detected
            });
        }
        catch (InvalidOperationException e)
        {
            e.Message.Should().Be("Something went wrong...");
        }

        var currentCounter = await counters.Find(x => x.Id == "Test")
                                           .Project(x => x.Value)
                                           .FirstOrDefaultAsync();

        currentCounter.Should().Be(42);
    }

    [OneTimeSetUp]
    public async Task GetMongoDbContainer() => _container = await MongoDbTestContainer.StartContainerAsync();

    private class Counter
    {
        [BsonId]
        public required String Id { get; init; }

        public Int32 Value { get; init; }
    }

    private class TestDocument
    {
        [BsonId]
        public ObjectId Id { get; init; }

        public Int32 Value { get; init; }
    }
}
