// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Chaos.Mongo.EventStore.Errors;
using Chaos.Mongo.EventStore.Integrity;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using NUnit.Framework;
using Testcontainers.MongoDb;

public class IntegrityChainIntegrationTests
{
    private static readonly DateTime FixedCreatedUtc = new DateTime(2026, 10, 7, 12, 30, 45, DateTimeKind.Utc).AddTicks(1234);

    private static readonly BsonValue[] NonVersionValues =
    [
        new BsonDecimal128(Decimal128.MaxValue),
        new BsonDecimal128(Decimal128.Parse("3.5")),
        new BsonDouble(3.5)
    ];

    private readonly List<LedgerStoreContext> _contexts = [];
    private MongoDbContainer _container;

    [Test]
    public async Task AppendEventsAsync_BatchWithIntegrityProtection_SealsEventsIntoChainFromGenesis()
    {
        var context = await CreateContextAsync();
        var aggregateId = Guid.NewGuid();

        await context.Store.AppendEventsAsync(
            [LedgerStoreContext.CreateEntry(aggregateId, 1), LedgerStoreContext.CreateEntry(aggregateId, 2)]);

        var first = await context.FindEventAsync(aggregateId, 1);
        var second = await context.FindEventAsync(aggregateId, 2);
        var firstIntegrity = LedgerStoreContext.ReadIntegrity(first);
        var secondIntegrity = LedgerStoreContext.ReadIntegrity(second);

        first[EventIntegrityChain.ElementName]["FormatVersion"].AsInt32.Should().Be(1);
        first[EventIntegrityChain.ElementName]["Algorithm"].AsString.Should().Be("SHA-256");
        first[EventIntegrityChain.ElementName]["SealMode"].AsString.Should().Be("Append");
        firstIntegrity.PreviousHash.Should().Equal(EventIntegrityChain.ComputeGenesis(nameof(LedgerAggregate), aggregateId));
        firstIntegrity.Hash.Should().HaveCount(32);
        secondIntegrity.PreviousHash.Should().Equal(firstIntegrity.Hash);
        secondIntegrity.SealMode.Should().Be(IntegritySealMode.Append);
    }

