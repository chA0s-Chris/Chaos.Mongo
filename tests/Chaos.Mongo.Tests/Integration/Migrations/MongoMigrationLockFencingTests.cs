// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests.Integration.Migrations;

using Chaos.Mongo.Migrations;
using Chaos.Mongo.TestSupport;
using Chaos.Testing.Logging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;
using NUnit.Framework;
using Testcontainers.MongoDb;

/// <summary>
/// Tests the server-side ownership check of transactional migrations: the migration transaction writes to the lock
/// document first, so MongoDB orders a takeover against the transaction instead of the runner's client-side checks.
/// </summary>
[Category("Integration")]
public class MongoMigrationLockFencingTests
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);
    private MongoDbContainer _container;

    [OneTimeSetUp]
    public async Task GetMongoDbContainer() => _container = await MongoDbTestContainer.StartContainerAsync();

    [Test]
    public async Task RunMigrationsAsync_WithLeaseOutlastedByTransaction_StopsAsLockLossAndRollsBackEverything()
    {
        // Arrange
        var databaseName = TestDatabases.NewName("FencingLeaseOutlasted");
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var appName = $"holder-{Guid.NewGuid():N}";
        var helper = CreateHelper(databaseName, "holder-1", clock, appName);
        var hookedHelper = new HookedMongoHelper(helper);
        var inside = NewGate();
        var migration = new FencingTestMigration("holder", async (_, token) =>
        {
            inside.TrySetResult();
            await new TaskCompletionSource().Task.WaitAsync(token);
        });
        var logger = new NUnitTestLogger<MongoMigrationRunner>(captureMessages: true);
        var runner = CreateRunner(hookedHelper, migration, logger, clock);
        using var testCancellation = new CancellationTokenSource(OverallTimeout);
        var cancellationToken = testCancellation.Token;
        Task<MongoMigrationResult>? runTask = null;

        try
        {
            // Act
            runTask = runner.RunMigrationsAsync(cancellationToken);
            await inside.Task.WaitAsync(TestTimeout, cancellationToken);

            // The ownership write and the session-bound data write have both succeeded. The renewal at half of the lease
            // now waits behind the transaction on the server.
            clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
            await WaitForActiveOperationAsync(helper, appName, "findAndModify", databaseName, "_locks", cancellationToken);

            // The lease runs out while ApplyAsync is still running, so before the commit is sent.
            clock.Advance(TimeSpan.FromMinutes(5));
            var act = async () => await runTask.WaitAsync(TestTimeout, cancellationToken);

            // Assert
            await act.Should().ThrowAsync<OperationCanceledException>();
            migration.ApplyCallCount.Should().Be(1);
            (await FindDocumentsAsync(helper)).Should().BeEmpty("the transaction was rolled back, including the data write that succeeded");
            (await FindHistoryAsync(helper)).Should().BeEmpty();
            logger.LogMessages.Should().Contain(message => message.LogLevel == LogLevel.Warning && message.Message.Contains("was lost"));
        }
        finally
        {
            await CancelAndDrainAsync(testCancellation, runTask);
        }
    }

    [Test]
    public async Task RunMigrationsAsync_WithRenewalPendingBehindOpenTransaction_CompletesAndExtendsLeaseAfterCommit()
    {
        // Arrange
        var databaseName = TestDatabases.NewName("FencingRenewalPending");
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var appName = $"holder-{Guid.NewGuid():N}";
        var helper = CreateHelper(databaseName, "holder-1", clock, appName);
        var hookedHelper = new HookedMongoHelper(helper);
        var inside = NewGate();
        var release = NewGate();
        var migration = new FencingTestMigration("holder", async (_, token) =>
        {
            inside.TrySetResult();
            await release.Task.WaitAsync(token);
        });
        var logger = new NUnitTestLogger<MongoMigrationRunner>(captureMessages: true);
        var runner = CreateRunner(hookedHelper, migration, logger, clock);
        using var testCancellation = new CancellationTokenSource(OverallTimeout);
        var cancellationToken = testCancellation.Token;
        Task<MongoMigrationResult>? runTask = null;

        try
        {
            // Act
            runTask = runner.RunMigrationsAsync(cancellationToken);
            await inside.Task.WaitAsync(TestTimeout, cancellationToken);
            var hookedLock = hookedHelper.Locks.Should().ContainSingle().Subject;
            var validUntilBeforeRenewal = hookedLock.ValidUntilUtc;

            // Half of the lease has passed, so the renewal runs while the transaction is open, with ample lease left. The
            // migration stays gated until the server lists the renewal as waiting behind the transaction.
            clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
            await WaitForActiveOperationAsync(helper, appName, "findAndModify", databaseName, "_locks", cancellationToken);
            release.SetResult();
            var result = await runTask.WaitAsync(TestTimeout, cancellationToken);

            // Assert
            result.AppliedMigrationIds.Should().Equal(migration.Id);
            hookedLock.ExtensionResults.Should().Equal(true);
            hookedLock.ValidUntilUtc.Should().BeAfter(validUntilBeforeRenewal, "the renewal completed once the transaction had ended");
            (await FindHistoryAsync(helper)).Should().ContainSingle();
            (await FindDocumentsAsync(helper)).Should().ContainSingle().Which.AppliedBy.Should().Be("holder");
            logger.LogMessages.Should().NotContain(message => message.LogLevel >= LogLevel.Warning);
        }
        finally
        {
            release.TrySetResult();
            await CancelAndDrainAsync(testCancellation, runTask);
        }
    }

    [Test]
    public async Task RunMigrationsAsync_WithTakeoverAfterOwnershipWrite_ContenderWaitsForCommitAndAppliesNothing()
    {
        // Arrange
        var databaseName = TestDatabases.NewName("FencingAfterWrite");
        var holderHelper = CreateHelper(databaseName, "holder-1", TimeProvider.System);
        var hookedHelper = new HookedMongoHelper(holderHelper);
        var fenced = NewGate();
        var continueCommit = NewGate();
        hookedHelper.AfterEnsureHeld = async (_, held, token) =>
        {
            held.Should().BeTrue();
            fenced.TrySetResult();
            await continueCommit.Task.WaitAsync(token);
        };

        // The contender's clock is past the holder's lease, so its acquisition judges the stored lease expired. The
        // holder's own client-side checks still pass, as for a runner that stalled after its last check. The holder's
        // lease (10 minutes) is far longer than this test, so no renewal falls into the race.
        var contenderClock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddMinutes(30));
        var contenderAppName = $"contender-{Guid.NewGuid():N}";
        var contenderHelper = CreateHelper(databaseName, "holder-2", contenderClock, contenderAppName);
        var holderMigration = new FencingTestMigration("stalled-runner");
        var contenderMigration = new FencingTestMigration("contender");
        var holderRunner = CreateRunner(hookedHelper, holderMigration, new NUnitTestLogger<MongoMigrationRunner>(), TimeProvider.System);
        var contenderRunner = CreateRunner(contenderHelper, contenderMigration, new NUnitTestLogger<MongoMigrationRunner>(), contenderClock);
        using var testCancellation = new CancellationTokenSource(OverallTimeout);
        var cancellationToken = testCancellation.Token;
        Task<MongoMigrationResult>? holderTask = null;
        Task<MongoMigrationResult>? contenderTask = null;

        try
        {
            // Act
            holderTask = holderRunner.RunMigrationsAsync(cancellationToken);
            await fenced.Task.WaitAsync(TestTimeout, cancellationToken);
            var lockBefore = await FindLockAsync(holderHelper, cancellationToken);
            contenderTask = contenderRunner.RunMigrationsAsync(cancellationToken);

            // Only an acquisition that reached the server while the holder's transaction is open proves the contest. The
            // commit stays gated until the server has listed it.
            await WaitForActiveOperationAsync(holderHelper, contenderAppName, "findAndModify", databaseName, "_locks", cancellationToken);
            var contenderCompletedWhileTransactionOpen = contenderTask.IsCompleted;
            var lockWhileTransactionOpen = await FindLockAsync(holderHelper, cancellationToken);
            continueCommit.SetResult();
            var holderResult = await holderTask.WaitAsync(TestTimeout, cancellationToken);
            var contenderResult = await contenderTask.WaitAsync(TestTimeout, cancellationToken);

            // Assert
            contenderCompletedWhileTransactionOpen.Should().BeFalse("a takeover attempted after the ownership write waits until the transaction ends");
            lockWhileTransactionOpen.Holder.Should().Be("holder-1");
            lockWhileTransactionOpen.AcquisitionToken.Should().Be(lockBefore.AcquisitionToken);
            lockWhileTransactionOpen.LeaseUntilUtc.Should().Be(lockBefore.LeaseUntilUtc);
            holderResult.AppliedMigrationIds.Should().Equal(holderMigration.Id);
            contenderResult.Skipped.Should().BeFalse();
            contenderResult.AppliedMigrationIds.Should().BeEmpty();
            holderMigration.ApplyCallCount.Should().Be(1);
            contenderMigration.ApplyCallCount.Should().Be(0);
            (await FindDocumentsAsync(holderHelper)).Should().ContainSingle().Which.AppliedBy.Should().Be("stalled-runner");
            (await FindHistoryAsync(holderHelper)).Should().ContainSingle();
        }
        finally
        {
            continueCommit.TrySetResult();
            await CancelAndDrainAsync(testCancellation, holderTask, contenderTask);
        }
    }

    [Test]
    public async Task RunMigrationsAsync_WithTakeoverBeforeOwnershipWrite_StalledRunnerStopsAndTakingOverRunnerAppliesOnce()
    {
        // Arrange
        var databaseName = TestDatabases.NewName("FencingBeforeWrite");
        var stalledHelper = CreateHelper(databaseName, "holder-1", TimeProvider.System);
        var hookedHelper = new HookedMongoHelper(stalledHelper);
        var reached = NewGate();
        var proceed = NewGate();

        // The runner stalls right before the ownership write, after all of its client-side checks have passed.
        hookedHelper.BeforeEnsureHeld = async (_, token) =>
        {
            reached.TrySetResult();
            await proceed.Task.WaitAsync(token);
        };

        var takeoverHelper = CreateHelper(databaseName, "holder-2", TimeProvider.System);
        var stalledMigration = new FencingTestMigration("stalled-runner");
        var takeoverMigration = new FencingTestMigration("taking-over-runner");
        var stalledLogger = new NUnitTestLogger<MongoMigrationRunner>(captureMessages: true);
        var stalledRunner = CreateRunner(hookedHelper, stalledMigration, stalledLogger, TimeProvider.System);
        var takeoverRunner = CreateRunner(takeoverHelper, takeoverMigration, new NUnitTestLogger<MongoMigrationRunner>(), TimeProvider.System);
        using var testCancellation = new CancellationTokenSource(OverallTimeout);
        var cancellationToken = testCancellation.Token;
        Task<MongoMigrationResult>? stalledTask = null;
        Task<MongoMigrationResult>? takeoverTask = null;

        try
        {
            // Act
            stalledTask = stalledRunner.RunMigrationsAsync(cancellationToken);
            await reached.Task.WaitAsync(TestTimeout, cancellationToken);
            await stalledHelper.Database.GetCollection<MongoLockDocument>("_locks")
                               .UpdateOneAsync(Builders<MongoLockDocument>.Filter.Eq(x => x.Id, Chaos.Mongo.MongoDefaults.MigrationLockName),
                                               Builders<MongoLockDocument>.Update.Set(x => x.LeaseUntilUtc, DateTime.UtcNow.AddMinutes(-1)),
                                               cancellationToken: cancellationToken);
            takeoverTask = takeoverRunner.RunMigrationsAsync(cancellationToken);
            var takeoverResult = await takeoverTask.WaitAsync(TestTimeout, cancellationToken);
            proceed.SetResult();
            var act = async () => await stalledTask.WaitAsync(TestTimeout, cancellationToken);

            // Assert
            await act.Should().ThrowAsync<OperationCanceledException>();
            takeoverResult.AppliedMigrationIds.Should().Equal(takeoverMigration.Id);
            stalledMigration.ApplyCallCount.Should().Be(0);
            takeoverMigration.ApplyCallCount.Should().Be(1);
            (await FindDocumentsAsync(stalledHelper)).Should().ContainSingle().Which.AppliedBy.Should().Be("taking-over-runner");
            (await FindHistoryAsync(stalledHelper)).Should().ContainSingle();
            stalledLogger.LogMessages.Should().Contain(message => message.LogLevel == LogLevel.Warning && message.Message.Contains("no longer held"));
        }
        finally
        {
            proceed.TrySetResult();
            await CancelAndDrainAsync(testCancellation, stalledTask, takeoverTask);
        }
    }

    private static MongoMigrationRunner CreateRunner(IMongoHelper helper,
                                                     IMongoMigration migration,
                                                     ILogger<MongoMigrationRunner> logger,
                                                     TimeProvider timeProvider)
    {
        var options = Options.Create(new MongoOptions
        {
            MigrationLockLeaseTime = TimeSpan.FromMinutes(10),
            MigrationLockWaitTimeout = TestTimeout,
            UseTransactionsForMigrationsIfAvailable = true
        });

        return new MongoMigrationRunner([migration], helper, options, logger, timeProvider);
    }

    private static async Task<List<FencingTestDocument>> FindDocumentsAsync(IMongoHelper helper) =>
        await helper.Database.GetCollection<FencingTestDocument>(FencingTestMigration.CollectionName).Find(FilterDefinition<FencingTestDocument>.Empty).ToListAsync();

    private static async Task<List<MongoMigrationHistoryItem>> FindHistoryAsync(IMongoHelper helper) =>
        await helper.Database.GetCollection<MongoMigrationHistoryItem>("_migrations").Find(FilterDefinition<MongoMigrationHistoryItem>.Empty).ToListAsync();

    private static async Task<MongoLockDocument> FindLockAsync(IMongoHelper helper, CancellationToken cancellationToken) =>
        await helper.Database.GetCollection<MongoLockDocument>("_locks").Find(x => x.Id == Chaos.Mongo.MongoDefaults.MigrationLockName).SingleAsync(cancellationToken);

    /// <summary>
    /// Stops whatever a failed test left running: cancels the runners and their polling I/O, then waits a bounded time
    /// for them to finish, so no runner or renewal loop outlives its test.
    /// </summary>
    private static async Task CancelAndDrainAsync(CancellationTokenSource testCancellation, params Task?[] runningTasks)
    {
        await testCancellation.CancelAsync();

        var running = runningTasks.OfType<Task>().ToArray();
        await Task.WhenAny(Task.WhenAll(running), Task.Delay(DrainTimeout));
    }

    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Waits until the server lists an active <paramref name="command"/> on the collection from the client named
    /// <paramref name="appName"/>. Matches only on fields that do not differ between the supported server versions. The
    /// <paramref name="cancellationToken"/> bounds the polling I/O as well as the wait.
    /// </summary>
    private static async Task WaitForActiveOperationAsync(IMongoHelper helper, String appName, String command, String databaseName, String collectionName,
                                                          CancellationToken cancellationToken)
    {
        var admin = helper.Client.GetDatabase("admin");

        // Read raw: an operation that waits for a lock lists "waitingForLock" twice, which BsonDocument rejects.
        var pipeline = PipelineDefinition<NoPipelineInput, RawBsonDocument>.Create([new BsonDocument("$currentOp", new BsonDocument("allUsers", true))]);
        var expectedNamespace = $"{databaseName}.{collectionName}";
        var deadline = DateTime.UtcNow + TestTimeout;
        List<RawBsonDocument> operations;

        do
        {
            operations = await (await admin.AggregateAsync(pipeline, cancellationToken: cancellationToken)).ToListAsync(cancellationToken);
            if (operations.Any(operation => operation.GetValue("appName", BsonNull.Value) == appName &&
                                            operation.GetValue("active", BsonBoolean.False).ToBoolean() &&
                                            operation.GetValue("ns", BsonNull.Value) == expectedNamespace &&
                                            operation.GetValue("command", BsonNull.Value) is BsonDocument commandDocument &&
                                            commandDocument.Contains(command)))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        } while (DateTime.UtcNow < deadline);

        Assert.Fail($"No active {command} on {expectedNamespace} from client '{appName}' was listed by $currentOp. " +
                    $"Operations listed for this client: {operations.Count(x => x.GetValue("appName", BsonNull.Value) == appName)}.");
    }

    private MongoHelper CreateHelper(String databaseName, String holderId, TimeProvider timeProvider, String? appName = null)
    {
        var urlBuilder = new MongoUrlBuilder(_container.GetConnectionString());
        if (appName is not null)
        {
            urlBuilder.ApplicationName = appName;
        }

        var services = new ServiceCollection();
        services.AddSingleton(timeProvider);

        return (MongoHelper)services
                            .AddMongo(urlBuilder.ToMongoUrl(), configure: options =>
                            {
                                options.DefaultDatabase = databaseName;
                                options.HolderId = holderId;
                            })
                            .Services
                            .BuildServiceProvider()
                            .GetRequiredService<IMongoHelper>();
    }
}

