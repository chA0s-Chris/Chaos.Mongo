// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Chaos.Mongo.EventStore.Integrity;
using Chaos.Mongo.EventStore.Tests.Integrity;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;
using NUnit.Framework;
using System.Runtime.CompilerServices;
using Testcontainers.MongoDb;

/// <summary>
/// The operational surface of anchoring: module-level configuration, the hosted run, the completion hook, the delay
/// alert and the sealing-anchor lookup.
/// </summary>
public class IntegrityAnchoringIntegrationTests
{
    private readonly List<LedgerStoreContext> _contexts = [];
    private readonly List<TestTimestampAuthority> _testAuthorities = [];
    private MongoDbContainer _container;

    [Test]
    public async Task FindAsync_AnchorAwaitingTimestamps_ReturnsNoneUntilItCompletes()
    {
        var testAuthority = CreateTestAuthority();
        testAuthority.Mode = TestTimestampAuthorityMode.HttpFailure;
        var context = await CreateContextAsync(anchoring => anchoring.WithTimestampAuthority(_ => testAuthority.CreateAuthority()));
        var store = GetStore(context);
        var run = context.ServiceProvider.GetRequiredService<AnchoringRun>();
        var lookup = context.ServiceProvider.GetRequiredService<SealingAnchorLookup>();
        var stream = await context.AppendStreamAsync(1);
        await RunUntilAsync(run, result => Task.FromResult(result.Outcome == AnchoringRunOutcome.AwaitingTimestamps));

        var whileAwaiting = await lookup.FindAsync(nameof(LedgerAggregate), stream, 1);
        testAuthority.Mode = TestTimestampAuthorityMode.Valid;
        await RunUntilAsync(run, _ => IsAnchoredAsync(store, stream));
        var afterCompletion = await lookup.FindAsync(nameof(LedgerAggregate), stream, 1);

        whileAwaiting.Should().BeNull();
        afterCompletion.Should().NotBeNull();
    }