    [Test]
    [TestCase(false)]
    [TestCase(true)]
    public async Task AppendEventsAsync_BothAppendPaths_PersistByteIdenticalDocuments(Boolean integrityProtection)
    {
        var defaultPath = await CreateContextAsync(integrityProtection);
        var bulkWritePath = await CreateContextAsync(integrityProtection, true);
        var aggregateId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        await defaultPath.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(aggregateId, 1, eventId, FixedCreatedUtc)]);
        await bulkWritePath.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(aggregateId, 1, eventId, FixedCreatedUtc)]);

        var defaultBytes = await defaultPath.ReadRawEventAsync(aggregateId, 1);
        var bulkWriteBytes = await bulkWritePath.ReadRawEventAsync(aggregateId, 1);

        defaultBytes.Should().Equal(bulkWriteBytes);
    }

    [Test]
    public async Task AppendEventsAsync_ConcurrentAppendsWithIntegrityProtection_ProduceLinearChain()
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(1);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => AppendWithRetryAsync(context, aggregateId)));

        var result = await context.CreateVerifier().VerifyStreamAsync(aggregateId);
        result.IsIntact.Should().BeTrue();
        (await context.CountEventsAsync(aggregateId)).Should().Be(9);
    }

    [Test]
    public async Task AppendEventsAsync_LaterAppendWithIntegrityProtection_ChainsFromStoredPredecessor()
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(1);

        await context.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(aggregateId, 2)]);

        var firstIntegrity = LedgerStoreContext.ReadIntegrity(await context.FindEventAsync(aggregateId, 1));
        var secondIntegrity = LedgerStoreContext.ReadIntegrity(await context.FindEventAsync(aggregateId, 2));
        secondIntegrity.PreviousHash.Should().Equal(firstIntegrity.Hash);
        (await context.CreateVerifier().VerifyStreamAsync(aggregateId)).IsIntact.Should().BeTrue();
    }

    [Test]
    public async Task AppendEventsAsync_MissingPredecessor_ThrowsMongoEventStoreExceptionAndPersistsNothing()
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(3);
        await context.Events.DeleteOneAsync(EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, 3));

        var act = () => context.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(aggregateId, 4)]);

        await act.Should().ThrowAsync<MongoEventStoreException>().WithMessage("*Version 3*missing*");
        (await context.CountEventsAsync(aggregateId)).Should().Be(2);
        var readModel = await context.MongoHelper.Database
                                     .GetCollection<LedgerAggregate>(context.Options.ReadModelCollectionName)
                                     .Find(a => a.Id == aggregateId)
                                     .SingleAsync();
        readModel.Version.Should().Be(3);
    }

    [Test]
    public async Task AppendEventsAsync_SubMillisecondCreatedUtc_StoredBytesRecomputeToWrittenHash()
    {
        var context = await CreateContextAsync();
        var aggregateId = Guid.NewGuid();
        var entry = LedgerStoreContext.CreateEntry(aggregateId, 1, createdUtc: FixedCreatedUtc);

        await context.Store.AppendEventsAsync([entry]);

        var storedBytes = await context.ReadRawEventAsync(aggregateId, 1);
        var stored = BsonSerializer.Deserialize<BsonDocument>(storedBytes);
        stored["Counter"].BsonType.Should().Be(BsonType.Int64);
        stored["Reference"].AsBsonBinaryData.SubType.Should().Be(BsonBinarySubType.UuidStandard);
        stored["Details"].BsonType.Should().Be(BsonType.Document);
        entry.CreatedUtc.Ticks.Should().Be(FixedCreatedUtc.Ticks, "the event store must not truncate CreatedUtc");

        var canonical = RawBsonElements.RemoveTopLevelElement(storedBytes, EventIntegrityChain.ElementName);
        var recomputedHash = EventIntegrityChain.ComputeHash(
            canonical,
            EventIntegrityChain.ComputeGenesis(nameof(LedgerAggregate), aggregateId));

        entry.Integrity.Should().NotBeNull();
        recomputedHash.Should().Equal(entry.Integrity.Hash);
        recomputedHash.Should().Equal(LedgerStoreContext.ReadIntegrity(stored).Hash);
    }

    [Test]
    public async Task AppendEventsAsync_WithoutIntegrityProtection_StoresSameBytesAsTypedInsert()
    {
        var context = await CreateContextAsync(false);
        var entry = LedgerStoreContext.CreateEntry(Guid.NewGuid(), 1, createdUtc: FixedCreatedUtc);
        await context.Store.AppendEventsAsync([entry]);

        // Typed insertion is how events were stored before; the server stores _id first in both cases.
        var typedCollection = context.MongoHelper.Database.GetCollection<Event<LedgerAggregate>>("TypedInsertReference");
        await typedCollection.InsertOneAsync(entry);
        using var typedDocument = await context.MongoHelper.Database
                                               .GetCollection<RawBsonDocument>("TypedInsertReference")
                                               .Find(FilterDefinition<RawBsonDocument>.Empty)
                                               .SingleAsync();

        var storedBytes = await context.ReadRawEventAsync(entry.AggregateId, 1);
        storedBytes.Should().Equal(typedDocument.ToBson());
        BsonSerializer.Deserialize<BsonDocument>(storedBytes).Contains(EventIntegrityChain.ElementName).Should().BeFalse();
    }

    [OneTimeSetUp]
    public async Task GetMongoDbContainer() => _container = await MongoDbTestContainer.StartContainerAsync();

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
    public async Task VerifyStreamAsync_DeletedEvent_ReportsVersionGap()
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(3);
        await context.Events.DeleteOneAsync(EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, 2));

        var result = await context.CreateVerifier().VerifyStreamAsync(aggregateId);

        result.Should().Be(StreamVerificationResult.Broken(2, StreamVerificationFailure.VersionGap));
    }

    [Test]
    public async Task VerifyStreamAsync_ExtraIntegrityElement_ReportsIntactAndKeepsTypedReads()
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(3);
        await context.Events.UpdateOneAsync(
            EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, 1),
            Builders<BsonDocument>.Update.Set($"{EventIntegrityChain.ElementName}.AnchorId", "future-field"));

        var result = await context.CreateVerifier().VerifyStreamAsync(aggregateId);
        var events = new List<Event<LedgerAggregate>>();
        await foreach (var @event in context.Store.GetEventStream(aggregateId))
        {
            events.Add(@event);
        }

        result.IsIntact.Should().BeTrue();
        events.Should().HaveCount(3);
    }

    [Test]
    public async Task VerifyStreamAsync_InsertedEventWithRecomputedChain_ReportsShiftedSuccessor()
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(3);
        await context.SetVersionAsync(aggregateId, 3, 4);

        var forged = await context.FindEventAsync(aggregateId, 2);
        var predecessorHash = LedgerStoreContext.ReadIntegrity(forged).Hash;
        forged.Remove(EventIntegrityChain.ElementName);
        forged["_id"] = new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard);
        forged["Version"] = 3L;
        var forgedIntegrity = EventIntegrityChain.Seal(forged.ToBson(), predecessorHash, IntegritySealMode.Append);
        forged.Add(EventIntegrityChain.ElementName, forgedIntegrity.ToBsonDocument());
        await context.Events.InsertOneAsync(forged);

        var result = await context.CreateVerifier().VerifyStreamAsync(aggregateId);

        result.Should().Be(StreamVerificationResult.Broken(4, StreamVerificationFailure.PreviousHashMismatch));
    }

    [Test]
    public async Task VerifyStreamAsync_IntactStream_ReportsIntact()
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(3);

        var result = await context.CreateVerifier().VerifyStreamAsync(aggregateId);

        result.Should().Be(StreamVerificationResult.Intact);
        result.IsIntact.Should().BeTrue();
    }

    [Test]
    [TestCase("_integrity")]
    [TestCase("_integrity.Hash")]
    [TestCase("_integrity.SealMode")]
    public async Task VerifyStreamAsync_MalformedIntegrity_ReportsMalformedIntegrity(String field)
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(3);
        await context.Events.UpdateOneAsync(
            EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, 2),
            Builders<BsonDocument>.Update.Set(field, "tampered"));

        var result = await context.CreateVerifier().VerifyStreamAsync(aggregateId);

        result.Should().Be(StreamVerificationResult.Broken(2, StreamVerificationFailure.MalformedIntegrity));
    }

    [Test]
    [TestCase("FormatVersion")]
    [TestCase("Algorithm")]
    [TestCase("PreviousHash")]
    [TestCase("Hash")]
    [TestCase("SealMode")]
    public async Task VerifyStreamAsync_MissingIntegrityMember_ReportsMalformedIntegrity(String member)
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(3);
        await context.Events.UpdateOneAsync(
            EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, 2),
            Builders<BsonDocument>.Update.Unset($"{EventIntegrityChain.ElementName}.{member}"));

        var result = await context.CreateVerifier().VerifyStreamAsync(aggregateId);

        result.Should().Be(StreamVerificationResult.Broken(2, StreamVerificationFailure.MalformedIntegrity));
    }

    [Test]
    public async Task VerifyStreamAsync_ModifiedEvent_ReportsHashMismatch()
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(3);
        await context.Events.UpdateOneAsync(
            EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, 2),
            Builders<BsonDocument>.Update.Set("Counter", 43L));

        var result = await context.CreateVerifier().VerifyStreamAsync(aggregateId);

        result.Should().Be(StreamVerificationResult.Broken(2, StreamVerificationFailure.HashMismatch));
    }

    [Test]
    [TestCaseSource(nameof(NonVersionValues))]
    public async Task VerifyStreamAsync_NonIntegralOrOutOfRangeVersion_ReportsVersionGap(BsonValue version)
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(3);

        // The last event keeps its position in version order, so the verifier has to read the tampered value.
        await context.Events.UpdateOneAsync(
            EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, 3),
            Builders<BsonDocument>.Update.Set("Version", version));

        var result = await context.CreateVerifier().VerifyStreamAsync(aggregateId);

        result.Should().Be(StreamVerificationResult.Broken(3, StreamVerificationFailure.VersionGap));
    }

    [Test]
    public async Task VerifyStreamAsync_ReorderedEvents_ReportsFirstSwappedVersion()
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(3);
        await context.SetVersionAsync(aggregateId, 2, 100);
        await context.SetVersionAsync(aggregateId, 3, 2);
        await context.SetVersionAsync(aggregateId, 100, 3);

        var result = await context.CreateVerifier().VerifyStreamAsync(aggregateId);

        result.Should().Be(StreamVerificationResult.Broken(2, StreamVerificationFailure.PreviousHashMismatch));
    }

    [Test]
    public async Task VerifyStreamAsync_UnsealedEvent_ReportsNotSealed()
    {
        var context = await CreateContextAsync(false);
        var aggregateId = await context.AppendStreamAsync(1);

        var result = await context.CreateVerifier().VerifyStreamAsync(aggregateId);

        result.Should().Be(StreamVerificationResult.Broken(1, StreamVerificationFailure.NotSealed));
    }

    [Test]
    public async Task VerifyStreamAsync_UnsupportedFormatVersion_ReportsUnsupportedFormat()
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(3);
        await context.Events.UpdateOneAsync(
            EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, 1),
            Builders<BsonDocument>.Update.Set($"{EventIntegrityChain.ElementName}.FormatVersion", 2));

        var result = await context.CreateVerifier().VerifyStreamAsync(aggregateId);

        result.Should().Be(StreamVerificationResult.Broken(1, StreamVerificationFailure.UnsupportedFormat));
    }

    private static async Task AppendWithRetryAsync(LedgerStoreContext context, Guid aggregateId)
    {
        while (true)
        {
            var version = await context.Store.GetExpectedNextVersionAsync(aggregateId);
            try
            {
                await context.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(aggregateId, version)]);
                return;
            }
            catch (MongoConcurrencyException)
            {
                // Another writer committed this version first; retry with the next one.
            }
        }
    }

    private async Task<LedgerStoreContext> CreateContextAsync(Boolean integrityProtection = true, Boolean bulkWrite = false)
    {
        var context = await LedgerStoreContext.CreateAsync(_container, integrityProtection, bulkWrite);
        _contexts.Add(context);
        return context;
    }
}