internal class FencingTestDocument
{
    public String AppliedBy { get; init; } = String.Empty;

    [MongoDB.Bson.Serialization.Attributes.BsonId]
    public ObjectId Id { get; init; }
}

/// <summary>
/// Writes one document attributed to its runner through the supplied session, then runs an optional step.
/// </summary>
internal sealed class FencingTestMigration : IMongoMigration
{
    public const String CollectionName = "FencingTestDocuments";
    private readonly Func<IClientSessionHandle, CancellationToken, Task>? _afterWrite;
    private readonly String _appliedBy;

    public FencingTestMigration(String appliedBy, Func<IClientSessionHandle, CancellationToken, Task>? afterWrite = null)
    {
        _appliedBy = appliedBy;
        _afterWrite = afterWrite;
    }

    public Int32 ApplyCallCount { get; private set; }

    public String Description => "Writes a document attributed to the runner that applied the migration";

    public String Id => "900_FencingTest";

    public async Task ApplyAsync(IMongoHelper mongoHelper, IClientSessionHandle? session = null, CancellationToken cancellationToken = default)
    {
        ApplyCallCount++;
        ArgumentNullException.ThrowIfNull(session);

        var collection = mongoHelper.Database.GetCollection<FencingTestDocument>(CollectionName);
        await collection.InsertOneAsync(session, new FencingTestDocument
        {
            Id = ObjectId.GenerateNewId(),
            AppliedBy = _appliedBy
        }, cancellationToken: cancellationToken);

        if (_afterWrite is not null)
        {
            await _afterWrite(session, cancellationToken);
        }
    }
}

