// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Chaos.Mongo.EventStore.Integrity;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using NUnit.Framework;
using Testcontainers.MongoDb;

/// <summary>
/// Sealing and verification of events whose members are marked as personal data.
/// </summary>
public class PersonalDataCommitmentIntegrationTests
{
    private static readonly DateTime FixedCreatedUtc = new(2026, 10, 7, 12, 30, 45, DateTimeKind.Utc);
    private const String MemoPath = "Memo";
    private const String NamePath = "Counterparty.Name";
    private readonly List<LedgerStoreContext> _contexts = [];
    private MongoDbContainer _container;

    [Test]
    [TestCase(false)]
    [TestCase(true)]
    public async Task AppendEventsAsync_BothAppendPaths_PersistIdenticalDocumentsOutsideIntegrityAndBothVerify(Boolean bulkWrite)
    {
        var reference = await CreateContextAsync();
        var other = await CreateContextAsync(bulkWrite: bulkWrite);
        var aggregateId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        await reference.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(aggregateId, 1, eventId, FixedCreatedUtc)]);
        await other.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(aggregateId, 1, eventId, FixedCreatedUtc)]);

        var referenceBytes = await reference.ReadRawEventAsync(aggregateId, 1);
        var otherBytes = await other.ReadRawEventAsync(aggregateId, 1);
        RawBsonElements.RemoveTopLevelElement(otherBytes, EventIntegrityChain.ElementName)
                       .Should().Equal(RawBsonElements.RemoveTopLevelElement(referenceBytes, EventIntegrityChain.ElementName));
        var referenceCommitments = ReadCommitments(referenceBytes);
        var otherCommitments = ReadCommitments(otherBytes);
        otherCommitments.Select(c => c.Path).Should().Equal(referenceCommitments.Select(c => c.Path));
        otherCommitments[0].Salt.Should().NotEqual(referenceCommitments[0].Salt, "every seal draws fresh salts");
        (await reference.CreateVerifier().VerifyStreamAsync(aggregateId)).IsIntact.Should().BeTrue();
        (await other.CreateVerifier().VerifyStreamAsync(aggregateId)).IsIntact.Should().BeTrue();
    }

    [Test]
    public async Task AppendEventsAsync_MarkedEvent_HashCoversCanonicalFormWithCommitments()
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(1);

        var storedBytes = await context.ReadRawEventAsync(aggregateId, 1);
        var integrity = LedgerStoreContext.ReadIntegrity(BsonSerializer.Deserialize<BsonDocument>(storedBytes));
        var storedDocument = RawBsonElements.RemoveTopLevelElement(storedBytes, EventIntegrityChain.ElementName);
        var genesis = EventIntegrityChain.ComputeGenesis(nameof(LedgerAggregate), aggregateId);

        integrity.PersonalData.Should().NotBeNull();
        var canonical = PersonalDataCommitments.Canonicalize(storedDocument, integrity.PersonalData);
        EventIntegrityChain.ComputeHash(canonical, genesis).Should().Equal(integrity.Hash);
        EventIntegrityChain.ComputeHash(storedDocument, genesis).Should().NotEqual(integrity.Hash);
    }

    [Test]
    public async Task AppendEventsAsync_MarkedEvent_RecordsSaltedCommitmentsAndKeepsValues()
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(1);

        var storedBytes = await context.ReadRawEventAsync(aggregateId, 1);
        var stored = BsonSerializer.Deserialize<BsonDocument>(storedBytes);
        var commitments = ReadCommitments(storedBytes);
        var storedDocument = RawBsonElements.RemoveTopLevelElement(storedBytes, EventIntegrityChain.ElementName);

        stored[MemoPath].AsString.Should().Be("monthly fee");
        stored["Counterparty"]["Name"].AsString.Should().Be("Alice Example");
        commitments.Select(c => c.Path).Should().Equal(MemoPath, NamePath);
        foreach (var commitment in commitments)
        {
            commitment.Salt.Should().HaveCount(PersonalDataCommitments.SaltLength);
            commitment.Commitment.Should().HaveCount(PersonalDataCommitments.CommitmentLength);
            RawBsonElements.TryLocate(storedDocument, commitment.Path, out var type, out var value).Should().BeTrue();
            type.Should().Be(BsonType.String);
            commitment.Commitment.Should().Equal(PersonalDataCommitments.ComputeCommitment(commitment.Salt, type, storedDocument.AsSpan(value)));
        }

        var events = new List<Event<LedgerAggregate>>();
        await foreach (var @event in context.Store.GetEventStream(aggregateId))
        {
            events.Add(@event);
        }

        events.Should().ContainSingle();
        var typedIntegrity = events[0].Integrity;
        typedIntegrity.Should().NotBeNull();
        typedIntegrity.PersonalData.Should().HaveCount(2);
        (await context.CreateVerifier().VerifyStreamAsync(aggregateId)).Should().Be(StreamVerificationResult.Intact);
    }

    [Test]
    public async Task AppendEventsAsync_NullMarkedValue_RecordsCommitmentToNull()
    {
        var context = await CreateContextAsync();
        var aggregateId = Guid.NewGuid();

        await context.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(aggregateId, 1, memo: null)]);

        var storedBytes = await context.ReadRawEventAsync(aggregateId, 1);
        BsonSerializer.Deserialize<BsonDocument>(storedBytes)[MemoPath].Should().Be(BsonNull.Value);
        var memo = ReadCommitments(storedBytes).Single(c => c.Path == MemoPath);
        memo.Commitment.Should().Equal(PersonalDataCommitments.ComputeCommitment(memo.Salt, BsonType.Null, []));
        (await context.CreateVerifier().VerifyStreamAsync(aggregateId)).IsIntact.Should().BeTrue();
    }

    [Test]
    public async Task AppendEventsAsync_UnmarkedEvents_KeepIntegrityFormat()
    {
        var marked = await CreateContextAsync();
        var unmarked = await CreateContextAsync(personalData: false);
        var markedStream = await marked.AppendStreamAsync(1);
        await marked.Store.AppendEventsAsync([LedgerStoreContext.CreateAudit(markedStream, 2)]);
        var unmarkedStream = await unmarked.AppendStreamAsync(1);

        var audit = await marked.FindEventAsync(markedStream, 2);
        var unmarkedEntry = await unmarked.FindEventAsync(unmarkedStream, 1);

        audit[EventIntegrityChain.ElementName].AsBsonDocument.Names
                                              .Should().BeEquivalentTo("FormatVersion", "Algorithm", "PreviousHash", "Hash", "SealMode");
        unmarkedEntry[EventIntegrityChain.ElementName].AsBsonDocument.Names
                                                      .Should().BeEquivalentTo("FormatVersion", "Algorithm", "PreviousHash", "Hash", "SealMode");
        (await marked.CreateVerifier().VerifyStreamAsync(markedStream)).IsIntact.Should().BeTrue();
    }

    [OneTimeSetUp]
    public async Task GetMongoDbContainer() => _container = await MongoDbTestContainer.StartContainerAsync();

    [Test]
    public async Task RunPassAsync_LegacyDocumentWithoutMarkedElement_RecordsOnlyResolvedPaths()
    {
        var (unprotected, sealing) = await CreateSharedContextsAsync();
        var aggregateId = await unprotected.AppendStreamAsync(1);
        await unprotected.Events.UpdateOneAsync(
            EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, 1),
            Builders<BsonDocument>.Update.Unset(MemoPath));

        await sealing.Sweep.RunPassAsync();

        ReadCommitments(await sealing.ReadRawEventAsync(aggregateId, 1)).Select(c => c.Path).Should().Equal(NamePath);
        (await sealing.CreateVerifier().VerifyStreamAsync(aggregateId)).IsIntact.Should().BeTrue();
    }

    [Test]
    public async Task RunPassAsync_UnknownDiscriminator_SealsWithoutCommitments()
    {
        var (unprotected, sealing) = await CreateSharedContextsAsync();
        var aggregateId = await unprotected.AppendStreamAsync(1);
        await unprotected.Events.UpdateOneAsync(
            EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, 1),
            Builders<BsonDocument>.Update.Set(EventDocumentFields<LedgerAggregate>.Discriminator, "LedgerUnknown"));

        await sealing.Sweep.RunPassAsync();

        var stored = await sealing.FindEventAsync(aggregateId, 1);
        stored[EventIntegrityChain.ElementName].AsBsonDocument.Contains("PersonalData").Should().BeFalse();
        LedgerStoreContext.ReadIntegrity(stored).SealMode.Should().Be(IntegritySealMode.Retroactive);
        (await sealing.CreateVerifier().VerifyStreamAsync(aggregateId)).IsIntact.Should().BeTrue();
    }

    [Test]
    public async Task RunPassAsync_UnsealedMarkedEvents_RecordsCommitmentsFromHierarchicalDiscriminator()
    {
        var (unprotected, sealing) = await CreateSharedContextsAsync();
        var aggregateId = await unprotected.AppendStreamAsync(2);
        await unprotected.Store.AppendEventsAsync([LedgerStoreContext.CreateAudit(aggregateId, 3)]);

        await sealing.Sweep.RunPassAsync();

        for (var version = 1; version <= 2; version++)
        {
            var stored = await sealing.FindEventAsync(aggregateId, version);
            stored[EventDocumentFields<LedgerAggregate>.Discriminator].Should().BeOfType<BsonArray>();
            var integrity = LedgerStoreContext.ReadIntegrity(stored);
            integrity.SealMode.Should().Be(IntegritySealMode.Retroactive);
            integrity.PersonalData.Should().NotBeNull();
            integrity.PersonalData.Select(c => c.Path).Should().Equal(MemoPath, NamePath);
        }

        LedgerStoreContext.ReadIntegrity(await sealing.FindEventAsync(aggregateId, 3)).PersonalData.Should().BeNull();
        (await sealing.CreateVerifier().VerifyStreamAsync(aggregateId)).Should().Be(StreamVerificationResult.Intact);
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
    public async Task VerifyStreamAsync_ChangedValueWithSaltPresent_ReportsPersonalDataMismatch()
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(3);
        await context.Events.UpdateOneAsync(
            EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, 2),
            Builders<BsonDocument>.Update.Set("Counterparty.Name", "Mallory"));

        var result = await context.CreateVerifier().VerifyStreamAsync(aggregateId);

        result.Should().Be(StreamVerificationResult.Broken(2, StreamVerificationFailure.PersonalDataMismatch));
    }

    [Test]
    public async Task VerifyStreamAsync_DuplicateRecordedPath_ReportsMalformedIntegrity()
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(2);
        var stored = await context.FindEventAsync(aggregateId, 2);
        var entry = stored[EventIntegrityChain.ElementName]["PersonalData"].AsBsonArray[0].AsBsonDocument;
        await context.Events.UpdateOneAsync(
            EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, 2),
            Builders<BsonDocument>.Update.Push($"{EventIntegrityChain.ElementName}.PersonalData", entry));

        var result = await context.CreateVerifier().VerifyStreamAsync(aggregateId);

        result.Should().Be(StreamVerificationResult.Broken(2, StreamVerificationFailure.MalformedIntegrity));
    }

    [Test]
    public async Task VerifyStreamAsync_ErasedValueWithoutSalt_ReportsIntact()
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(3);
        await context.Events.UpdateOneAsync(
            EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, 2),
            Builders<BsonDocument>.Update.Set(MemoPath, BsonNull.Value));
        await context.UnsetSaltAsync(aggregateId, 2, MemoPath);

        var result = await context.CreateVerifier().VerifyStreamAsync(aggregateId);

        result.IsIntact.Should().BeTrue();
        result.FirstBrokenVersion.Should().BeNull();
    }

    [Test]
    public async Task VerifyStreamAsync_EventsSealedWithMarks_VerifyWithStoreWithoutMarks()
    {
        var databaseName = $"IntegrityTestDb_{Guid.NewGuid():N}";
        var marked = await CreateContextAsync(databaseName: databaseName);
        var unmarked = await CreateContextAsync(databaseName: databaseName, personalData: false);
        var aggregateId = await marked.AppendStreamAsync(2);

        var beforeChange = await unmarked.CreateVerifier().VerifyStreamAsync(aggregateId);
        await unmarked.Events.UpdateOneAsync(
            EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, 1),
            Builders<BsonDocument>.Update.Set(MemoPath, "changed"));
        var afterChange = await unmarked.CreateVerifier().VerifyStreamAsync(aggregateId);

        beforeChange.Should().Be(StreamVerificationResult.Intact);
        afterChange.Should().Be(StreamVerificationResult.Broken(1, StreamVerificationFailure.PersonalDataMismatch));
    }

    [Test]
    public async Task VerifyStreamAsync_EventsSealedWithoutMarks_UsesOnlyRecordedPaths()
    {
        var databaseName = $"IntegrityTestDb_{Guid.NewGuid():N}";
        var unmarked = await CreateContextAsync(databaseName: databaseName, personalData: false);
        var marked = await CreateContextAsync(databaseName: databaseName);
        var aggregateId = await unmarked.AppendStreamAsync(2);

        var beforeChange = await marked.CreateVerifier().VerifyStreamAsync(aggregateId);
        await marked.Events.UpdateOneAsync(
            EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, 2),
            Builders<BsonDocument>.Update.Set(MemoPath, "changed"));
        var afterChange = await marked.CreateVerifier().VerifyStreamAsync(aggregateId);

        beforeChange.Should().Be(StreamVerificationResult.Intact);
        afterChange.Should().Be(StreamVerificationResult.Broken(2, StreamVerificationFailure.HashMismatch));
    }

    [Test]
    public async Task VerifyStreamAsync_RemovedSaltWithValuePresent_ReportsErasedPersonalDataPresent()
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(3);
        await context.UnsetSaltAsync(aggregateId, 3, NamePath);

        var result = await context.CreateVerifier().VerifyStreamAsync(aggregateId);

        result.Should().Be(StreamVerificationResult.Broken(3, StreamVerificationFailure.ErasedPersonalDataPresent));
    }

    [Test]
    public async Task VerifyStreamAsync_UnresolvedRecordedPath_ReportsMalformedIntegrity()
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(2);
        await context.Events.UpdateOneAsync(
            EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, 1),
            Builders<BsonDocument>.Update.Set($"{EventIntegrityChain.ElementName}.PersonalData.0.Path", "Nope"));

        var result = await context.CreateVerifier().VerifyStreamAsync(aggregateId);

        result.Should().Be(StreamVerificationResult.Broken(1, StreamVerificationFailure.MalformedIntegrity));
    }

    [Test]
    [TestCase("Salt", 16)]
    [TestCase("Commitment", 31)]
    public async Task VerifyStreamAsync_WrongCommitmentOrSaltLength_ReportsMalformedIntegrity(String member, Int32 length)
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(2);
        await context.Events.UpdateOneAsync(
            EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, 2),
            Builders<BsonDocument>.Update.Set($"{EventIntegrityChain.ElementName}.PersonalData.1.{member}", new BsonBinaryData(new Byte[length])));

        var result = await context.CreateVerifier().VerifyStreamAsync(aggregateId);

        result.Should().Be(StreamVerificationResult.Broken(2, StreamVerificationFailure.MalformedIntegrity));
    }

    private static List<PersonalDataCommitment> ReadCommitments(Byte[] storedBytes)
    {
        var personalData = LedgerStoreContext.ReadIntegrity(BsonSerializer.Deserialize<BsonDocument>(storedBytes)).PersonalData;
        personalData.Should().NotBeNull();
        return personalData;
    }

    private async Task<LedgerStoreContext> CreateContextAsync(Boolean integrityProtection = true,
                                                              Boolean bulkWrite = false,
                                                              String? databaseName = null,
                                                              Boolean personalData = true)
    {
        var context = await LedgerStoreContext.CreateAsync(_container, integrityProtection, bulkWrite, databaseName, personalData: personalData);
        _contexts.Add(context);
        return context;
    }

    private async Task<(LedgerStoreContext Unprotected, LedgerStoreContext Sealing)> CreateSharedContextsAsync()
    {
        var databaseName = $"IntegrityTestDb_{Guid.NewGuid():N}";
        var unprotected = await CreateContextAsync(false, databaseName: databaseName, personalData: false);
        var sealing = await CreateContextAsync(databaseName: databaseName);
        return (unprotected, sealing);
    }
}
