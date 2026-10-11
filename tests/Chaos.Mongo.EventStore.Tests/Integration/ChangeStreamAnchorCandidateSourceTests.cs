// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Chaos.Mongo.EventStore.Integrity;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using NUnit.Framework;
using Testcontainers.MongoDb;

public class ChangeStreamAnchorCandidateSourceTests
{
    private readonly List<LedgerStoreContext> _contexts = [];
    private MongoDbContainer _container;

    [Test]
    public async Task ConsumeBatchAsync_EmptyBatch_PersistsResumeToken()
    {
        var context = await CreateContextAsync();
        var consumer = context.CreateConsumer();
        await consumer.DrainAsync(10);

        var consumed = await consumer.ConsumeBatchAsync(10);

        consumed.Candidates.Should().BeEmpty();
        var state = await ReadStateAsync(context);
        state.Should().NotBeNull();
        state.Contains("ResumeToken").Should().BeTrue();
    }

    [Test]
    public async Task ConsumeBatchAsync_RestartDuringRecoveryAcrossTypes_ContinuesScanInsteadOfRepeatingIt()
    {
        var databaseName = $"IntegrityTestDb_{Guid.NewGuid():N}";
        var context = await CreateContextAsync(databaseName, configureMongo: LedgerStoreContext.AddShipmentStore);
        var shipments = context.ServiceProvider.GetRequiredService<IEventStore<ShipmentAggregate>>();
        var shipmentStreams = new[]
        {
            Guid.NewGuid(),
            Guid.NewGuid()
        };
        foreach (var shipment in shipmentStreams)
        {
            await shipments.AppendEventsAsync([ShipmentDispatchedEvent.Create(shipment, 1)]);
        }

        var ledgerStreams = new[]
        {
            await context.AppendStreamAsync(1),
            await context.AppendStreamAsync(2)
        };
        var consumer = context.CreateConsumer();

        // The ledger type is registered first, so the third batch crosses into the shipment type.
        var beforeRestart = new List<AnchorCandidate>();
        for (var batch = 0; batch < 3; batch++)
        {
            beforeRestart.AddRange((await consumer.ConsumeBatchAsync(1)).Candidates);
        }

        await context.DisposeAsync();
        _contexts.Remove(context);
        var restarted = await CreateContextAsync(databaseName, configureMongo: LedgerStoreContext.AddShipmentStore);
        var afterRestart = await restarted.CreateConsumer().DrainAsync(10);

        beforeRestart.Select(candidate => candidate.AggregateType)
                     .Should().Equal(nameof(LedgerAggregate), nameof(LedgerAggregate), nameof(ShipmentAggregate));
        afterRestart.Should().ContainSingle().Which.AggregateType.Should().Be(nameof(ShipmentAggregate));
        beforeRestart.Concat(afterRestart).Select(candidate => candidate.AggregateId)
                     .Should().BeEquivalentTo(ledgerStreams.Concat(shipmentStreams));
    }

    [Test]
    public async Task ConsumeBatchAsync_RestartDuringRecovery_ContinuesScanInsteadOfRepeatingIt()
    {
        var databaseName = $"IntegrityTestDb_{Guid.NewGuid():N}";
        var context = await CreateContextAsync(databaseName);
        var streams = new[]
        {
            await context.AppendStreamAsync(1),
            await context.AppendStreamAsync(2),
            await context.AppendStreamAsync(3)
        };
        var consumer = context.CreateConsumer();
        var beforeRestart = new List<AnchorCandidate>();
        beforeRestart.AddRange((await consumer.ConsumeBatchAsync(1)).Candidates);
        beforeRestart.AddRange((await consumer.ConsumeBatchAsync(1)).Candidates);

        await context.DisposeAsync();
        _contexts.Remove(context);
        var restarted = await CreateContextAsync(databaseName);
        var afterRestart = await restarted.CreateConsumer().DrainAsync(10);

        beforeRestart.Should().HaveCount(2);
        afterRestart.Select(candidate => candidate.AggregateId).Should().NotIntersectWith(beforeRestart.Select(candidate => candidate.AggregateId));
        beforeRestart.Concat(afterRestart).Select(candidate => candidate.AggregateId).Should().BeEquivalentTo(streams);
        var state = await ReadStateAsync(restarted);
        state.Should().NotBeNull();
        state.Contains("Recovery").Should().BeFalse();
    }

    [OneTimeSetUp]
    public async Task GetMongoDbContainer() => _container = await MongoDbTestContainer.StartContainerAsync();

