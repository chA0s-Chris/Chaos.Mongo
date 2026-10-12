// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Chaos.Mongo.EventStore.Integrity;
using Chaos.Mongo.EventStore.Tests.Integrity;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using NUnit.Framework;
using System.Security.Cryptography.Pkcs;
using Testcontainers.MongoDb;

public class AnchoringRunTests
{
    private static readonly Comparer<Byte[]> LeafComparer = Comparer<Byte[]>.Create((x, y) => x.AsSpan().SequenceCompareTo(y));
    private readonly List<LedgerStoreContext> _contexts = [];
    private readonly List<TestTimestampAuthority> _testAuthorities = [];
    private MongoDbContainer _container;

    [Test]
    public async Task Constructor_InvalidConfiguration_ThrowsArgumentException()
    {
        var context = await CreateContextAsync(consumeExisting: false);
        var options = GetOptions(context);
        var authority = CreateTestAuthority().CreateAuthority(name: "a");
        var other = CreateTestAuthority().CreateAuthority(name: "b");

        FluentActions.Invoking(() => CreateRun(context)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => CreateRun(context, authority, CreateTestAuthority().CreateAuthority(name: "a"))).Should().Throw<ArgumentException>();
        options.MinimumTimestampTokens = 3;
        FluentActions.Invoking(() => CreateRun(context, authority, other)).Should().Throw<ArgumentException>();
        options.MinimumTimestampTokens = 0;
        FluentActions.Invoking(() => CreateRun(context, authority)).Should().Throw<ArgumentException>();
        options.MinimumTimestampTokens = 1;
        options.MaxLeavesPerAnchor = 0;
        FluentActions.Invoking(() => CreateRun(context, authority)).Should().Throw<ArgumentException>();
        options.MaxLeavesPerAnchor = 1;
        options.CandidateReadBatchSize = 0;
        FluentActions.Invoking(() => CreateRun(context, authority)).Should().Throw<ArgumentException>();
    }

    [OneTimeSetUp]
    public async Task GetMongoDbContainer() => _container = await MongoDbTestContainer.StartContainerAsync();

