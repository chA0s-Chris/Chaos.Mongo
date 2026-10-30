// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Chaos.Mongo.EventStore.Integrity;
using Chaos.Mongo.EventStore.Tests.Integrity;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using NUnit.Framework;
using Testcontainers.MongoDb;

/// <summary>
/// Two containers configured with different anchoring collection prefixes against the same database.
/// </summary>
public class IntegrityAnchoringCollectionPrefixIntegrationTests
{
    private readonly List<LedgerStoreContext> _contexts = [];
    private readonly List<TestTimestampAuthority> _testAuthorities = [];
    private MongoDbContainer _container;

    [OneTimeSetUp]
    public async Task GetMongoDbContainer() => _container = await MongoDbTestContainer.StartContainerAsync();

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

    [Test]
    public async Task TwoContainers_DifferentPrefixes_KeepIndependentAnchorChainsWithNoOverlappingCollections()
    {
        var databaseName = $"IntegrityTestDb_{Guid.NewGuid():N}";
        var contextA = await CreateContextAsync(databaseName, "Alpha");
        var contextB = await CreateContextAsync(databaseName, "Beta");
        var storeA = GetStore(contextA);
        var storeB = GetStore(contextB);
        var runA = contextA.ServiceProvider.GetRequiredService<AnchoringRun>();
        var runB = contextB.ServiceProvider.GetRequiredService<AnchoringRun>();
        var streamA = await contextA.AppendStreamAsync(1);
        var streamB = await contextB.AppendStreamAsync(1);

        await RunUntilAsync(runA, () => IsAnchoredAsync(storeA, streamA));
        await RunUntilAsync(runB, () => IsAnchoredAsync(storeB, streamB));

        storeA.Anchors.CollectionNamespace.CollectionName.Should().Be("AlphaAnchors");
        storeB.Anchors.CollectionNamespace.CollectionName.Should().Be("BetaAnchors");
        storeA.Leaves.CollectionNamespace.CollectionName.Should().Be("AlphaAnchorLeaves");
        storeB.Leaves.CollectionNamespace.CollectionName.Should().Be("BetaAnchorLeaves");
        (await IsAnchoredAsync(storeA, streamB)).Should().BeFalse("contextA's anchors must not cover contextB's stream");
        (await IsAnchoredAsync(storeB, streamA)).Should().BeFalse("contextB's anchors must not cover contextA's stream");
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

    private static async Task RunUntilAsync(AnchoringRun run, Func<Task<Boolean>> isDone)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await isDone())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("The expected anchoring state was not reached within the timeout.");
            }

            await run.RunAsync();
            await Task.Delay(50);
        }
    }

    private async Task<LedgerStoreContext> CreateContextAsync(String databaseName, String prefix)
    {
        var testAuthority = CreateTestAuthority();
        var context = await LedgerStoreContext.CreateAsync(_container,
                                                           databaseName: databaseName,
                                                           configureOptions: options => options.CollectionPrefix = $"{prefix}Ledgers",
                                                           configureMongo: mongo => mongo.WithIntegrityAnchoring(anchoring => anchoring
                                                                                                                     .WithTimestampAuthority(_ => testAuthority.CreateAuthority())
                                                                                                                     .WithCollectionPrefix(prefix)));
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
