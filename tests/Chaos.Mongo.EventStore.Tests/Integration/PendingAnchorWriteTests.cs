// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Chaos.Mongo.EventStore.Integrity;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Driver;
using NUnit.Framework;
using Testcontainers.MongoDb;

public class PendingAnchorWriteTests
{
    private readonly List<LedgerStoreContext> _contexts = [];
    private MongoDbContainer _container;

    [Test]
    [TestCase(false)]
    [TestCase(true)]
    public async Task AppendEventsAsync_PendingCollectionSource_WritesOneEntryForTheLastEvent(Boolean bulkWrite)
    {
        var context = await CreateContextAsync(true, bulkWrite, "PendingCollection");

        var aggregateId = await context.AppendStreamAsync(3);

        var entries = await ReadEntriesAsync(context);
        entries.Should().ContainSingle();
        var candidate = PendingAnchorEntry.ToCandidate(entries[0]);
        candidate.Should().BeEquivalentTo(new AnchorCandidate(nameof(LedgerAggregate),
                                                              aggregateId,
                                                              3,
                                                              LedgerStoreContext.ReadIntegrity(await context.FindEventAsync(aggregateId, 3)).Hash));
    }

    [Test]
    [TestCase(true, "None")]
    [TestCase(true, "ChangeStream")]
    [TestCase(false, "PendingCollection")]
    public async Task AppendEventsAsync_WithoutPendingCollectionSource_WritesNoEntries(Boolean integrityProtection, String candidateSource)
    {
        var context = await CreateContextAsync(integrityProtection, false, candidateSource);

        await context.AppendStreamAsync(2);

        (await ReadEntriesAsync(context)).Should().BeEmpty();
    }

    [OneTimeSetUp]
    public async Task GetMongoDbContainer() => _container = await MongoDbTestContainer.StartContainerAsync();

    [Test]
    public async Task RunPassAsync_PendingCollectionSource_WritesOneEntryPerSealedChunk()
    {
        var databaseName = $"IntegrityTestDb_{Guid.NewGuid():N}";
        var unprotected = await CreateContextAsync(false, false, "None", databaseName);
        var context = await CreateContextAsync(true, false, "PendingCollection", databaseName, options => options.SealingChunkSize = 2);
        var aggregateId = await unprotected.AppendStreamAsync(5);

        await context.Sweep.RunPassAsync();

        var candidates = (await ReadEntriesAsync(context)).Select(PendingAnchorEntry.ToCandidate).ToList();
        candidates.Select(candidate => candidate.Version).Should().BeEquivalentTo([2L, 4L, 5L]);
        foreach (var candidate in candidates)
        {
            candidate.AggregateId.Should().Be(aggregateId);
            candidate.Hash.Should().Equal(LedgerStoreContext.ReadIntegrity(await context.FindEventAsync(aggregateId, candidate.Version)).Hash);
        }
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

    private static Task<List<BsonDocument>> ReadEntriesAsync(LedgerStoreContext context)
        => context.MongoHelper.Database
                  .GetCollection<BsonDocument>(new IntegrityAnchoringOptions().PendingAnchorsCollectionName)
                  .Find(FilterDefinition<BsonDocument>.Empty)
                  .ToListAsync();

    private async Task<LedgerStoreContext> CreateContextAsync(Boolean integrityProtection,
                                                              Boolean bulkWrite,
                                                              String candidateSource,
                                                              String? databaseName = null,
                                                              Action<MongoEventStoreOptions<LedgerAggregate>>? configureOptions = null)
    {
        var context = await LedgerStoreContext.CreateAsync(_container,
                                                           integrityProtection,
                                                           bulkWrite,
                                                           databaseName,
                                                           configureOptions,
                                                           anchorCandidateSource: Enum.Parse<AnchorCandidateSourceKind>(candidateSource));
        _contexts.Add(context);
        return context;
    }
}
