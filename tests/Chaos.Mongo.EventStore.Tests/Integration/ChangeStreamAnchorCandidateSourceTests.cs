// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Chaos.Mongo.EventStore.Integrity;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
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
        await consumer.ConsumeBatchAsync(10);

        var candidates = await consumer.ConsumeBatchAsync(10);

        candidates.Should().BeEmpty();
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
            beforeRestart.AddRange(await consumer.ConsumeBatchAsync(1));
        }

        await context.DisposeAsync();
        _contexts.Remove(context);
        var restarted = await CreateContextAsync(databaseName, configureMongo: LedgerStoreContext.AddShipmentStore);
        var afterRestart = await restarted.CreateConsumer().ConsumeBatchAsync(10);

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
        beforeRestart.AddRange(await consumer.ConsumeBatchAsync(1));
        beforeRestart.AddRange(await consumer.ConsumeBatchAsync(1));

        await context.DisposeAsync();
        _contexts.Remove(context);
        var restarted = await CreateContextAsync(databaseName);
        var afterRestart = await restarted.CreateConsumer().ConsumeBatchAsync(10);

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
    public async Task ReadBatchAsync_ResumePointNoLongerInOplog_RecoversByScanningStreamHeads()
    {
        var applicationName = $"history-lost-{Guid.NewGuid():N}";
        var context = await CreateContextAsync(applicationName: applicationName);
        await context.CreateConsumer().ConsumeBatchAsync(10);
        var twoVersions = await context.AppendStreamAsync(2);
        var oneVersion = await context.AppendStreamAsync(1);

        // The next change stream this client opens fails as if its resume point had left the oplog.
        await context.MongoHelper.Client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "configureFailPoint", "failCommand" },
            { "mode", new BsonDocument("times", 1) },
            {
                "data", new BsonDocument
                {
                    {
                        "failCommands", new BsonArray
                        {
                            "aggregate"
                        }
                    },
                    { "errorCode", ChangeStreamAnchorCandidateSource.ChangeStreamHistoryLostCode },
                    { "appName", applicationName }
                }
            }
        });

        var batch = await context.CandidateSource.ReadBatchAsync(10, CancellationToken.None);

        // A scan yields only stream heads, whereas the change stream would also yield version 1 of the first stream.
        batch.Candidates.Select(candidate => (candidate.AggregateId, candidate.Version))
             .Should().BeEquivalentTo([(twoVersions, 2L), (oneVersion, 1L)]);
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

    private static IMongoCollection<BsonDocument> GetStateCollection(LedgerStoreContext context)
        => context.MongoHelper.Database.GetCollection<BsonDocument>(
            context.ServiceProvider.GetRequiredService<IntegrityAnchoringOptions>().ChangeStreamStateCollectionName);

    private static async Task<BsonDocument?> ReadStateAsync(LedgerStoreContext context)
        => await GetStateCollection(context).Find(new BsonDocument("_id", "changeStream")).FirstOrDefaultAsync();

    private async Task<LedgerStoreContext> CreateContextAsync(String? databaseName = null,
                                                              String? applicationName = null,
                                                              Action<MongoBuilder>? configureMongo = null)
    {
        var context = await LedgerStoreContext.CreateAsync(_container,
                                                           databaseName: databaseName,
                                                           anchorCandidateSource: AnchorCandidateSourceKind.ChangeStream,
                                                           applicationName: applicationName,
                                                           configureMongo: configureMongo);
        _contexts.Add(context);
        return context;
    }
}
