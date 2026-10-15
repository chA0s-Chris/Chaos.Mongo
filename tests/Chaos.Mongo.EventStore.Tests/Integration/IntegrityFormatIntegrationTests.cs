// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Chaos.Mongo.EventStore.Errors;
using Chaos.Mongo.EventStore.Integrity;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using NUnit.Framework;
using Testcontainers.MongoDb;

public class IntegrityFormatIntegrationTests
{
    private const String RenamedAggregateType = "RenamedLedger";
    private readonly List<LedgerStoreContext> _contexts = [];
    private MongoDbContainer _container;

    [Test]
    [TestCase(false)]
    [TestCase(true)]
    public async Task AppendEventsAsync_VersionOneWithoutAggregateType_ThrowsAndPersistsNothing(Boolean storeAsNumber)
    {
        var (unprotected, protectedContext) = await CreateSharedContextsAsync();
        var aggregateId = await unprotected.AppendStreamAsync(2);
        await BreakAggregateTypeAsync(unprotected, aggregateId, storeAsNumber);

        var act = () => protectedContext.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(aggregateId, 3)]);

        await act.Should().ThrowAsync<MongoEventStoreException>().WithMessage("*Version 1*no aggregate type*");
        (await protectedContext.CountEventsAsync(aggregateId)).Should().Be(2);
        (await protectedContext.FindEventAsync(aggregateId, 1)).Contains(EventIntegrityChain.ElementName).Should().BeFalse();
    }

    [Test]
    public async Task AppendEventsAsync_VersionOne_ChainsFromGenesisOfStoredAggregateType()
    {
        var context = await CreateContextAsync(true);
        var aggregateId = await context.AppendStreamAsync(1);

        var first = await context.FindEventAsync(aggregateId, 1);

        first[EventDocumentFields<LedgerAggregate>.AggregateType].AsString.Should().Be(nameof(LedgerAggregate));
        LedgerStoreContext.ReadIntegrity(first).PreviousHash
                          .Should().Equal(EventIntegrityChain.ComputeGenesis(nameof(LedgerAggregate), aggregateId));
    }

    [OneTimeSetUp]
    public async Task GetMongoDbContainer() => _container = await MongoDbTestContainer.StartContainerAsync();

    [Test]
    public async Task RunPassAsync_StoredAggregateTypeDiffersFromClassName_SealsAndVerifies()
    {
        var (unprotected, protectedContext) = await CreateSharedContextsAsync();
        var aggregateId = await unprotected.AppendStreamAsync(3);
        await unprotected.Events.UpdateManyAsync(
            EventDocumentFields<LedgerAggregate>.ForAggregate<BsonDocument>(aggregateId),
            Builders<BsonDocument>.Update.Set(EventDocumentFields<LedgerAggregate>.AggregateType, RenamedAggregateType));

        await protectedContext.Sweep.RunPassAsync();
        await protectedContext.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(aggregateId, 4)]);

        (await protectedContext.CreateVerifier().VerifyStreamAsync(aggregateId)).IsIntact.Should().BeTrue();
        LedgerStoreContext.ReadIntegrity(await protectedContext.FindEventAsync(aggregateId, 1)).PreviousHash
                          .Should().Equal(EventIntegrityChain.ComputeGenesis(RenamedAggregateType, aggregateId));
    }

    [Test]
    [TestCase(false)]
    [TestCase(true)]
    public async Task RunPassAsync_VersionOneWithoutAggregateType_SkipsAndLogsStream(Boolean storeAsNumber)
    {
        var (unprotected, protectedContext) = await CreateSharedContextsAsync();
        var brokenStream = await unprotected.AppendStreamAsync(2);
        await BreakAggregateTypeAsync(unprotected, brokenStream, storeAsNumber);
        var healthyStream = await unprotected.AppendStreamAsync(2);
        var logger = new CapturingLogger<EventStreamSealingSweep<LedgerAggregate>>();
        var sweep = new EventStreamSealingSweep<LedgerAggregate>(protectedContext.MongoHelper, protectedContext.Options, TimeProvider.System, logger);

        var outcome = await sweep.RunPassAsync();

        outcome.Should().Be(SealingPassOutcome.Completed);
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Message.Contains(brokenStream.ToString()));
        (await protectedContext.CreateVerifier().VerifyStreamAsync(healthyStream)).IsIntact.Should().BeTrue();
        (await protectedContext.CreateVerifier().VerifyStreamAsync(brokenStream))
            .Should().Be(StreamVerificationResult.Broken(1, StreamVerificationFailure.MissingAggregateType));
        (await protectedContext.FindEventAsync(brokenStream, 1)).Contains(EventIntegrityChain.ElementName).Should().BeFalse();
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

    [Test]
    [TestCase(false, "Retroactive")]
    [TestCase(true, "Append")]
    public async Task VerifyStreamAsync_ChangedSealMode_ReportsHashMismatch(Boolean sealedRetroactively, String forgedMode)
    {
        var (unprotected, protectedContext) = await CreateSharedContextsAsync();
        Guid aggregateId;
        if (sealedRetroactively)
        {
            aggregateId = await unprotected.AppendStreamAsync(3);
            await protectedContext.Sweep.RunPassAsync();
        }
        else
        {
            aggregateId = await protectedContext.AppendStreamAsync(3);
        }

        LedgerStoreContext.ReadIntegrity(await protectedContext.FindEventAsync(aggregateId, 2)).SealMode.ToString().Should().NotBe(forgedMode);
        await SetSealModeAsync(protectedContext, aggregateId, 2, forgedMode);

        (await protectedContext.CreateVerifier().VerifyStreamAsync(aggregateId))
            .Should().Be(StreamVerificationResult.Broken(2, StreamVerificationFailure.HashMismatch));
    }

    [Test]
    public async Task VerifyStreamAsync_UndefinedNumericSealMode_ReportsMalformedIntegrity()
    {
        var context = await CreateContextAsync(true);
        var aggregateId = await context.AppendStreamAsync(3);
        await SetSealModeAsync(context, aggregateId, 2, 7);

        (await context.CreateVerifier().VerifyStreamAsync(aggregateId))
            .Should().Be(StreamVerificationResult.Broken(2, StreamVerificationFailure.MalformedIntegrity));
    }

    private static Task BreakAggregateTypeAsync(LedgerStoreContext context, Guid aggregateId, Boolean storeAsNumber)
    {
        var aggregateTypeElement = EventDocumentFields<LedgerAggregate>.AggregateType;
        return context.Events.UpdateOneAsync(
            EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, 1),
            storeAsNumber
                ? Builders<BsonDocument>.Update.Set(aggregateTypeElement, 42)
                : Builders<BsonDocument>.Update.Unset(aggregateTypeElement));
    }

    private static Task SetSealModeAsync(LedgerStoreContext context, Guid aggregateId, Int64 version, BsonValue sealMode)
        => context.Events.UpdateOneAsync(
            EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, version),
            Builders<BsonDocument>.Update.Set($"{EventIntegrityChain.ElementName}.{nameof(EventIntegrity.SealMode)}", sealMode));

    private async Task<LedgerStoreContext> CreateContextAsync(Boolean integrityProtection, String? databaseName = null)
    {
        var context = await LedgerStoreContext.CreateAsync(_container, integrityProtection, databaseName: databaseName);
        _contexts.Add(context);
        return context;
    }

    /// <summary>
    /// Creates an unprotected and a protected store on the same collections, so the unprotected one
    /// can write events without integrity data for the protected one to seal.
    /// </summary>
    private async Task<(LedgerStoreContext Unprotected, LedgerStoreContext Protected)> CreateSharedContextsAsync()
    {
        var databaseName = $"IntegrityTestDb_{Guid.NewGuid():N}";
        var unprotected = await CreateContextAsync(false, databaseName);
        var protectedContext = await CreateContextAsync(true, databaseName);
        return (unprotected, protectedContext);
    }
}
