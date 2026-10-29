// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Chaos.Mongo.EventStore.Integrity;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Testcontainers.MongoDb;

/// <summary>
/// Correctness tests of the anchor candidate source: no gaps with late commits, at-least-once delivery
/// across restarts, and coverage of every protected aggregate type.
/// </summary>
public class AnchorCandidateSourceTests
{
    private const Int32 BatchSize = 100;

    private readonly List<LedgerStoreContext> _contexts = [];
    private MongoDbContainer _container;

    [Test]
    public async Task AcknowledgeAsync_EarlierBatch_ThrowsArgumentException()
    {
        var context = await CreateInitializedContextAsync();
        var earlier = await context.CandidateSource.ReadBatchAsync(BatchSize, CancellationToken.None);
        await context.CandidateSource.ReadBatchAsync(BatchSize, CancellationToken.None);

        var act = () => context.MongoHelper.ExecuteInTransactionAsync((_, session, ct) => context.CandidateSource.AcknowledgeAsync(session, earlier, ct));

        await act.Should().ThrowAsync<ArgumentException>();
    }


    [Test]
    public async Task AcknowledgeAsync_TransactionAborts_ReturnsCandidatesAgain()
    {
        var context = await CreateInitializedContextAsync();
        var aggregateId = await context.AppendStreamAsync(2);
        var batch = await ReadUntilNonEmptyAsync(context.CandidateSource);

        var act = () => context.MongoHelper.ExecuteInTransactionAsync(async (_, session, ct) =>
        {
            await context.CandidateSource.AcknowledgeAsync(session, batch, ct);
            throw new InvalidOperationException("abort");
        });

        await act.Should().ThrowAsync<InvalidOperationException>();
        var again = await ReadUntilNonEmptyAsync(context.CandidateSource);
        again.Candidates.Should().Contain(candidate => candidate.AggregateId == aggregateId && candidate.Version == 2);
    }