/// <summary>
/// Wraps the helper's locks to pause or observe the ownership check and to record extensions, delegating all real work to
/// the real lock.
/// </summary>
internal sealed class HookedMongoHelper : IMongoHelper
{
    private readonly MongoHelper _inner;

    public HookedMongoHelper(MongoHelper inner)
    {
        _inner = inner;
    }

    public Func<IClientSessionHandle, Boolean, CancellationToken, Task>? AfterEnsureHeld { get; set; }
    public Func<IClientSessionHandle, CancellationToken, Task>? BeforeEnsureHeld { get; set; }

    public List<HookedLock> Locks { get; } = [];

    public IMongoClient Client => _inner.Client;

    public IMongoDatabase Database => _inner.Database;

    public IMongoCollection<TDocument> GetCollection<TDocument>(MongoCollectionSettings? settings = null) => _inner.GetCollection<TDocument>(settings);

    public async Task<IMongoLock?> TryAcquireLockAsync(String lockName, TimeSpan? leaseTime = null, CancellationToken cancellationToken = default)
    {
        var mongoLock = await _inner.TryAcquireLockAsync(lockName, leaseTime, cancellationToken);
        if (mongoLock is null)
        {
            return null;
        }

        var hookedLock = new HookedLock((MongoLock)mongoLock, this);
        Locks.Add(hookedLock);
        return hookedLock;
    }
}

internal sealed class HookedLock : ITransactionFencedLock
{
    private readonly HookedMongoHelper _helper;
    private readonly MongoLock _inner;

    public HookedLock(MongoLock inner, HookedMongoHelper helper)
    {
        _inner = inner;
        _helper = helper;
    }

    public List<Boolean> ExtensionResults { get; } = [];

    public String Id => _inner.Id;

    public Boolean IsValid => _inner.IsValid;

    public DateTime ValidUntilUtc => _inner.ValidUntilUtc;

    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    public async Task<Boolean> TryExtendAsync(TimeSpan? leaseTime = null, CancellationToken cancellationToken = default)
    {
        var extended = await _inner.TryExtendAsync(leaseTime, cancellationToken);
        ExtensionResults.Add(extended);
        return extended;
    }

    public async Task<Boolean> TryEnsureHeldAsync(IClientSessionHandle session, CancellationToken cancellationToken = default)
    {
        if (_helper.BeforeEnsureHeld is { } before)
        {
            await before(session, cancellationToken);
        }

        var held = await _inner.TryEnsureHeldAsync(session, cancellationToken);

        if (_helper.AfterEnsureHeld is { } after)
        {
            await after(session, held, cancellationToken);
        }

        return held;
    }
}