    [Test]
    public async Task ReadBatchAsync_FailedRead_ReopensStreamAndDeliversCandidatesAgain()
    {
        var applicationName = $"interrupted-{Guid.NewGuid():N}";
        var context = await CreateContextAsync(applicationName: applicationName);
        var consumer = context.CreateConsumer();
        await consumer.DrainAsync(10);
        await consumer.ConsumeBatchAsync(10);
        var aggregateId = await context.AppendStreamAsync(1);

        // A non-resumable error on the open stream's next getMore, which the driver does not resume.
        await ConfigureFailPointAsync(context, "getMore", 2, applicationName);
        var failedRead = () => context.CandidateSource.ReadBatchAsync(10, CancellationToken.None);
        await failedRead.Should().ThrowAsync<MongoCommandException>();

        var batch = await ReadUntilAsync(context.CandidateSource, 10, read => read.Candidates.Any(c => c.AggregateId == aggregateId));

        batch.Candidates.Should().ContainSingle(candidate => candidate.AggregateId == aggregateId);
    }

    [Test]
    public async Task ReadBatchAsync_FullServerBatch_ReportsMoreAndReopensForLargerBatchSize()
    {
        var context = await CreateContextAsync();
        await context.CreateConsumer().DrainAsync(10);
        var streams = new[]
        {
            await context.AppendStreamAsync(1),
            await context.AppendStreamAsync(1),
            await context.AppendStreamAsync(1)
        };

        var partial = await ReadUntilAsync(context.CandidateSource, 2, read => read.Candidates.Count == 2);
        var complete = await ReadUntilAsync(context.CandidateSource, 10, read => read.Candidates.Count == 3);

        partial.HasMore.Should().BeTrue();
        complete.HasMore.Should().BeFalse();
        complete.Candidates.Select(candidate => candidate.AggregateId).Should().BeEquivalentTo(streams);
    }

    [Test]
    public async Task ReadBatchAsync_RecoveryStarts_LogsItsCause()
    {
        var applicationName = $"recovery-cause-{Guid.NewGuid():N}";
        var context = await CreateContextAsync(applicationName: applicationName);
        var logger = new CapturingLogger<ChangeStreamAnchorCandidateSource>();
        using (var source = CreateSource(context, logger))
        {
            var consumer = new AnchorCandidateConsumer(context.MongoHelper, source);
            await consumer.DrainAsync(10);
            await consumer.ConsumeBatchAsync(10);
        }

        // A new source opens the stream at the persisted resume token, which the server reports as lost.
        await ConfigureFailPointAsync(context, "aggregate", ChangeStreamAnchorCandidateSource.ChangeStreamHistoryLostCode, applicationName);
        using (var source = CreateSource(context, logger))
        {
            await source.ReadBatchAsync(10, CancellationToken.None);
        }

        logger.Entries.Where(entry => entry.Level == LogLevel.Warning).Select(entry => entry.Message)
              .Should().SatisfyRespectively(
                  first => first.Should().Contain("no change-stream position is persisted"),
                  second => second.Should().Contain("no longer in the oplog"));
    }

    [Test]
    public async Task ReadBatchAsync_ResumePointNoLongerInOplog_RecoversByScanningStreamHeads()
    {
        var applicationName = $"history-lost-{Guid.NewGuid():N}";
        var context = await CreateContextAsync(applicationName: applicationName);
        await context.CreateConsumer().DrainAsync(10);
        var twoVersions = await context.AppendStreamAsync(2);
        var oneVersion = await context.AppendStreamAsync(1);

        // The next change stream this client opens fails as if its resume point had left the oplog.
        await ConfigureFailPointAsync(context, "aggregate", ChangeStreamAnchorCandidateSource.ChangeStreamHistoryLostCode, applicationName);

        var batch = await context.CandidateSource.ReadBatchAsync(10, CancellationToken.None);

        // A scan yields only stream heads, whereas the change stream would also yield version 1 of the first stream.
        batch.Candidates.Select(candidate => (candidate.AggregateId, candidate.Version))
             .Should().BeEquivalentTo([(twoVersions, 2L), (oneVersion, 1L)]);
    }

