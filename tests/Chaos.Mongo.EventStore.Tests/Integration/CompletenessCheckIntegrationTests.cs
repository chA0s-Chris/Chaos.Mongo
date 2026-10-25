// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Chaos.Mongo.EventStore.Integrity;
using Chaos.Mongo.EventStore.Tests.Integrity;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;
using NUnit.Framework;
using Testcontainers.MongoDb;

public class CompletenessCheckIntegrationTests
{
    private readonly List<LedgerStoreContext> _contexts = [];
    private readonly List<TestTimestampAuthority> _testAuthorities = [];
    private MongoDbContainer _container;

    [Test]
    public async Task CheckAsync_AnchorAwaitingTimestamps_DoesNotCountAsCoverage()
    {
        var testAuthority = CreateTestAuthority();
        testAuthority.Mode = TestTimestampAuthorityMode.HttpFailure;
        var context = await CreateContextAsync(mongo => mongo.WithIntegrityAnchoring(anchoring => anchoring.WithTimestampAuthority(_ => testAuthority.CreateAuthority())));
        var aggregateId = await context.AppendStreamAsync(2);
        var run = context.ServiceProvider.GetRequiredService<AnchoringRun>();
        await run.RunAsync();
        await run.RunAsync();
        (await GetStore(context).Leaves.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty)).Should().Be(1, "the anchor and its leaves exist");

        var findings = await CheckAsync(context);