    [Test]
    public async Task RunAsync_AttemptDiscardedBeforeCommit_FailsAndLeavesCandidatesForTheNextRun()
    {
        var context = await CreateContextAsync();
        var store = GetStore(context);
        var stream = await context.AppendStreamAsync(1);
        var run = CreateRun(context, CreateTestAuthority().CreateAuthority());

        // A later run whose lock lease outlived this one discards the attempt before it commits.
        run.PhaseCompleted = (phase, ct) => phase == AnchoringPhase.LeavesWritten ? store.DiscardPreparingAsync(DateTime.UtcNow, ct) : Task.CompletedTask;
        await RunUntilThrowsAsync<InvalidOperationException>(run);
        run.PhaseCompleted = null;
        await RunUntilAsync(run, _ => IsAnchoredAsync(store, stream));

        var anchors = await ReadCommittedAnchorsAsync(store);
        anchors.Should().ContainSingle().Which.Sequence.Should().Be(1);
        (await ReadLeavesAsync(store, anchors)).Should().ContainSingle(leaf => leaf.AggregateId == stream);
        (await store.Anchors.CountDocumentsAsync(new BsonDocument(AnchorStore.StatusField, nameof(AnchorStatus.Discarded)))).Should().Be(1);
        (await store.Leaves.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty)).Should().Be(anchors[0].LeafCount);
    }

    [Test]
    public async Task RunAsync_ConsecutiveAnchors_AreChainedThroughTheirDigests()
    {
        var context = await CreateContextAsync();
        var store = GetStore(context);
        var run = CreateRun(context, CreateTestAuthority().CreateAuthority());
        var first = await context.AppendStreamAsync(1);
        await RunUntilAsync(run, _ => IsAnchoredAsync(store, first));
        var second = await context.AppendStreamAsync(1);
        await RunUntilAsync(run, _ => IsAnchoredAsync(store, second));

        var anchors = await ReadCommittedAnchorsAsync(store);

        anchors.Select(anchor => anchor.Sequence).Should().Equal(1, 2);
        anchors[0].PreviousDigest.Should().Equal(new Byte[32]);
        anchors[1].PreviousDigest.Should().Equal(anchors[0].AnchorDigest);
        anchors[1].PeriodStartUtc.Should().Be(anchors[0].PeriodEndUtc);
        foreach (var anchor in anchors)
        {
            await AssertVerifiableAsync(store, anchor);
        }
    }

    [TestCase(nameof(AnchoringPhase.PendingAnchorStamped))]
    [TestCase(nameof(AnchoringPhase.CandidatesRead))]
    [TestCase(nameof(AnchoringPhase.LeavesWritten))]
    [TestCase(nameof(AnchoringPhase.Committed))]
    [TestCase(nameof(AnchoringPhase.Stamped))]
    public async Task RunAsync_CrashAfterPhase_NewRunnerCompletesWithoutLosingOrDuplicatingCandidates(String phaseName)
    {
        var phase = Enum.Parse<AnchoringPhase>(phaseName);
        var databaseName = $"IntegrityTestDb_{Guid.NewGuid():N}";
        var context = await CreateContextAsync(databaseName);
        var testAuthority = CreateTestAuthority();
        var streams = new[]
        {
            await context.AppendStreamAsync(2),
            await context.AppendStreamAsync(1)
        };
        if (phase == AnchoringPhase.PendingAnchorStamped)
        {
            testAuthority.Mode = TestTimestampAuthorityMode.HttpFailure;
            await RunUntilAsync(CreateRun(context, testAuthority.CreateAuthority()), AnchoringRunOutcome.AwaitingTimestamps);
            testAuthority.Mode = TestTimestampAuthorityMode.Valid;
        }

        var crashing = CreateRun(context, testAuthority.CreateAuthority());
        crashing.PhaseCompleted = (completed, _) => completed == phase ? throw new SimulatedCrashException() : Task.CompletedTask;
        await RunUntilThrowsAsync<SimulatedCrashException>(crashing);
        await context.DisposeAsync();
        _contexts.Remove(context);

        var restarted = await CreateContextAsync(databaseName, false);
        var store = GetStore(restarted);
        await RunUntilAsync(CreateRun(restarted, testAuthority.CreateAuthority()), _ => IsAnchoredAsync(store, streams));

        var anchors = await ReadCommittedAnchorsAsync(store);
        anchors.Select(anchor => anchor.Sequence).Should().Equal(Enumerable.Range(1, anchors.Count).Select(sequence => (Int64)sequence));
        (await ReadLeavesAsync(store, anchors)).Select(leaf => (leaf.AggregateId, leaf.Version))
                                               .Should().BeEquivalentTo([(streams[0], 2L), (streams[1], 1L)]);
        (await store.Leaves.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty)).Should().Be(anchors.Sum(anchor => anchor.LeafCount));
    }

    [Test]
    public async Task RunAsync_DiscardedAttempts_DeletesTheirLeavesAndRemovesOldAttempts()
    {
        var context = await CreateContextAsync();
        var store = GetStore(context);
        var discarded = Guid.NewGuid();
        var longDiscarded = Guid.NewGuid();
        await store.Anchors.InsertManyAsync([
            new BsonDocument
            {
                { "_id", new BsonBinaryData(discarded, GuidRepresentation.Standard) },
                { AnchorStore.StatusField, nameof(AnchorStatus.Discarded) },
                { "DiscardedUtc", DateTime.UtcNow.AddMinutes(-10) }
            },
            new BsonDocument
            {
                { "_id", new BsonBinaryData(longDiscarded, GuidRepresentation.Standard) },
                { AnchorStore.StatusField, nameof(AnchorStatus.Discarded) },
                { "DiscardedUtc", DateTime.UtcNow - AnchorStore.DiscardedCleanupWindow - TimeSpan.FromHours(1) }
            }
        ]);

        // A superseded run may still write leaves after its attempt was discarded.
        await store.InsertLeavesAsync([
                                          new AnchorLeaf(discarded, 1, 0, nameof(LedgerAggregate), Guid.NewGuid(), 1, new Byte[32], []),
                                          new AnchorLeaf(longDiscarded, 1, 0, nameof(LedgerAggregate), Guid.NewGuid(), 1, new Byte[32], [])
                                      ],
                                      CancellationToken.None);

        var result = await CreateRun(context, CreateTestAuthority().CreateAuthority()).RunAsync();

        result.Outcome.Should().Be(AnchoringRunOutcome.NoCandidates);
        (await store.ReadLeavesAsync(discarded, CancellationToken.None)).Should().BeEmpty();
        (await store.ReadLeavesAsync(longDiscarded, CancellationToken.None)).Should().BeEmpty();
        (await store.Anchors.CountDocumentsAsync(new BsonDocument("_id", new BsonBinaryData(discarded, GuidRepresentation.Standard))))
            .Should().Be(1, "a superseded run may still write leaves of a recently discarded attempt");
        (await store.Anchors.CountDocumentsAsync(new BsonDocument("_id", new BsonBinaryData(longDiscarded, GuidRepresentation.Standard))))
            .Should().Be(0, "attempts discarded long ago are removed together with their leaves");
    }

    [TestCase(nameof(AnchoringPhase.CandidatesRead))]
    [TestCase(nameof(AnchoringPhase.LeavesWritten))]
    public async Task RunAsync_FailureBeforeCommitPoint_NextRunOfTheSameProcessAnchorsTheCandidates(String phaseName)
    {
        var phase = Enum.Parse<AnchoringPhase>(phaseName);
        var context = await CreateContextAsync();
        var store = GetStore(context);
        var streams = new[]
        {
            await context.AppendStreamAsync(1),
            await context.AppendStreamAsync(3)
        };
        var run = CreateRun(context, CreateTestAuthority().CreateAuthority());
        run.PhaseCompleted = (completed, _) => completed == phase ? throw new SimulatedCrashException() : Task.CompletedTask;
        await RunUntilThrowsAsync<SimulatedCrashException>(run);

        run.PhaseCompleted = null;
        await RunUntilAsync(run, _ => IsAnchoredAsync(store, streams));

        (await ReadLeavesAsync(store, await ReadCommittedAnchorsAsync(store))).Select(leaf => (leaf.AggregateId, leaf.Version))
                                                                              .Should().BeEquivalentTo([(streams[0], 1L), (streams[1], 3L)]);
    }

    [Test]
    public async Task RunAsync_LateCommit_IsCoveredByALaterAnchor()
    {
        var context = await CreateContextAsync();
        var store = GetStore(context);
        var run = CreateRun(context, CreateTestAuthority().CreateAuthority());
        var late = Guid.NewGuid();
        var early = Guid.NewGuid();
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The late append keeps its transaction open until the early append is anchored.
        var lateAppend = Task.Run(() => context.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(late, 1)],
                                                                        onBeforeCommit: async (_, _, _, ct) =>
                                                                        {
                                                                            holding.TrySetResult();
                                                                            await release.Task.WaitAsync(ct);
                                                                        }));
        await holding.Task;
        await context.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(early, 1)]);
        await RunUntilAsync(run, _ => IsAnchoredAsync(store, early));
        var anchoredBeforeLateCommit = await ReadLeavesAsync(store, await ReadCommittedAnchorsAsync(store));

        release.SetResult();
        await lateAppend;
        await RunUntilAsync(run, _ => IsAnchoredAsync(store, late));

        anchoredBeforeLateCommit.Should().NotContain(leaf => leaf.AggregateId == late);
        (await ReadLeavesAsync(store, await ReadCommittedAnchorsAsync(store))).Should().ContainSingle(leaf => leaf.AggregateId == late);
    }

    [Test]
    public async Task RunAsync_MaxLeavesReached_LeavesRemainingCandidatesForTheNextAnchor()
    {
        var context = await CreateContextAsync();
        var options = GetOptions(context);
        options.CandidateReadBatchSize = 2;
        options.MaxLeavesPerAnchor = 3;
        var source = new InMemoryAnchorCandidateSource(InMemoryAnchorCandidateSource.CreateCandidates(5), context.MongoHelper.Database);
        var run = CreateRun(context, source, CreateTestAuthority().CreateAuthority());

        var first = await run.RunAsync();
        var second = await run.RunAsync();
        var third = await run.RunAsync();

        // The maximum is checked between batches, so the first anchor exceeds it by less than one batch.
        first.Anchor!.LeafCount.Should().Be(4);
        second.Anchor!.LeafCount.Should().Be(1);
        second.Anchor.Sequence.Should().Be(2);
        third.Outcome.Should().Be(AnchoringRunOutcome.NoCandidates);
    }

    [Test]
    public async Task RunAsync_MinimumNotReached_KeepsAwaitingAndRetriesOnlyTheMissingAuthority()
    {
        var context = await CreateContextAsync();
        var store = GetStore(context);
        GetOptions(context).MinimumTimestampTokens = 2;
        var reliable = CreateTestAuthority();
        var failing = CreateTestAuthority();
        failing.Mode = TestTimestampAuthorityMode.Unreachable;
        var run = CreateRun(context, reliable.CreateAuthority(name: "reliable"), failing.CreateAuthority(name: "failing"));
        var first = await context.AppendStreamAsync(1);

        var awaiting = await RunUntilAsync(run, AnchoringRunOutcome.AwaitingTimestamps);
        var second = await context.AppendStreamAsync(1);
        var retried = await run.RunAsync();

        awaiting.Anchor!.Timestamps.Select(timestamp => timestamp.Authority).Should().Equal("reliable");
        awaiting.OldestCandidateUtc.Should().NotBeNull();
        retried.Outcome.Should().Be(AnchoringRunOutcome.AwaitingTimestamps);
        retried.Anchor!.AuthorityFailures.Should().ContainSingle().Which.Should().Match<AnchorAuthorityFailure>(failure =>
                                                                                                                    failure.Authority == "failing" && failure.FailureCount == 2 &&
                                                                                                                    failure.LastFailure.Contains("timestamp request failed"));
        reliable.Nonces.Should().ContainSingle();
        (await ReadCommittedAnchorsAsync(store)).Should().ContainSingle();

        failing.Mode = TestTimestampAuthorityMode.Valid;
        await RunUntilAsync(run, _ => IsAnchoredAsync(store, first, second));

        var anchors = await ReadCommittedAnchorsAsync(store);
        anchors.Select(anchor => anchor.Sequence).Should().Equal(1, 2);
        anchors[0].Timestamps.Select(timestamp => timestamp.Authority).Should().BeEquivalentTo("reliable", "failing");
        (await store.ReadLeavesAsync(anchors[1].Id, CancellationToken.None)).Should().Contain(leaf => leaf.AggregateId == second);
    }

    [Test]
    public async Task RunAsync_NewCandidates_CompletesAnchorWithOneVerifiableLeafPerStreamHead()
    {
        var context = await CreateContextAsync();
        var store = GetStore(context);
        var streams = new[]
        {
            await context.AppendStreamAsync(3),
            await context.AppendStreamAsync(1),
            await context.AppendStreamAsync(2)
        };

        await RunUntilAsync(CreateRun(context, CreateTestAuthority().CreateAuthority()), _ => IsAnchoredAsync(store, streams));

        var anchors = await ReadCommittedAnchorsAsync(store);
        anchors[0].Sequence.Should().Be(1);
        anchors[0].PreviousDigest.Should().Equal(new Byte[32]);
        foreach (var anchor in anchors)
        {
            anchor.Status.Should().Be(AnchorStatus.Completed);
            anchor.FormatVersion.Should().Be(AnchorEncoding.FormatVersion);
            anchor.HashAlgorithm.Should().Be("SHA-256");
            anchor.Timestamps.Should().ContainSingle().Which.Authority.Should().Be("test-tsa");
            anchor.PeriodEndUtc.Should().BeOnOrAfter(anchor.PeriodStartUtc).And.BeOnOrAfter(anchor.OldestCandidateUtc);
            await AssertVerifiableAsync(store, anchor);
        }

        (await ReadLeavesAsync(store, anchors)).Select(leaf => (leaf.AggregateId, leaf.Version))
                                               .Should().BeEquivalentTo([(streams[0], 3L), (streams[1], 1L), (streams[2], 2L)]);
    }

    [Test]
    public async Task RunAsync_NoCandidates_CreatesNoAnchorAndRequestsNoTimestamp()
    {
        var context = await CreateContextAsync();
        var testAuthority = CreateTestAuthority();

        var result = await CreateRun(context, testAuthority.CreateAuthority()).RunAsync();

        result.Outcome.Should().Be(AnchoringRunOutcome.NoCandidates);
        result.OldestCandidateUtc.Should().BeNull();
        (await GetStore(context).Anchors.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty)).Should().Be(0);
        testAuthority.Nonces.Should().BeEmpty();
    }

    [Test]
    public async Task RunAsync_OneOfSeveralAuthoritiesFails_CompletesAtMinimumAndRecordsTheFailure()
    {
        var context = await CreateContextAsync();
        var store = GetStore(context);
        var failing = CreateTestAuthority();
        failing.Mode = TestTimestampAuthorityMode.HttpFailure;
        var run = CreateRun(context, CreateTestAuthority().CreateAuthority(name: "reliable"), failing.CreateAuthority(name: "failing"));
        await context.AppendStreamAsync(1);

        var result = await RunUntilAsync(run, AnchoringRunOutcome.Completed);

        var anchor = result.Anchor!;
        anchor.Timestamps.Select(timestamp => timestamp.Authority).Should().Equal("reliable");
        anchor.AuthorityFailures.Should().ContainSingle().Which.Should().Match<AnchorAuthorityFailure>(failure =>
                                                                                                           failure.Authority == "failing" && failure.FailureCount == 1 &&
                                                                                                           failure.LastFailure.Contains("500"));
        anchor.CompletedUtc.Should().NotBeNull();
        await AssertVerifiableAsync(store, anchor);
    }

    [Test]
    public async Task RunAsync_SequenceTakenBeforeCommit_FailsWithoutForkingTheChain()
    {
        var context = await CreateContextAsync();
        var store = GetStore(context);
        await context.AppendStreamAsync(1);
        var run = CreateRun(context, CreateTestAuthority().CreateAuthority());
        run.PhaseCompleted = (phase, ct) => phase == AnchoringPhase.LeavesWritten
            ? store.Anchors.InsertOneAsync(new BsonDocument
            {
                { "_id", new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard) },
                { AnchorStore.StatusField, nameof(AnchorStatus.Completed) },
                { AnchorStore.SequenceField, 1L }
            }, cancellationToken: ct)
            : Task.CompletedTask;

        await RunUntilThrowsAsync<InvalidOperationException>(run);

        (await store.Anchors.CountDocumentsAsync(new BsonDocument(AnchorStore.SequenceField, 1L))).Should().Be(1);
        (await store.Anchors.CountDocumentsAsync(new BsonDocument(AnchorStore.StatusField, nameof(AnchorStatus.Preparing)))).Should().Be(1);
    }

    [Test]
    public async Task RunAsync_TreeDataWellBeyond16Megabytes_CreatesAndCompletesAnchor()
    {
        var context = await CreateContextAsync();
        var store = GetStore(context);
        var source = new InMemoryAnchorCandidateSource(InMemoryAnchorCandidateSource.CreateCandidates(50_000), context.MongoHelper.Database);

        var result = await CreateRun(context, source, CreateTestAuthority().CreateAuthority()).RunAsync();

        result.Outcome.Should().Be(AnchoringRunOutcome.Completed);
        var anchor = result.Anchor!;
        anchor.LeafCount.Should().Be(50_000);
        var anchorId = new BsonBinaryData(anchor.Id, GuidRepresentation.Standard);
        var size = await store.Leaves.Aggregate()
                              .Match(new BsonDocument(AnchorStore.AnchorIdField, anchorId))
                              .Group(new BsonDocument
                              {
                                  { "_id", BsonNull.Value },
                                  { "Size", new BsonDocument("$sum", new BsonDocument("$bsonSize", "$$ROOT")) }
                              })
                              .SingleAsync();
        size["Size"].ToInt64().Should().BeGreaterThan(32L * 1024 * 1024);

        var sample = await store.Leaves.Find(new BsonDocument
                                {
                                    { AnchorStore.AnchorIdField, anchorId },
                                    {
                                        "Index", new BsonDocument("$in", new BsonArray
                                        {
                                            0,
                                            24_999,
                                            49_999
                                        })
                                    }
                                })
                                .ToListAsync();
        foreach (var leaf in sample.Select(AnchorStore.ReadLeaf))
        {
            var leafHash = MerkleTree.HashLeaf(AnchorEncoding.EncodeLeaf(new AnchorCandidate(leaf.AggregateType, leaf.AggregateId, leaf.Version, leaf.Hash, default)));
            MerkleTree.VerifyInclusion(leafHash, leaf.Index, anchor.LeafCount, leaf.Proof, anchor.MerkleRoot).Should().BeTrue();
        }
    }

    [Test]
    public async Task RunAsync_WhileAnotherRunHoldsTheLock_IsSkipped()
    {
        var context = await CreateContextAsync();
        var authority = CreateTestAuthority().CreateAuthority();
        var first = CreateRun(context, authority);
        AnchoringRunResult? concurrent = null;
        first.PhaseCompleted = async (phase, ct) =>
        {
            if (phase == AnchoringPhase.CandidatesRead)
            {
                concurrent = await CreateRun(context, authority).RunAsync(ct);
            }
        };
        await context.AppendStreamAsync(1);

        // The callback reports only a run that read candidates.
        await RunUntilAsync(first, _ => Task.FromResult(concurrent is not null));

        concurrent.Should().NotBeNull();
        concurrent!.Outcome.Should().Be(AnchoringRunOutcome.LockUnavailable);
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

    private static async Task AssertVerifiableAsync(AnchorStore store, AnchorRecord anchor)
    {
        var leaves = await store.ReadLeavesAsync(anchor.Id, CancellationToken.None);
        leaves.Should().HaveCount(anchor.LeafCount);
        leaves.Select(leaf => leaf.Index).Should().Equal(Enumerable.Range(0, anchor.LeafCount));
        leaves.Should().OnlyContain(leaf => leaf.AnchorSequence == anchor.Sequence);

        var encoded = leaves.Select(leaf => AnchorEncoding.EncodeLeaf(new AnchorCandidate(leaf.AggregateType, leaf.AggregateId, leaf.Version, leaf.Hash, default)))
                            .ToList();
        encoded.Should().BeInAscendingOrder(LeafComparer);
        for (var index = 0; index < leaves.Count; index++)
        {
            MerkleTree.VerifyInclusion(MerkleTree.HashLeaf(encoded[index]), index, leaves.Count, leaves[index].Proof, anchor.MerkleRoot)
                      .Should().BeTrue();
        }

        AnchorEncoding.ComputeAnchorDigest(anchor.FormatVersion, anchor.Sequence, anchor.PreviousDigest, anchor.MerkleRoot)
                      .Should().Equal(anchor.AnchorDigest);
        foreach (var timestamp in anchor.Timestamps)
        {
            Rfc3161TimestampToken.TryDecode(timestamp.Token, out var token, out _).Should().BeTrue();
            token!.TokenInfo.GetMessageHash().ToArray().Should().Equal(anchor.AnchorDigest);
            timestamp.CertificateChain.Should().HaveCount(2);
            timestamp.CertificateRevocationLists.Should().ContainSingle();
        }
    }

    private static AnchoringRun CreateRun(LedgerStoreContext context, params ITimestampAuthority[] authorities)
        => CreateRun(context, context.CandidateSource, authorities);

    private static AnchoringRun CreateRun(LedgerStoreContext context, IAnchorCandidateSource source, params ITimestampAuthority[] authorities)
        => new(context.MongoHelper, source, authorities, GetOptions(context), TimeProvider.System, NullLogger<AnchoringRun>.Instance);

    private static IntegrityAnchoringOptions GetOptions(LedgerStoreContext context)
        => context.ServiceProvider.GetRequiredService<IntegrityAnchoringOptions>();

    private static AnchorStore GetStore(LedgerStoreContext context) => new(context.MongoHelper, GetOptions(context));

    private static async Task<Boolean> IsAnchoredAsync(AnchorStore store, params Guid[] streams)
    {
        var anchors = await ReadCommittedAnchorsAsync(store);
        if (anchors.Any(anchor => anchor.Status != AnchorStatus.Completed))
        {
            return false;
        }

        var leaves = await ReadLeavesAsync(store, anchors);
        return streams.All(stream => leaves.Any(leaf => leaf.AggregateId == stream));
    }

    private static async Task<List<AnchorRecord>> ReadCommittedAnchorsAsync(AnchorStore store)
        => (await store.Anchors.Find(new BsonDocument(AnchorStore.SequenceField, new BsonDocument("$exists", true)))
                       .Sort(new BsonDocument(AnchorStore.SequenceField, 1))
                       .ToListAsync())
           .Select(AnchorStore.ReadAnchor)
           .ToList();

    private static async Task<List<AnchorLeaf>> ReadLeavesAsync(AnchorStore store, IEnumerable<AnchorRecord> anchors)
    {
        var leaves = new List<AnchorLeaf>();
        foreach (var anchor in anchors)
        {
            leaves.AddRange(await store.ReadLeavesAsync(anchor.Id, CancellationToken.None));
        }

        return leaves;
    }

    /// <summary>
    /// Runs until the condition holds; a run may start before the change stream reports a just-committed append.
    /// </summary>
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

    private static async Task<AnchoringRunResult> RunUntilAsync(AnchoringRun run, AnchoringRunOutcome outcome)
    {
        AnchoringRunResult? last = null;
        await RunUntilAsync(run, result =>
        {
            last = result;
            return Task.FromResult(result.Outcome == outcome);
        });
        return last!;
    }

    /// <summary>
    /// Runs until the run throws <typeparamref name="TException"/>, for example from a phase callback.
    /// </summary>
    private static async Task RunUntilThrowsAsync<TException>(AnchoringRun run) where TException : Exception
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            try
            {
                await run.RunAsync();
            }
            catch (TException)
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"The run did not throw {typeof(TException).Name} within the timeout.");
            }

            await Task.Delay(50);
        }
    }

    private async Task<LedgerStoreContext> CreateContextAsync(String? databaseName = null, Boolean consumeExisting = true)
    {
        var context = await LedgerStoreContext.CreateAsync(_container, databaseName: databaseName, anchorCandidates: true);
        _contexts.Add(context);
        await new IntegrityAnchoringConfigurator(GetOptions(context)).ConfigureAsync(context.MongoHelper);
        if (consumeExisting)
        {
            await context.CreateConsumer().DrainAsync(100);
        }

        return context;
    }

    private TestTimestampAuthority CreateTestAuthority()
    {
        var testAuthority = new TestTimestampAuthority();
        _testAuthorities.Add(testAuthority);
        return testAuthority;
    }
}