    [Test]
    public async Task ReadBatchAsync_StartOfCompletedRecoveryLost_LogsErrorWithScanDuration()
    {
        var applicationName = $"recovery-lost-{Guid.NewGuid():N}";
        var context = await CreateContextAsync(applicationName: applicationName);
        var aggregateId = await context.AppendStreamAsync(1);
        var logger = new CapturingLogger<ChangeStreamAnchorCandidateSource>();
        using var source = CreateSource(context, logger);
        await new AnchorCandidateConsumer(context.MongoHelper, source).DrainAsync(10);

        // The stream would start at the cluster time taken before the completed scan, which has left the oplog.
        await ConfigureFailPointAsync(context, "aggregate", ChangeStreamAnchorCandidateSource.ChangeStreamHistoryLostCode, applicationName);
        var batch = await source.ReadBatchAsync(10, CancellationToken.None);

        logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Error)
              .Which.Message.Should().Contain("must start over").And.Contain("its scan took").And.Contain("ago");
        batch.Candidates.Should().ContainSingle(candidate => candidate.AggregateId == aggregateId);
    }

    [Test]
    public async Task ReadBatchAsync_UnsealedStreamHeads_ExaminesBoundedNumberOfStreamsPerBatch()
    {
        var databaseName = $"IntegrityTestDb_{Guid.NewGuid():N}";
        var unprotected = await LedgerStoreContext.CreateAsync(_container, false, databaseName: databaseName);
        _contexts.Add(unprotected);
        var context = await CreateContextAsync(databaseName);

        // Unsealed heads sort before the sealed ones, so the first batches examine streams without yielding candidates.
        for (var index = 1; index <= 4; index++)
        {
            await unprotected.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(Guid.Parse($"00000000-0000-0000-0000-00000000000{index}"), 1)]);
        }

        var sealedStreams = new[]
        {
            Guid.Parse("ffffffff-ffff-ffff-ffff-fffffffffff1"),
            Guid.Parse("ffffffff-ffff-ffff-ffff-fffffffffff2")
        };
        foreach (var aggregateId in sealedStreams)
        {
            await context.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(aggregateId, 1)]);
        }

        var consumer = context.CreateConsumer();
        var batches = new List<ConsumedAnchorCandidates>();
        do
        {
            batches.Add(await consumer.ConsumeBatchAsync(2));
        } while (batches[^1].HasMore);

        batches[0].Candidates.Should().BeEmpty();
        batches[0].HasMore.Should().BeTrue();
        // Six streams take three full batches; a fourth finds that no stream is left.
        batches.Should().HaveCount(4).And.OnlyContain(batch => batch.Candidates.Count <= 2);
        batches.SelectMany(batch => batch.Candidates).Select(candidate => candidate.AggregateId).Should().BeEquivalentTo(sealedStreams);
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

    /// <summary>
    /// Makes the next <paramref name="command"/> of the client with <paramref name="applicationName"/> fail with
    /// <paramref name="errorCode"/> and no error labels, so the driver does not resume the change stream.
    /// </summary>
    private static Task ConfigureFailPointAsync(LedgerStoreContext context, String command, Int32 errorCode, String applicationName)
        => context.MongoHelper.Client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "configureFailPoint", "failCommand" },
            { "mode", new BsonDocument("times", 1) },
            {
                "data", new BsonDocument
                {
                    {
                        "failCommands", new BsonArray
                        {
                            command
                        }
                    },
                    { "errorCode", errorCode },
                    { "errorLabels", new BsonArray() },
                    { "appName", applicationName }
                }
            }
        });

    private static ChangeStreamAnchorCandidateSource CreateSource(LedgerStoreContext context, CapturingLogger<ChangeStreamAnchorCandidateSource> logger)
        => new(context.MongoHelper,
               context.ServiceProvider.GetRequiredService<IntegrityAnchoringOptions>(),
               context.ServiceProvider.GetRequiredService<ProtectedStreamRegistry>(),
               TimeProvider.System,
               logger);

    private static IMongoCollection<BsonDocument> GetStateCollection(LedgerStoreContext context)
        => context.MongoHelper.Database.GetCollection<BsonDocument>(
            context.ServiceProvider.GetRequiredService<IntegrityAnchoringOptions>().ChangeStreamStateCollectionName);

    private static async Task<BsonDocument?> ReadStateAsync(LedgerStoreContext context)
        => await GetStateCollection(context).Find(new BsonDocument("_id", "changeStream")).FirstOrDefaultAsync();

    private static async Task<AnchorCandidateBatch> ReadUntilAsync(IAnchorCandidateSource source,
                                                                   Int32 maxCandidates,
                                                                   Func<AnchorCandidateBatch, Boolean> isComplete)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var batch = await source.ReadBatchAsync(maxCandidates, CancellationToken.None);
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

    private async Task<LedgerStoreContext> CreateContextAsync(String? databaseName = null,
                                                              String? applicationName = null,
                                                              Action<MongoBuilder>? configureMongo = null)
    {
        var context = await LedgerStoreContext.CreateAsync(_container,
                                                           databaseName: databaseName,
                                                           anchorCandidates: true,
                                                           applicationName: applicationName,
                                                           configureMongo: configureMongo);
        _contexts.Add(context);
        return context;
    }
}