    [Test]
    public async Task AcknowledgeAsync_UnknownBatch_ThrowsArgumentException()
    {
        var context = await CreateInitializedContextAsync();

        var act = () => context.MongoHelper.ExecuteInTransactionAsync((_, session, ct) =>
                                                                          context.CandidateSource.AcknowledgeAsync(session, new AnchorCandidateBatch([], new Object(), false), ct));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Test]
    public async Task ConsumeBatchAsync_AbortedAppend_NeverDeliversItsCandidate()
    {
        var context = await CreateInitializedContextAsync();
        var aborted = Guid.NewGuid();
        var committed = Guid.NewGuid();

        var abortedAppend = () => context.Store.AppendEventsAsync(
            [LedgerStoreContext.CreateEntry(aborted, 1)],
            onBeforeCommit: (_, _, _, _) => throw new InvalidOperationException("abort"));
        await abortedAppend.Should().ThrowAsync<InvalidOperationException>();
        await context.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(committed, 1)]);

        var candidates = await ConsumeUntilAsync(context.CreateConsumer(), consumed => consumed.Any(c => c.AggregateId == committed));

        candidates.Should().NotContain(candidate => candidate.AggregateId == aborted);
    }

    [Test]
    public async Task ConsumeBatchAsync_Appends_DeliversSealedHeadPerStream()
    {
        var context = await CreateInitializedContextAsync();
        var first = await context.AppendStreamAsync(3);
        var second = await context.AppendStreamAsync(1);
        await context.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(second, 2)]);

        var candidates = AnchorCandidateConsumer.Reduce(
            await ConsumeUntilAsync(context.CreateConsumer(), consumed => consumed.Any(c => c.AggregateId == second && c.Version == 2)));

        candidates.Should().HaveCount(2);
        foreach (var candidate in candidates)
        {
            var head = await context.FindEventAsync(candidate.AggregateId, candidate.Version);
            candidate.AggregateType.Should().Be(nameof(LedgerAggregate));
            candidate.Hash.Should().Equal(LedgerStoreContext.ReadIntegrity(head).Hash);
        }

        candidates.Should().Contain(candidate => candidate.AggregateId == first && candidate.Version == 3);
    }

    [Test]
    public async Task ConsumeBatchAsync_LateCommit_IsDeliveredAfterLaterCommitWasAcknowledged()
    {
        var context = await CreateInitializedContextAsync();
        var consumer = context.CreateConsumer();
        var late = Guid.NewGuid();
        var early = Guid.NewGuid();
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The late append keeps its transaction open until the early append is consumed and acknowledged.
        var lateAppend = Task.Run(() => context.Store.AppendEventsAsync(
                                      [LedgerStoreContext.CreateEntry(late, 1)],
                                      onBeforeCommit: async (_, _, _, ct) =>
                                      {
                                          holding.TrySetResult();
                                          await release.Task.WaitAsync(ct);
                                      }));
        await holding.Task;
        await context.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(early, 1)]);
        var beforeLateCommit = await ConsumeUntilAsync(consumer, consumed => consumed.Any(c => c.AggregateId == early));

        release.SetResult();
        await lateAppend;
        var afterLateCommit = await ConsumeUntilAsync(consumer, consumed => consumed.Any(c => c.AggregateId == late));

        beforeLateCommit.Should().NotContain(candidate => candidate.AggregateId == late);
        afterLateCommit.Should().Contain(candidate => candidate.AggregateId == late && candidate.Version == 1);
    }

    [Test]
    public async Task ConsumeBatchAsync_RetroactiveSealing_DeliversSealedHead()
    {
        var databaseName = $"IntegrityTestDb_{Guid.NewGuid():N}";
        var unprotected = await CreateContextAsync(databaseName, false);
        var context = await CreateInitializedContextAsync(databaseName, options => options.SealingChunkSize = 2);
        var aggregateId = await unprotected.AppendStreamAsync(5);

        await context.Sweep.RunPassAsync();
        var candidates = await ConsumeUntilAsync(context.CreateConsumer(), consumed => consumed.Any(c => c.AggregateId == aggregateId && c.Version == 5));

        var head = await context.FindEventAsync(aggregateId, 5);
        candidates.Single(candidate => candidate.AggregateId == aggregateId && candidate.Version == 5)
                  .Hash.Should().Equal(LedgerStoreContext.ReadIntegrity(head).Hash);
    }

    [Test]
    public async Task ConsumeBatchAsync_SeveralProtectedTypes_DeliversHeadOfEveryType()
    {
        var context = await CreateInitializedContextAsync(configureMongo: LedgerStoreContext.AddShipmentStore);
        var shipments = context.ServiceProvider.GetRequiredService<IEventStore<ShipmentAggregate>>();
        var ledger = await context.AppendStreamAsync(2);
        var shipment = Guid.NewGuid();
        await shipments.AppendEventsAsync([ShipmentDispatchedEvent.Create(shipment, 1), ShipmentDispatchedEvent.Create(shipment, 2)]);

        var candidates = AnchorCandidateConsumer.Reduce(
            await ConsumeUntilAsync(context.CreateConsumer(),
                                    consumed => consumed.Any(c => c.AggregateId == ledger && c.Version == 2) &&
                                                consumed.Any(c => c.AggregateId == shipment && c.Version == 2)));

        // Read models and checkpoints live in collections of their own and must not yield candidates.
        candidates.Select(candidate => (candidate.AggregateType, candidate.AggregateId, candidate.Version))
                  .Should().BeEquivalentTo([(nameof(LedgerAggregate), ledger, 2L), (nameof(ShipmentAggregate), shipment, 2L)]);
        candidates.Single(candidate => candidate.AggregateId == ledger)
                  .Hash.Should().Equal(LedgerStoreContext.ReadIntegrity(await context.FindEventAsync(ledger, 2)).Hash);
        candidates.Single(candidate => candidate.AggregateId == shipment)
                  .Hash.Should().Equal(LedgerStoreContext.ReadIntegrity(await context.FindShipmentEventAsync(shipment, 2)).Hash);
    }

    [Test]
    public async Task DiscardUnacknowledgedReads_AfterConsecutiveReads_NextReadsDeliverThemAgain()
    {
        var context = await CreateInitializedContextAsync();
        var first = await context.AppendStreamAsync(1);
        var second = await context.AppendStreamAsync(1);
        Func<IReadOnlyCollection<AnchorCandidate>, Boolean> hasBoth = read => read.Any(c => c.AggregateId == first) && read.Any(c => c.AggregateId == second);
        var (_, discarded) = await ReadConsecutivelyUntilAsync(context.CandidateSource, 1, hasBoth);

        context.CandidateSource.DiscardUnacknowledgedReads();
        var (again, _) = await ReadConsecutivelyUntilAsync(context.CandidateSource, 1, hasBoth);

        again.Should().Contain(candidate => candidate.AggregateId == first).And.Contain(candidate => candidate.AggregateId == second);
        var acknowledgeDiscarded = () => context.MongoHelper.ExecuteInTransactionAsync((_, session, ct) => context.CandidateSource.AcknowledgeAsync(session, discarded, ct));
        await acknowledgeDiscarded.Should().ThrowAsync<ArgumentException>();
    }

    [OneTimeSetUp]
    public async Task GetMongoDbContainer() => _container = await MongoDbTestContainer.StartContainerAsync();

    [Test]
    public async Task ReadBatchAsync_AfterRestart_ReturnsOnlyUnacknowledgedCandidates()
    {
        var databaseName = $"IntegrityTestDb_{Guid.NewGuid():N}";
        var context = await CreateInitializedContextAsync(databaseName);
        var acknowledged = await context.AppendStreamAsync(1);
        await ConsumeUntilAsync(context.CreateConsumer(), consumed => consumed.Any(c => c.AggregateId == acknowledged));
        var unacknowledged = await context.AppendStreamAsync(1);
        await ReadUntilAsync(context.CandidateSource, batch => batch.Candidates.Any(c => c.AggregateId == unacknowledged));

        await context.DisposeAsync();
        _contexts.Remove(context);
        var restarted = await CreateContextAsync(databaseName);
        var candidates = await ConsumeUntilAsync(restarted.CreateConsumer(), consumed => consumed.Any(c => c.AggregateId == unacknowledged));
        candidates.AddRange(await restarted.CreateConsumer().DrainAsync(BatchSize));

        candidates.Should().NotContain(candidate => candidate.AggregateId == acknowledged);
    }

    [Test]
    public async Task ReadBatchAsync_ConsecutiveReads_ContinueAndLastAcknowledgementCoversAll()
    {
        var databaseName = $"IntegrityTestDb_{Guid.NewGuid():N}";
        var context = await CreateInitializedContextAsync(databaseName);
        var first = await context.AppendStreamAsync(2);
        var second = await context.AppendStreamAsync(1);

        var (candidates, last) = await ReadConsecutivelyUntilAsync(context.CandidateSource,
                                                                   1,
                                                                   read => read.Count(c => (c.AggregateId == first) || (c.AggregateId == second)) == 3);
        await context.MongoHelper.ExecuteInTransactionAsync((_, session, ct) => context.CandidateSource.AcknowledgeAsync(session, last, ct));

        await context.DisposeAsync();
        _contexts.Remove(context);
        var restarted = await CreateContextAsync(databaseName);
        var afterRestart = await restarted.CreateConsumer().DrainAsync(BatchSize);

        candidates.Select(candidate => (candidate.AggregateId, candidate.Version))
                  .Should().BeEquivalentTo([(first, 1L), (first, 2L), (second, 1L)]);
        afterRestart.Should().NotContain(candidate => (candidate.AggregateId == first) || (candidate.AggregateId == second));
    }

    [Test]
    public async Task ReadBatchAsync_RestartAfterConsecutiveUnacknowledgedReads_DeliversThemAgain()
    {
        var databaseName = $"IntegrityTestDb_{Guid.NewGuid():N}";
        var context = await CreateInitializedContextAsync(databaseName);
        var first = await context.AppendStreamAsync(1);
        var second = await context.AppendStreamAsync(1);
        await ReadConsecutivelyUntilAsync(context.CandidateSource, 1, read => read.Any(c => c.AggregateId == first) && read.Any(c => c.AggregateId == second));

        await context.DisposeAsync();
        _contexts.Remove(context);
        var restarted = await CreateContextAsync(databaseName);
        var candidates = await ConsumeUntilAsync(restarted.CreateConsumer(),
                                                 consumed => consumed.Any(c => c.AggregateId == first) && consumed.Any(c => c.AggregateId == second));

        candidates.Should().Contain(candidate => candidate.AggregateId == first).And.Contain(candidate => candidate.AggregateId == second);
    }

    [Test]
    public async Task ReadBatchAsync_StreamChange_CarriesClusterTimeAsCommitTime()
    {
        var context = await CreateInitializedContextAsync();
        var before = DateTime.UtcNow.AddSeconds(-5);
        var aggregateId = await context.AppendStreamAsync(1);

        // Reading well after the commit tells the commit time apart from the time of the read.
        await Task.Delay(TimeSpan.FromSeconds(2));
        var readStart = DateTime.UtcNow;
        var batch = await ReadUntilAsync(context.CandidateSource, read => read.Candidates.Any(c => c.AggregateId == aggregateId));

        var committed = batch.Candidates.Single(candidate => candidate.AggregateId == aggregateId).CommittedUtc;
        committed.Kind.Should().Be(DateTimeKind.Utc);
        committed.Should().BeOnOrAfter(before).And.BeBefore(readStart.AddSeconds(-1));
    }

    [Test]
    public async Task RunAsync_UntilCancelled_AcknowledgesCandidatesAndStops()
    {
        var context = await CreateInitializedContextAsync();
        var aggregateId = await context.AppendStreamAsync(1);
        using var cancellation = new CancellationTokenSource();

        var loop = context.CreateConsumer().RunAsync(BatchSize, TimeSpan.FromMilliseconds(10), cancellation.Token);
        await Task.Delay(TimeSpan.FromSeconds(2));
        await cancellation.CancelAsync();
        await loop;

        var batch = await context.CandidateSource.ReadBatchAsync(BatchSize, CancellationToken.None);
        batch.Candidates.Should().NotContain(candidate => candidate.AggregateId == aggregateId);
    }

    [TearDown]
    public async Task TearDown()
    {
        foreach (var context in _contexts)
        {
            await context.DisposeAsync();
        }

        _contexts.Clear();
    }

    private static async Task<List<AnchorCandidate>> ConsumeUntilAsync(AnchorCandidateConsumer consumer,
                                                                       Func<IReadOnlyCollection<AnchorCandidate>, Boolean> isComplete)
    {
        var consumed = new List<AnchorCandidate>();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!isComplete(consumed))
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("The expected candidates were not delivered within the timeout.");
            }

            consumed.AddRange((await consumer.ConsumeBatchAsync(BatchSize)).Candidates);
        }

        return consumed;
    }

    private static async Task<(List<AnchorCandidate> Candidates, AnchorCandidateBatch Last)> ReadConsecutivelyUntilAsync(
        IAnchorCandidateSource source,
        Int32 batchSize,
        Func<IReadOnlyCollection<AnchorCandidate>, Boolean> isComplete)
    {
        var candidates = new List<AnchorCandidate>();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var batch = await source.ReadBatchAsync(batchSize, CancellationToken.None);
            candidates.AddRange(batch.Candidates);
            if (isComplete(candidates))
            {
                return (candidates, batch);
            }

            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("The expected candidates were not read within the timeout.");
            }

            if (batch.Candidates.Count == 0)
            {
                await Task.Delay(20);
            }
        }
    }

    private static async Task<AnchorCandidateBatch> ReadUntilAsync(IAnchorCandidateSource source, Func<AnchorCandidateBatch, Boolean> isComplete)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var batch = await source.ReadBatchAsync(BatchSize, CancellationToken.None);
            if (isComplete(batch))
            {
                return batch;
            }

            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("The expected candidates were not read within the timeout.");
            }

            await Task.Delay(20);
        }
    }

    private static Task<AnchorCandidateBatch> ReadUntilNonEmptyAsync(IAnchorCandidateSource source)
        => ReadUntilAsync(source, batch => batch.Candidates.Count > 0);

    private async Task<LedgerStoreContext> CreateContextAsync(String? databaseName = null,
                                                              Boolean integrityProtection = true,
                                                              Action<MongoEventStoreOptions<LedgerAggregate>>? configureOptions = null,
                                                              Action<MongoBuilder>? configureMongo = null)
    {
        var context = await LedgerStoreContext.CreateAsync(_container,
                                                           integrityProtection,
                                                           databaseName: databaseName,
                                                           configureOptions: configureOptions,
                                                           anchorCandidates: true,
                                                           configureMongo: configureMongo);
        _contexts.Add(context);
        return context;
    }

    /// <summary>
    /// Creates a context whose source has consumed everything that existed before, so a change stream has
    /// completed its initial scan and follows new commits.
    /// </summary>
    private async Task<LedgerStoreContext> CreateInitializedContextAsync(String? databaseName = null,
                                                                         Action<MongoEventStoreOptions<LedgerAggregate>>? configureOptions = null,
                                                                         Action<MongoBuilder>? configureMongo = null)
    {
        var context = await CreateContextAsync(databaseName, true, configureOptions, configureMongo);
        await context.CreateConsumer().DrainAsync(BatchSize);
        return context;
    }
}