        findings.Should().ContainSingle().Which.Should().Match<CompletenessFinding>(f => f.Kind == CompletenessFindingKind.Unanchored &&
                                                                                         f.AggregateId == aggregateId &&
                                                                                         f.FirstVersion == 1);
    }

    [Test]
    [TestCase("prefix", 1)]
    [TestCase("anchoredHead", 3)]
    public async Task CheckAsync_AnchoredVersionsDeletedBelowANewerHead_ReportsTruncatedStream(String deleted, Int64 firstMissing)
    {
        var context = await CreateAnchoredContextAsync();
        var aggregateId = await AnchorNewStreamAsync(context, 3);
        await context.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(aggregateId, 4)]);
        await context.Events.DeleteManyAsync(deleted == "prefix"
                                                 ? Builders<BsonDocument>.Filter.And(EventDocumentFields<LedgerAggregate>.ForAggregate<BsonDocument>(aggregateId),
                                                                                     Builders<BsonDocument>.Filter.Lte("Version", 3))
                                                 : EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, 3));

        var findings = await CheckAsync(context);

        findings.Should().ContainEquivalentOf(new
        {
            Kind = CompletenessFindingKind.StreamTruncated,
            AggregateId = (Guid?)aggregateId,
            FirstVersion = (Int64?)firstMissing,
            LastVersion = (Int64?)3,
            HeadVersion = (Int64?)4
        });
    }

    [Test]
    public async Task CheckAsync_BrokenAnchorChain_ReportsItFirst()
    {
        var context = await CreateAnchoredContextAsync();
        await AnchorNewStreamAsync(context);
        await AnchorNewStreamAsync(context);
        await GetStore(context).Anchors.DeleteOneAsync(new BsonDocument(AnchorStore.SequenceField, 1L));

        var findings = await CheckAsync(context);

        findings[0].Kind.Should().Be(CompletenessFindingKind.AnchorChainBroken);
        findings[0].AnchorChain.Should().Be(new AnchorChainVerificationResult(1, AnchorChainFailure.SequenceGap, 0));
    }

    [Test]
    public async Task CheckAsync_DeletedAndTruncatedStreams_AreReported()
    {
        var context = await CreateAnchoredContextAsync();
        var deleted = await AnchorNewStreamAsync(context, 2);
        var truncated = await AnchorNewStreamAsync(context, 3);
        await context.Events.DeleteManyAsync(EventDocumentFields<LedgerAggregate>.ForAggregate<BsonDocument>(deleted));
        await context.Events.DeleteOneAsync(EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(truncated, 3));

        var findings = await CheckAsync(context);

        findings.Should().HaveCount(2);
        findings.Should().ContainEquivalentOf(new
        {
            Kind = CompletenessFindingKind.StreamDeleted,
            AggregateId = (Guid?)deleted,
            FirstVersion = (Int64?)1,
            LastVersion = (Int64?)2,
            HeadVersion = (Int64?)0
        });
        findings.Should().ContainEquivalentOf(new
        {
            Kind = CompletenessFindingKind.StreamTruncated,
            AggregateId = (Guid?)truncated,
            FirstVersion = (Int64?)3,
            LastVersion = (Int64?)3,
            HeadVersion = (Int64?)2
        });
        findings.Should().OnlyContain(f => f.Anchor != null);
    }

    [Test]
    public async Task CheckAsync_DeletedStreamWithItsLeaves_ReportsBrokenAnchorChain()
    {
        var context = await CreateAnchoredContextAsync();
        var aggregateId = await AnchorNewStreamAsync(context, 2);
        await context.Events.DeleteManyAsync(EventDocumentFields<LedgerAggregate>.ForAggregate<BsonDocument>(aggregateId));
        await GetStore(context).Leaves.DeleteManyAsync(new BsonDocument("AggregateId", new BsonBinaryData(aggregateId, GuidRepresentation.Standard)));

        var findings = await CheckAsync(context);

        findings.Should().ContainSingle().Which.AnchorChain.Should().Be(new AnchorChainVerificationResult(1, AnchorChainFailure.LeafSetMismatch, 0));
    }

    [Test]
    [TestCase("shortUuid")]
    [TestCase("boolean")]
    public async Task CheckAsync_EventWithUnreadableIdentifier_ReportsItWhereTheWalkMeetsIt(String identifier)
    {
        var context = await CreateContextAsync(mongo => mongo.WithIntegrityVerification());
        var aggregateId = await context.AppendStreamAsync(1);
        // A UUID that is not 16 bytes long makes the driver throw when it reads the value and sorts before the stream,
        // because MongoDB orders binary data by length first; a boolean sorts after every UUID.
        Byte[] value = identifier == "shortUuid" ? [0x05, .. "AggregateId"u8, 0x00, .. BitConverter.GetBytes(15), 0x04, .. new Byte[15]] : [0x08, .. "AggregateId"u8, 0x00, 0x01];
        Byte[] fields = [.. value, 0x12, .. "Version"u8, 0x00, .. BitConverter.GetBytes(1L)];
        await context.MongoHelper.Database.GetCollection<RawBsonDocument>(context.Options.EventsCollectionName)
                     .InsertOneAsync(new RawBsonDocument([.. BitConverter.GetBytes(fields.Length + 5), .. fields, 0x00]));

        var findings = await CheckAsync(context);

        (CompletenessFindingKind, Guid?) unanchored = (CompletenessFindingKind.Unanchored, aggregateId);
        (CompletenessFindingKind, Guid?) malformed = (CompletenessFindingKind.MalformedEvent, null);
        findings.Select(f => (f.Kind, f.AggregateId)).Should().Equal(identifier == "shortUuid" ? [malformed, unanchored] : [unanchored, malformed]);
    }

    [Test]
    public async Task CheckAsync_FullyAnchoredStreams_ReportsNothing()
    {
        var context = await CreateAnchoredContextAsync();
        await AnchorNewStreamAsync(context, 2);
        await AnchorNewStreamAsync(context, 1);

        (await CheckAsync(context)).Should().BeEmpty();
    }

    [Test]
    public async Task CheckAsync_LeafRepeatingAnElementName_ReportsBrokenAnchorChainInsteadOfFailing()
    {
        var context = await CreateAnchoredContextAsync();
        await AnchorNewStreamAsync(context, 2);
        var leaves = GetStore(context).Leaves;
        var leaf = await leaves.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        var repeated = new BsonDocument
        {
            AllowDuplicateNames = true
        };
        repeated.AddRange(leaf);
        repeated.Add("Hash", leaf["Hash"]);
        await leaves.DeleteOneAsync(new BsonDocument("_id", leaf["_id"]));
        await leaves.Database.GetCollection<RawBsonDocument>(leaves.CollectionNamespace.CollectionName).InsertOneAsync(new RawBsonDocument(repeated.ToBson()));

        var findings = await CheckAsync(context);

        findings[0].AnchorChain.Should().Be(new AnchorChainVerificationResult(1, AnchorChainFailure.LeafSetMismatch, 0));
    }

    [Test]
    public async Task CheckAsync_MalformedEventAfterDeletedStream_ReportsFindingsInIdentifierOrder()
    {
        var deleted = Guid.Parse("00000001-0000-0000-0000-000000000000");
        var malformed = Guid.Parse("00000002-0000-0000-0000-000000000000");
        var context = await CreateAnchoredContextAsync();
        foreach (var aggregateId in new[]
                 {
                     deleted,
                     malformed
                 })
        {
            await context.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(aggregateId, 1), LedgerStoreContext.CreateEntry(aggregateId, 2)]);
            await context.RunUntilAnchoredAsync(aggregateId, 2);
        }

        await context.Events.DeleteManyAsync(EventDocumentFields<LedgerAggregate>.ForAggregate<BsonDocument>(deleted));
        await context.Events.UpdateOneAsync(EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(malformed, 2),
                                            Builders<BsonDocument>.Update.Set("Version", "2"));

        var findings = await CheckAsync(context);

        findings.Select(f => (f.Kind, f.AggregateId))
                .Should().Equal((CompletenessFindingKind.StreamDeleted, deleted),
                                (CompletenessFindingKind.MalformedEvent, malformed),
                                (CompletenessFindingKind.StreamTruncated, malformed));
    }

    [Test]
    public async Task CheckAsync_MalformedEvents_AreReported()
    {
        var context = await CreateContextAsync(mongo => mongo.WithIntegrityVerification());
        var withStringVersion = await context.AppendStreamAsync(2);
        var withOtherGuidRepresentation = await context.AppendStreamAsync(1);
        await context.Events.UpdateOneAsync(EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(withStringVersion, 2),
                                            Builders<BsonDocument>.Update.Set("Version", "2"));
        await context.Events.UpdateOneAsync(EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(withOtherGuidRepresentation, 1),
                                            Builders<BsonDocument>.Update.Set("AggregateId",
                                                                              new BsonBinaryData(withOtherGuidRepresentation.ToByteArray(true),
                                                                                                 BsonBinarySubType.Binary)));

        var findings = await CheckAsync(context);

        findings.Where(f => f.Kind == CompletenessFindingKind.MalformedEvent)
                .Should().BeEquivalentTo([
                    new CompletenessFinding(CompletenessFindingKind.MalformedEvent, withStringVersion, null, null, null, null, null, null),
                    new CompletenessFinding(CompletenessFindingKind.MalformedEvent, null, 1, 1, null, null, null, null)
                ]);
        findings.Should().Contain(f => f.Kind == CompletenessFindingKind.Unanchored && f.AggregateId == withStringVersion && f.LastVersion == 1);
    }

    [Test]
    public async Task CheckAsync_NeverAnchoredStream_ReportsUnanchoredVersionsWithAge()
    {
        var now = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(now);
        var context = await CreateContextAsync(mongo => mongo.WithIntegrityVerification(), timeProvider: timeProvider);
        var aggregateId = Guid.NewGuid();
        await context.Store.AppendEventsAsync([
            LedgerStoreContext.CreateEntry(aggregateId, 1, createdUtc: now.UtcDateTime.AddHours(-3)),
            LedgerStoreContext.CreateEntry(aggregateId, 2, createdUtc: now.UtcDateTime.AddHours(-1))
        ]);

        var findings = await CheckAsync(context);

        findings.Should().ContainSingle().Which.Should().Be(new CompletenessFinding(CompletenessFindingKind.Unanchored,
                                                                                    aggregateId,
                                                                                    1,
                                                                                    2,
                                                                                    2,
                                                                                    TimeSpan.FromHours(3),
                                                                                    null,
                                                                                    null));
    }

    [Test]
    public async Task CheckAsync_NewestAnchorDeletedWithItsLeavesAndStream_ReportsMissingTrustedHead()
    {
        var context = await CreateAnchoredContextAsync();
        await AnchorNewStreamAsync(context);
        var newest = await AnchorNewStreamAsync(context, 2);
        var store = GetStore(context);
        var head = AnchorStore.ReadAnchor(await store.Anchors.Find(new BsonDocument(AnchorStore.SequenceField, 2L)).SingleAsync());
        await store.Anchors.DeleteOneAsync(new BsonDocument(AnchorStore.SequenceField, 2L));
        await store.Leaves.DeleteManyAsync(new BsonDocument(AnchorStore.AnchorIdField, new BsonBinaryData(head.Id, GuidRepresentation.Standard)));
        await context.Events.DeleteManyAsync(EventDocumentFields<LedgerAggregate>.ForAggregate<BsonDocument>(newest));

        var withoutHead = await CheckAsync(context);
        var withHead = await CheckAsync(context, new TrustedAnchorHead(2, head.AnchorDigest));

        withoutHead.Should().BeEmpty("without a trusted head the shortened chain is consistent");
        withHead.Should().ContainSingle().Which.AnchorChain.Should().Be(new AnchorChainVerificationResult(2, AnchorChainFailure.TrustedHeadMissing, 1));
    }

    [Test]
    public async Task CheckAsync_OrderOfStoredIdentifiers_FindsDeletedStreamBetweenLiveStreams()
    {
        // MongoDB orders standard UUIDs by their RFC 9562 bytes, the order the walk merges in. The little-endian bytes
        // of Guid.ToByteArray() order these identifiers the other way round, so a walk in that order would misplace
        // the deleted stream between the live ones.
        var first = Guid.Parse("00000001-0000-0000-0000-000000000000");
        var deleted = Guid.Parse("00000100-0000-0000-0000-000000000000");
        var last = Guid.Parse("00010000-0000-0000-0000-000000000000");
        first.ToByteArray().AsSpan().SequenceCompareTo(last.ToByteArray()).Should().BePositive();
        var context = await CreateAnchoredContextAsync();
        foreach (var aggregateId in new[]
                 {
                     last,
                     deleted,
                     first
                 })
        {
            await context.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(aggregateId, 1)]);
            await context.RunUntilAnchoredAsync(aggregateId, 1);
        }

        await context.Events.DeleteManyAsync(EventDocumentFields<LedgerAggregate>.ForAggregate<BsonDocument>(deleted));

        var findings = await CheckAsync(context);

        findings.Select(f => (f.Kind, f.AggregateId)).Should().Equal((CompletenessFindingKind.StreamDeleted, deleted));
    }

    [Test]
    public async Task CheckAsync_PartiallyAnchoredStream_ReportsFirstUnanchoredVersion()
    {
        var context = await CreateAnchoredContextAsync();
        var aggregateId = await AnchorNewStreamAsync(context, 2);
        await context.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(aggregateId, 3), LedgerStoreContext.CreateEntry(aggregateId, 4)]);

        var finding = (await CheckAsync(context)).Should().ContainSingle().Subject;

        finding.Kind.Should().Be(CompletenessFindingKind.Unanchored);
        finding.FirstVersion.Should().Be(3);
        finding.LastVersion.Should().Be(4);
        finding.Age.Should().NotBeNull();
    }

    [Test]
    public async Task CheckAsync_RetroactivelySealedStream_ReportsProtectedSinceAnchor()
    {
        var databaseName = $"IntegrityTestDb_{Guid.NewGuid():N}";
        var unprotected = await LedgerStoreContext.CreateAsync(_container, false, databaseName: databaseName);
        _contexts.Add(unprotected);
        var context = await CreateAnchoredContextAsync(databaseName);
        var aggregateId = await unprotected.AppendStreamAsync(3);
        await context.Sweep.RunPassAsync();
        await context.RunUntilAnchoredAsync(aggregateId, 3);
        var anchor = (await context.CreateVerifier().VerifyEventAnchorAsync(aggregateId, 1)).Anchor;
        await context.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(aggregateId, 4)]);
        await context.RunUntilAnchoredAsync(aggregateId, 4);

        var findings = await CheckAsync(context);

        findings.Should().ContainSingle().Which.Should().Be(new CompletenessFinding(CompletenessFindingKind.RetroactivelyProtected,
                                                                                    aggregateId,
                                                                                    1,
                                                                                    3,
                                                                                    4,
                                                                                    null,
                                                                                    anchor,
                                                                                    null));
    }

    [Test]
    public async Task CheckAsync_TrustedHeadWithoutPositiveSequence_ThrowsArgumentExceptionAtTheCall()
    {
        var context = await CreateAnchoredContextAsync();

        var act = () => context.ServiceProvider.GetRequiredService<CompletenessChecker<LedgerAggregate>>().CheckAsync(new TrustedAnchorHead(0, new Byte[32]));

        act.Should().Throw<ArgumentException>().WithParameterName("trustedHead");
    }

    [Test]
    public async Task CheckAsync_UnanchoredEventsCreatedOutOfOrder_ReportsAgeOfTheOldest()
    {
        var now = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        var context = await CreateContextAsync(mongo => mongo.WithIntegrityVerification(), timeProvider: new FakeTimeProvider(now));
        var aggregateId = Guid.NewGuid();
        await context.Store.AppendEventsAsync([
            LedgerStoreContext.CreateEntry(aggregateId, 1, createdUtc: now.UtcDateTime.AddHours(-1)),
            LedgerStoreContext.CreateEntry(aggregateId, 2, createdUtc: now.UtcDateTime.AddHours(-3))
        ]);

        var finding = (await CheckAsync(context)).Should().ContainSingle().Subject;

        finding.Kind.Should().Be(CompletenessFindingKind.Unanchored);
        finding.FirstVersion.Should().Be(1);
        finding.Age.Should().Be(TimeSpan.FromHours(3));
    }

    [Test]
    public async Task CheckAsync_UnsealedStream_ReportsUnsealedAndUnanchoredVersions()
    {
        var databaseName = $"IntegrityTestDb_{Guid.NewGuid():N}";
        var unprotected = await LedgerStoreContext.CreateAsync(_container, false, databaseName: databaseName);
        _contexts.Add(unprotected);
        var context = await CreateAnchoredContextAsync(databaseName);
        var aggregateId = await unprotected.AppendStreamAsync(2);

        var findings = await CheckAsync(context);

        findings.Select(f => (f.Kind, f.FirstVersion, f.LastVersion))
                .Should().Equal((CompletenessFindingKind.Unsealed, 1L, 2L), (CompletenessFindingKind.Unanchored, 1L, 2L));
        findings.Should().OnlyContain(f => f.AggregateId == aggregateId);
    }

    [Test]
    public async Task CheckAsync_WithoutAnchors_ThrowsInvalidOperationExceptionAtTheCall()
    {
        var context = await CreateContextAsync();

        var act = () => context.ServiceProvider.GetRequiredService<CompletenessChecker<LedgerAggregate>>().CheckAsync();

        act.Should().Throw<InvalidOperationException>().WithMessage("*needs anchors*");
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

        foreach (var testAuthority in _testAuthorities)
        {
            testAuthority.Dispose();
        }

        _contexts.Clear();
        _testAuthorities.Clear();
    }

    private static async Task<List<CompletenessFinding>> CheckAsync(LedgerStoreContext context, TrustedAnchorHead? trustedHead = null)
    {
        var findings = new List<CompletenessFinding>();
        await foreach (var finding in context.ServiceProvider.GetRequiredService<CompletenessChecker<LedgerAggregate>>().CheckAsync(trustedHead))
        {
            findings.Add(finding);
        }

        return findings;
    }

    private static AnchorStore GetStore(LedgerStoreContext context) => context.ServiceProvider.GetRequiredService<AnchorVerifier>().Store;

    private async Task<Guid> AnchorNewStreamAsync(LedgerStoreContext context, Int32 eventCount = 1)
    {
        var aggregateId = await context.AppendStreamAsync(eventCount);
        await context.RunUntilAnchoredAsync(aggregateId, eventCount);
        return aggregateId;
    }

    private async Task<LedgerStoreContext> CreateAnchoredContextAsync(String? databaseName = null)
    {
        var testAuthority = CreateTestAuthority();
        return await CreateContextAsync(mongo => mongo.WithIntegrityAnchoring(anchoring => anchoring.WithTimestampAuthority(_ => testAuthority.CreateAuthority())),
                                        databaseName);
    }

    private async Task<LedgerStoreContext> CreateContextAsync(Action<MongoBuilder>? configureMongo = null,
                                                              String? databaseName = null,
                                                              TimeProvider? timeProvider = null)
    {
        var context = await LedgerStoreContext.CreateAsync(_container,
                                                           databaseName: databaseName,
                                                           configureMongo: configureMongo,
                                                           timeProvider: timeProvider);
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