    [Test]
    public async Task FindAsync_AppendedEvents_ReturnsFirstCompletedAnchorCoveringTheVersion()
    {
        var testAuthority = CreateTestAuthority();
        var context = await CreateContextAsync(anchoring => anchoring.WithTimestampAuthority(_ => testAuthority.CreateAuthority()));
        var store = GetStore(context);
        var run = context.ServiceProvider.GetRequiredService<AnchoringRun>();
        var lookup = context.ServiceProvider.GetRequiredService<SealingAnchorLookup>();
        var stream = await context.AppendStreamAsync(3);
        await RunUntilAsync(run, _ => IsAnchoredAsync(store, stream, 3));

        var forFirst = await lookup.FindAsync(nameof(LedgerAggregate), stream, 1);
        var forHead = await lookup.FindAsync(nameof(LedgerAggregate), stream, 3);
        var unanchored = await lookup.FindAsync(nameof(LedgerAggregate), stream, 4);
        await context.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(stream, 4)]);
        await RunUntilAsync(run, _ => IsAnchoredAsync(store, stream, 4));
        var forAppended = await lookup.FindAsync(nameof(LedgerAggregate), stream, 4);
        var forFirstAfterwards = await lookup.FindAsync(nameof(LedgerAggregate), stream, 1);

        forFirst!.Leaf.Version.Should().Be(3);
        forFirst.Anchor.Status.Should().Be(AnchorStatus.Completed);
        forHead!.Anchor.Id.Should().Be(forFirst.Anchor.Id);
        unanchored.Should().BeNull();
        forAppended!.Leaf.Version.Should().Be(4);
        forAppended.Anchor.Sequence.Should().BeGreaterThan(forFirst.Anchor.Sequence);
        forFirstAfterwards!.Anchor.Id.Should().Be(forFirst.Anchor.Id);
        MerkleTree.VerifyInclusion(MerkleTree.HashLeaf(AnchorEncoding.EncodeLeaf(new AnchorCandidate(forAppended.Leaf.AggregateType,
                                                                                                     forAppended.Leaf.AggregateId,
                                                                                                     forAppended.Leaf.Version,
                                                                                                     forAppended.Leaf.Hash,
                                                                                                     default))),
                                   forAppended.Leaf.Index,
                                   forAppended.Anchor.LeafCount,
                                   forAppended.Leaf.Proof,
                                   forAppended.Anchor.MerkleRoot)
                  .Should().BeTrue();
    }

    [Test]
    public async Task FindAsync_RetroactivelySealedEvents_ReturnsTheSealingAnchor()
    {
        var databaseName = $"IntegrityTestDb_{Guid.NewGuid():N}";
        var unprotected = await LedgerStoreContext.CreateAsync(_container, false, databaseName: databaseName);
        _contexts.Add(unprotected);
        var stream = await unprotected.AppendStreamAsync(4);
        var testAuthority = CreateTestAuthority();
        var context = await CreateContextAsync(anchoring => anchoring.WithTimestampAuthority(_ => testAuthority.CreateAuthority()), databaseName);
        var store = GetStore(context);
        var run = context.ServiceProvider.GetRequiredService<AnchoringRun>();
        var lookup = context.ServiceProvider.GetRequiredService<SealingAnchorLookup>();

        var beforeSealing = await lookup.FindAsync(nameof(LedgerAggregate), stream, 2);
        await context.Sweep.RunPassAsync();
        await RunUntilAsync(run, _ => IsAnchoredAsync(store, stream, 4));
        var afterSealing = await lookup.FindAsync(nameof(LedgerAggregate), stream, 2);

        beforeSealing.Should().BeNull();
        afterSealing!.Leaf.Version.Should().Be(4);
        afterSealing.Leaf.Hash.Should().Equal(LedgerStoreContext.ReadIntegrity(await context.FindEventAsync(stream, 4)).Hash);
    }

    [OneTimeSetUp]
    public async Task GetMongoDbContainer() => _container = await MongoDbTestContainer.StartContainerAsync();

    [Test]
    public async Task NotifyAsync_LockLostDuringHook_CancelsTheHookAndKeepsTheAnchorUnnotified()
    {
        var testAuthority = CreateTestAuthority();
        var context = await CreateContextAsync(anchoring => anchoring.WithTimestampAuthority(_ => testAuthority.CreateAuthority()));
        var store = GetStore(context);
        var run = context.ServiceProvider.GetRequiredService<AnchoringRun>();
        var stream = await context.AppendStreamAsync(1);
        await RunUntilAsync(run, _ => IsAnchoredAsync(store, stream));
        var hookCancelled = new TaskCompletionSource<Boolean>(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifier = new AnchorCompletionNotifier(context.MongoHelper,
                                                    context.ServiceProvider.GetRequiredService<IntegrityAnchoringOptions>(),
                                                    context.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
                                                    async (_, _, ct) =>
                                                    {
                                                        try
                                                        {
                                                            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                                                        }
                                                        catch (OperationCanceledException)
                                                        {
                                                            hookCancelled.TrySetResult(ct.IsCancellationRequested);
                                                            throw;
                                                        }
                                                    },
                                                    TimeProvider.System,
                                                    NullLogger<AnchorCompletionNotifier>.Instance)
        {
            LeaseCheckInterval = TimeSpan.FromMilliseconds(50)
        };
        var lockChecks = 0;

        // The check before the hook succeeds; the first extension while the hook runs fails.
        var completed = await notifier.NotifyAsync(_ => Task.FromResult(++lockChecks == 1), CancellationToken.None);

        completed.Should().BeFalse();
        (await hookCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        (await store.FindFirstUnnotifiedAsync(CancellationToken.None))!.Sequence.Should().Be(1);
    }

    [Test]
    public async Task NotifyAsync_LockLost_StopsBeforeTheNextAnchor()
    {
        var testAuthority = CreateTestAuthority();
        var context = await CreateContextAsync(anchoring => anchoring.WithTimestampAuthority(_ => testAuthority.CreateAuthority()));
        var store = GetStore(context);
        var run = context.ServiceProvider.GetRequiredService<AnchoringRun>();
        var first = await context.AppendStreamAsync(1);
        await RunUntilAsync(run, _ => IsAnchoredAsync(store, first));
        var second = await context.AppendStreamAsync(1);
        await RunUntilAsync(run, _ => IsAnchoredAsync(store, second));
        var notified = new List<Int64>();
        var notifier = new AnchorCompletionNotifier(context.MongoHelper,
                                                    context.ServiceProvider.GetRequiredService<IntegrityAnchoringOptions>(),
                                                    context.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
                                                    (_, anchor, _) =>
                                                    {
                                                        notified.Add(anchor.Sequence);
                                                        return Task.CompletedTask;
                                                    },
                                                    TimeProvider.System,
                                                    NullLogger<AnchorCompletionNotifier>.Instance);
        var lockChecks = 0;

        var completed = await notifier.NotifyAsync(_ => Task.FromResult(++lockChecks == 1), CancellationToken.None);

        completed.Should().BeFalse();
        notified.Should().Equal(1);
        (await store.FindFirstUnnotifiedAsync(CancellationToken.None))!.Sequence.Should().Be(2);
    }

    [Test]
    public async Task RunAsync_CompletedAnchors_NotifiesHookInOrderAtLeastOnceWithoutBlockingAnchoring()
    {
        var notified = new List<Int64>();
        var attempts = new List<Int64>();
        var leafCounts = new List<(Int32 Streamed, Int32 Expected)>();
        var blockSecond = new StrongBox<Boolean>(false);
        var testAuthority = CreateTestAuthority();
        var context = await CreateContextAsync(anchoring => anchoring.WithTimestampAuthority(_ => testAuthority.CreateAuthority())
                                                                     .OnAnchorCompleted(async (services, anchor, ct) =>
                                                                     {
                                                                         attempts.Add(anchor.Sequence);
                                                                         if (blockSecond.Value && anchor.Sequence == 2)
                                                                         {
                                                                             throw new InvalidOperationException("The hook is unavailable.");
                                                                         }

                                                                         var streamed = 0;
                                                                         await foreach (var _ in services.GetRequiredService<AnchorLeafReader>()
                                                                                                         .ReadLeavesAsync(anchor.Id, ct))
                                                                         {
                                                                             streamed++;
                                                                         }

                                                                         leafCounts.Add((streamed, anchor.LeafCount));
                                                                         notified.Add(anchor.Sequence);
                                                                     }));
        var store = GetStore(context);
        var run = context.ServiceProvider.GetRequiredService<AnchoringRun>();
        var first = await context.AppendStreamAsync(1);
        await RunUntilAsync(run, _ => IsAnchoredAsync(store, first));

        blockSecond.Value = true;
        var second = await context.AppendStreamAsync(1);
        await RunUntilAsync(run, _ => IsAnchoredAsync(store, second));
        var attemptsBeforeRun = attempts.Count(sequence => sequence == 2);
        await run.RunAsync();
        var attemptsPerRun = attempts.Count(sequence => sequence == 2) - attemptsBeforeRun;
        var third = await context.AppendStreamAsync(1);
        await RunUntilAsync(run, _ => IsAnchoredAsync(store, third));
        var notifiedWhileBlocked = notified.ToList();

        blockSecond.Value = false;
        await run.RunAsync();

        notifiedWhileBlocked.Should().Equal(1);
        notified.Should().Equal(1, 2, 3);
        attempts.Count(sequence => sequence == 2).Should().BeGreaterThan(1);
        attemptsPerRun.Should().Be(1, "a failing hook is attempted once per run and retried on the next run");
        leafCounts.Should().OnlyContain(count => count.Streamed == count.Expected);
        (await store.Anchors.CountDocumentsAsync(new BsonDocument("Notified", false))).Should().Be(0);
    }

    [Test]
    public async Task RunAsync_CompletionHook_RunsEachCallInItsOwnScope()
    {
        var probes = new List<ScopedProbe>();
        var testAuthority = CreateTestAuthority();
        var context = await CreateContextAsync(anchoring => anchoring.WithTimestampAuthority(_ => testAuthority.CreateAuthority())
                                                                     .OnAnchorCompleted((services, _, _) =>
                                                                     {
                                                                         probes.Add(services.GetRequiredService<ScopedProbe>());
                                                                         return Task.CompletedTask;
                                                                     }),
                                               configureServices: services => services.AddScoped<ScopedProbe>());
        var store = GetStore(context);
        var run = context.ServiceProvider.GetRequiredService<AnchoringRun>();
        var first = await context.AppendStreamAsync(1);
        await RunUntilAsync(run, _ => IsAnchoredAsync(store, first));
        var second = await context.AppendStreamAsync(1);
        await RunUntilAsync(run, _ => IsAnchoredAsync(store, second));

        probes.Should().HaveCount(2);
        probes[0].Should().NotBeSameAs(probes[1]);
        probes.Should().OnlyContain(probe => probe.IsDisposed);
    }

    [Test]
    public async Task RunOnceAsync_FailingRunWhileAnchorAwaitsTimestamps_StillReportsTheDelay()
    {
        using var metrics = new AnchoringMetricsCollector();
        var testAuthority = CreateTestAuthority();
        testAuthority.Mode = TestTimestampAuthorityMode.HttpFailure;
        var context = await CreateContextAsync(anchoring => anchoring.WithTimestampAuthority(_ => testAuthority.CreateAuthority())
                                                                     .WithDelayThreshold(TimeSpan.FromMilliseconds(1)));
        var service = context.ServiceProvider.GetRequiredService<IntegrityAnchoringService>();
        await context.AppendStreamAsync(1);
        await WaitUntilAsync(async () => (await service.RunOnceAsync(CancellationToken.None))?.Outcome == AnchoringRunOutcome.AwaitingTimestamps);
        context.ServiceProvider.GetRequiredService<AnchoringRun>().PhaseCompleted = (_, _) => throw new InvalidOperationException("The run failed.");
        var exceededBefore = metrics.Measurements.Count(measurement => measurement.Instrument == IntegrityAnchoringDiagnostics.AnchorDelayExceededInstrument);

        var result = await service.RunOnceAsync(CancellationToken.None);

        result.Should().BeNull();
        metrics.Measurements.Count(measurement => measurement.Instrument == IntegrityAnchoringDiagnostics.AnchorDelayExceededInstrument)
               .Should().Be(exceededBefore + 1);
    }

    [Test]
    public async Task RunOnceAsync_FailingRun_LogsErrorForTheNextRunToRetry()
    {
        var logger = new CapturingLogger<IntegrityAnchoringService>();
        var testAuthority = CreateTestAuthority();
        var context = await CreateContextAsync(anchoring => anchoring.WithTimestampAuthority(_ => testAuthority.CreateAuthority()),
                                               configureServices: services => services.AddSingleton<ILogger<IntegrityAnchoringService>>(logger));
        var run = context.ServiceProvider.GetRequiredService<AnchoringRun>();
        run.PhaseCompleted = (_, _) => throw new InvalidOperationException("The run failed.");
        var service = context.ServiceProvider.GetRequiredService<IntegrityAnchoringService>();
        await context.AppendStreamAsync(1);

        // A run that reads candidates reaches the failing phase callback; a failed run returns no result.
        await WaitUntilAsync(async () => await service.RunOnceAsync(CancellationToken.None) is null);

        logger.Entries.Should().Contain(entry => entry.Level == LogLevel.Error && entry.Message.Contains("anchoring run failed"));
    }

    [Test]
    public async Task RunOnceAsync_FirstRunFailsAfterReadingCandidates_ReportsTheDelay()
    {
        using var metrics = new AnchoringMetricsCollector();
        var testAuthority = CreateTestAuthority();
        var context = await CreateContextAsync(anchoring => anchoring.WithTimestampAuthority(_ => testAuthority.CreateAuthority())
                                                                     .WithDelayThreshold(TimeSpan.FromMilliseconds(1)));
        var service = context.ServiceProvider.GetRequiredService<IntegrityAnchoringService>();
        context.ServiceProvider.GetRequiredService<AnchoringRun>().PhaseCompleted = (phase, _) =>
            phase == AnchoringPhase.CandidatesRead ? throw new InvalidOperationException("The run failed.") : Task.CompletedTask;
        await context.AppendStreamAsync(1);

        // Runs before the change stream delivers the candidate read nothing and report nothing.
        await WaitUntilAsync(async () =>
        {
            await service.RunOnceAsync(CancellationToken.None);
            return metrics.Measurements.Any(measurement => measurement.Instrument == IntegrityAnchoringDiagnostics.AnchorDelayExceededInstrument);
        });

        (await GetStore(context).Anchors.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty)).Should().Be(0, "the run fails before its commit point");
    }

    [Test]
    public async Task RunOnceAsync_OldestCandidateAboveThreshold_ReportsDelayThroughLogAndMetric()
    {
        var logger = new CapturingLogger<IntegrityAnchoringService>();
        using var metrics = new AnchoringMetricsCollector();
        var testAuthority = CreateTestAuthority();
        var context = await CreateContextAsync(anchoring => anchoring.WithTimestampAuthority(_ => testAuthority.CreateAuthority())
                                                                     .WithDelayThreshold(TimeSpan.FromMilliseconds(1)),
                                               configureServices: services => services.AddSingleton<ILogger<IntegrityAnchoringService>>(logger));
        var service = context.ServiceProvider.GetRequiredService<IntegrityAnchoringService>();
        await context.AppendStreamAsync(1);

        await WaitUntilAsync(async () => (await service.RunOnceAsync(CancellationToken.None))?.OldestCandidateUtc is not null);

        logger.Entries.Should().Contain(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("above the anchor delay threshold"));
        metrics.Measurements.Should().Contain(measurement => measurement.Instrument == IntegrityAnchoringDiagnostics.AnchorDelayExceededInstrument &&
                                                             measurement.Value > 0);
        metrics.Measurements.Should().Contain(measurement => measurement.Instrument == IntegrityAnchoringDiagnostics.AnchorDelayInstrument);
    }

    [Test]
    public async Task RunOnceAsync_OldestCandidateBelowThreshold_RecordsDelayWithoutAlert()
    {
        var logger = new CapturingLogger<IntegrityAnchoringService>();
        using var metrics = new AnchoringMetricsCollector();
        var testAuthority = CreateTestAuthority();
        var context = await CreateContextAsync(anchoring => anchoring.WithTimestampAuthority(_ => testAuthority.CreateAuthority()),
                                               configureServices: services => services.AddSingleton<ILogger<IntegrityAnchoringService>>(logger));
        var service = context.ServiceProvider.GetRequiredService<IntegrityAnchoringService>();
        await context.AppendStreamAsync(1);

        await WaitUntilAsync(async () => (await service.RunOnceAsync(CancellationToken.None))?.OldestCandidateUtc is not null);

        logger.Entries.Should().NotContain(entry => entry.Level == LogLevel.Warning);
        metrics.Measurements.Should().Contain(measurement => measurement.Instrument == IntegrityAnchoringDiagnostics.AnchorDelayInstrument);
        metrics.Measurements.Should().NotContain(measurement => measurement.Instrument == IntegrityAnchoringDiagnostics.AnchorDelayExceededInstrument);
    }

    [Test]
    public async Task StartedAsync_AfterStartup_RunsAtTheConfiguredInterval()
    {
        var interval = TimeSpan.FromMinutes(10);
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var testAuthority = CreateTestAuthority();
        var context = await CreateContextAsync(anchoring => anchoring.WithTimestampAuthority(_ => testAuthority.CreateAuthority()).WithInterval(interval),
                                               timeProvider: timeProvider,
                                               configureServices: services => services.AddLogging());
        var store = GetStore(context);
        var stateCollection = context.MongoHelper.Database.GetCollection<BsonDocument>(
            context.ServiceProvider.GetRequiredService<IntegrityAnchoringOptions>().ChangeStreamStateCollectionName);
        var hostedService = context.ServiceProvider.GetServices<IHostedService>().OfType<IntegrityAnchoringHostedService>().Single();

        await hostedService.StartAsync(CancellationToken.None);
        await hostedService.StartedAsync(CancellationToken.None);
        try
        {
            // The run at startup acknowledges the empty recovery scan of the new database.
            await WaitUntilAsync(async () => await stateCollection.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty) > 0);
            var stream = await context.AppendStreamAsync(1);
            await Task.Delay(TimeSpan.FromSeconds(1));
            var anchoredBeforeInterval = await IsAnchoredAsync(store, stream);

            // Advancing only after a run had time to finish keeps the clock from expiring that run's lock lease.
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!await IsAnchoredAsync(store, stream))
            {
                if (DateTime.UtcNow > deadline)
                {
                    Assert.Fail("The hosted run did not anchor the stream within the timeout.");
                }

                timeProvider.Advance(interval);
                await WaitUntilAsync(() => IsAnchoredAsync(store, stream), TimeSpan.FromSeconds(3), false);
            }

            anchoredBeforeInterval.Should().BeFalse();
        }
        finally
        {
            await hostedService.StoppingAsync(CancellationToken.None);
            await hostedService.StopAsync(CancellationToken.None);
            await hostedService.StoppedAsync(CancellationToken.None);
        }
    }

    [TearDown]
    public async Task TearDown()
    {
        foreach (var context in _contexts)
        {
            await context.DisposeAsync();
        }

        foreach (var testAuthority in _testAuthorities)
        {
            testAuthority.Dispose();
        }

        _contexts.Clear();
        _testAuthorities.Clear();
    }

    private static AnchorStore GetStore(LedgerStoreContext context)
        => new(context.MongoHelper, context.ServiceProvider.GetRequiredService<IntegrityAnchoringOptions>());

    private static async Task<Boolean> IsAnchoredAsync(AnchorStore store, Guid stream, Int64 version = 1)
    {
        var anchors = (await store.Anchors.Find(new BsonDocument(AnchorStore.StatusField, nameof(AnchorStatus.Completed))).ToListAsync())
            .Select(AnchorStore.ReadAnchor);
        foreach (var anchor in anchors)
        {
            var leaves = await store.ReadLeavesAsync(anchor.Id, CancellationToken.None);
            if (leaves.Any(leaf => leaf.AggregateId == stream && leaf.Version >= version))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task RunUntilAsync(AnchoringRun run, Func<AnchoringRunResult, Task<Boolean>> isDone)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await isDone(await run.RunAsync()))
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("The expected anchoring state was not reached within the timeout.");
            }

            await Task.Delay(50);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<Boolean>> condition, TimeSpan? timeout = null, Boolean failOnTimeout = true)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                if (failOnTimeout)
                {
                    Assert.Fail("The expected state was not reached within the timeout.");
                }

                return;
            }

            await Task.Delay(100);
        }
    }

    private async Task<LedgerStoreContext> CreateContextAsync(Action<IntegrityAnchoringBuilder> configure,
                                                              String? databaseName = null,
                                                              TimeProvider? timeProvider = null,
                                                              Action<IServiceCollection>? configureServices = null)
    {
        var context = await LedgerStoreContext.CreateAsync(_container,
                                                           databaseName: databaseName,
                                                           timeProvider: timeProvider,
                                                           configureMongo: mongo =>
                                                           {
                                                               configureServices?.Invoke(mongo.Services);
                                                               mongo.WithIntegrityAnchoring(configure);
                                                           });
        _contexts.Add(context);
        return context;
    }

    private TestTimestampAuthority CreateTestAuthority()
    {
        var testAuthority = new TestTimestampAuthority();
        _testAuthorities.Add(testAuthority);
        return testAuthority;
    }
}
