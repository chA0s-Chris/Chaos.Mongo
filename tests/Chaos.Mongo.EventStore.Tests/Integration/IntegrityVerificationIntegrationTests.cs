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
using System.Security.Cryptography.X509Certificates;
using Testcontainers.MongoDb;

public class IntegrityVerificationIntegrationTests
{
    private const String AuthorityName = "test-tsa";
    private const String MemoPath = "Memo";
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
    public async Task VerifyAnchorChainAsync_ChangedAnchor_ReportsFirstBrokenSequence(
        [Values("delete", "root", "previous", "unset", "format", "status", "anchorId")]
        String change)
    {
        var (context, _) = await CreateAnchoredContextAsync();
        await AnchorNewStreamAsync(context);
        await AnchorNewStreamAsync(context);
        var anchors = GetStore(context).Anchors;
        var bySequence = (Int64 anchorSequence) => new BsonDocument(AnchorStore.SequenceField, anchorSequence);
        var (sequence, failure) = change switch
        {
            "delete" => (1L, AnchorChainFailure.SequenceGap),
            "root" => (2L, AnchorChainFailure.AnchorDigestMismatch),
            "previous" => (2L, AnchorChainFailure.PreviousDigestMismatch),
            "unset" => (1L, AnchorChainFailure.MalformedAnchor),
            "status" => (1L, AnchorChainFailure.MalformedAnchor),
            _ => (2L, AnchorChainFailure.MalformedAnchor)
        };
        switch (change)
        {
            case "delete":
                await anchors.DeleteOneAsync(bySequence(1));
                break;
            case "root":
                await anchors.UpdateOneAsync(bySequence(2), Builders<BsonDocument>.Update.Set("MerkleRoot", new BsonBinaryData(new Byte[32])));
                break;
            case "previous":
                await anchors.UpdateOneAsync(bySequence(2), Builders<BsonDocument>.Update.Set("PreviousDigest", new BsonBinaryData(new Byte[32])));
                break;
            case "unset":
                await anchors.UpdateOneAsync(bySequence(1), Builders<BsonDocument>.Update.Unset("MerkleRoot"));
                break;
            case "status":
                // Hides the anchor's leaves from stream verification, which only uses completed anchors.
                await anchors.UpdateOneAsync(bySequence(1), Builders<BsonDocument>.Update.Set(AnchorStore.StatusField, nameof(AnchorStatus.AwaitingTimestamp)));
                break;
            case "anchorId":
                var anchor = await anchors.Find(bySequence(2)).SingleAsync();
                await anchors.DeleteOneAsync(bySequence(2));
                anchor["_id"] = new BsonBinaryData(anchor["_id"].AsGuid.ToByteArray(), BsonBinarySubType.Binary);
                await anchors.InsertOneAsync(anchor);
                break;
            default:
                await anchors.UpdateOneAsync(bySequence(2), Builders<BsonDocument>.Update.Set("FormatVersion", 2));
                break;
        }

        var result = await GetAnchorVerifier(context).VerifyAnchorChainAsync();

        result.Should().Be(new AnchorChainVerificationResult(sequence, failure, sequence - 1));
    }

    [Test]
    public async Task VerifyAnchorChainAsync_ChangedLeaves_ReportsLeafSetMismatch(
        [Values("hash", "delete", "add", "version", "anchorId", "sequence")]
        String change)
    {
        var (context, _) = await CreateAnchoredContextAsync();
        await AnchorNewStreamAsync(context);
        var aggregateId = await context.AppendStreamAsync(1);
        await RunUntilAnchoredAsync(context, aggregateId, 1);
        var leaves = GetStore(context).Leaves;
        var leafFilter = new BsonDocument("AggregateId", new BsonBinaryData(aggregateId, GuidRepresentation.Standard));
        switch (change)
        {
            case "hash":
                await leaves.UpdateOneAsync(leafFilter, Builders<BsonDocument>.Update.Set("Hash", new BsonBinaryData(new Byte[32])));
                break;
            case "delete":
                // Deleting a stream together with its leaves leaves its anchor's root unexplained.
                await leaves.DeleteOneAsync(leafFilter);
                break;
            case "add":
                var extra = await leaves.Find(leafFilter).SingleAsync();
                extra["_id"] = ObjectId.GenerateNewId();
                extra["Index"] = 1;
                await leaves.InsertOneAsync(extra);
                break;
            case "version":
                await leaves.UpdateOneAsync(leafFilter, Builders<BsonDocument>.Update.Set("Version", 0L));
                break;
            case "anchorId":
                var leaf = await leaves.Find(leafFilter).SingleAsync();
                await leaves.UpdateOneAsync(leafFilter, Builders<BsonDocument>.Update.Set(AnchorStore.AnchorIdField,
                                                                                          new BsonBinaryData(leaf[AnchorStore.AnchorIdField].AsGuid.ToByteArray(),
                                                                                                             BsonBinarySubType.Binary)));
                break;
            default:
                await leaves.UpdateOneAsync(leafFilter, Builders<BsonDocument>.Update.Set(AnchorStore.AnchorSequenceField, 1L));
                break;
        }

        var result = await GetAnchorVerifier(context).VerifyAnchorChainAsync();
        var stream = await context.CreateVerifier().VerifyStreamAsync(aggregateId);

        result.Should().Be(new AnchorChainVerificationResult(2, AnchorChainFailure.LeafSetMismatch, 1));
        stream.AnchorCoverageChecked.Should().BeTrue("damaged leaves are skipped or reported, never thrown");
    }

    [Test]
    public async Task VerifyAnchorChainAsync_IntactChain_ReportsEveryAnchor()
    {
        var (context, _) = await CreateAnchoredContextAsync();
        var verifier = GetAnchorVerifier(context);
        var empty = await verifier.VerifyAnchorChainAsync();
        await AnchorNewStreamAsync(context);
        await AnchorNewStreamAsync(context);

        var result = await verifier.VerifyAnchorChainAsync();

        empty.Should().Be(new AnchorChainVerificationResult(null, null, 0));
        result.Should().Be(new AnchorChainVerificationResult(null, null, 2));
        result.IsIntact.Should().BeTrue();
    }

    [Test]
    public async Task VerifyAnchorChainAsync_TrustedHead_DetectsDeletedNewestAnchorsAndChangedDigests()
    {
        var (context, _) = await CreateAnchoredContextAsync();
        await AnchorNewStreamAsync(context);
        await AnchorNewStreamAsync(context);
        var store = GetStore(context);
        var head = AnchorStore.ReadAnchor(await store.Anchors.Find(new BsonDocument(AnchorStore.SequenceField, 2L)).SingleAsync());
        var verifier = GetAnchorVerifier(context);

        var intact = await verifier.VerifyAnchorChainAsync(new TrustedAnchorHead(2, head.AnchorDigest));
        var mismatch = await verifier.VerifyAnchorChainAsync(new TrustedAnchorHead(1, head.AnchorDigest));
        await store.Anchors.DeleteOneAsync(new BsonDocument(AnchorStore.SequenceField, 2L));
        var withoutHead = await verifier.VerifyAnchorChainAsync();
        var missing = await verifier.VerifyAnchorChainAsync(new TrustedAnchorHead(2, head.AnchorDigest));
        var invalidHead = () => verifier.VerifyAnchorChainAsync(new TrustedAnchorHead(0, head.AnchorDigest));

        intact.IsIntact.Should().BeTrue();
        mismatch.Should().Be(new AnchorChainVerificationResult(1, AnchorChainFailure.TrustedHeadMismatch, 2));
        withoutHead.IsIntact.Should().BeTrue("deleting the newest anchor leaves a consistent chain");
        missing.Should().Be(new AnchorChainVerificationResult(2, AnchorChainFailure.TrustedHeadMissing, 1));
        await invalidHead.Should().ThrowAsync<ArgumentException>();
    }

    [Test]
    public async Task VerifyEventAnchorAsync_AnchoredEvent_VerifiesInclusionDigestAndTimestamps()
    {
        var (context, _) = await CreateAnchoredContextAsync();
        var verifier = context.CreateVerifier();
        var aggregateId = await context.AppendStreamAsync(3);
        var beforeAnchoring = await verifier.VerifyEventAnchorAsync(aggregateId, 1);
        await RunUntilAnchoredAsync(context, aggregateId, 3);

        var result = await verifier.VerifyEventAnchorAsync(aggregateId, 1);

        beforeAnchoring.Should().Be(EventAnchorVerificationResult.NotAnchored);
        result.Status.Should().Be(EventAnchorStatus.Anchored);
        result.Failure.Should().BeNull();
        result.LeafVersion.Should().Be(3);
        result.Anchor.Should().NotBeNull();
        result.Timestamps.Should().ContainSingle().Which.Should().Match<TimestampVerificationResult>(t => t.IsValid && t.Authority == AuthorityName);
    }

    [Test]
    public async Task VerifyEventAnchorAsync_ChangedEvidence_ReportsFailure(
        [Values("event", "leafHash", "proof", "digest", "evidence", "tokenHash")]
        String change)
    {
        var (context, _) = await CreateAnchoredContextAsync();
        var aggregateId = await context.AppendStreamAsync(3);
        await RunUntilAnchoredAsync(context, aggregateId, 3);
        var store = GetStore(context);
        var leafFilter = new BsonDocument("AggregateId", new BsonBinaryData(aggregateId, GuidRepresentation.Standard));
        var expected = change switch
        {
            "event" => EventAnchorFailure.ChainBroken,
            "leafHash" => EventAnchorFailure.LeafHashMismatch,
            "proof" => EventAnchorFailure.InclusionProofInvalid,
            "digest" => EventAnchorFailure.AnchorDigestMismatch,
            _ => EventAnchorFailure.NoValidTimestamp
        };
        switch (change)
        {
            case "event":
                await context.Events.UpdateOneAsync(EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, 2),
                                                    Builders<BsonDocument>.Update.Set(MemoPath, "forged"));
                break;
            case "leafHash":
                await store.Leaves.UpdateOneAsync(leafFilter, Builders<BsonDocument>.Update.Set("Hash", new BsonBinaryData(new Byte[32])));
                break;
            case "proof":
                await store.Leaves.UpdateOneAsync(leafFilter, Builders<BsonDocument>.Update.Set("Proof", new BsonArray
                {
                    new BsonBinaryData(new Byte[32])
                }));
                break;
            case "digest":
                await store.Anchors.UpdateManyAsync(FilterDefinition<BsonDocument>.Empty,
                                                    Builders<BsonDocument>.Update.Set(AnchorStore.AnchorDigestField, new BsonBinaryData(new Byte[32])));
                break;
            case "tokenHash":
                await store.Anchors.UpdateManyAsync(FilterDefinition<BsonDocument>.Empty,
                                                    Builders<BsonDocument>.Update.Set("Timestamps.0.TokenHash", new BsonBinaryData(new Byte[16])));
                break;
            default:
                await context.MongoHelper.Database.GetCollection<BsonDocument>(context.ServiceProvider.GetRequiredService<IntegrityAnchoringOptions>()
                                                                                      .AnchorEvidenceCollectionName)
                             .DeleteManyAsync(FilterDefinition<BsonDocument>.Empty);
                break;
        }

        var result = await context.CreateVerifier().VerifyEventAnchorAsync(aggregateId, 1);

        result.Status.Should().Be(EventAnchorStatus.Failed);
        result.Failure.Should().Be(expected);
        if (change is "evidence" or "tokenHash")
        {
            result.Timestamps.Should().ContainSingle().Which.Failure.Should().Contain(change == "evidence" ? "missing" : "malformed evidence");
        }
    }

    [Test]
    public async Task VerifyEventAnchorAsync_TrustFromRfc3161Authority_VerifiesWithoutVerificationConfiguration()
    {
        var testAuthority = CreateTestAuthority();
        var context = await CreateContextAsync(mongo => mongo.WithIntegrityAnchoring(anchoring => anchoring.WithRfc3161TimestampAuthority(
                                                                                         AuthorityName,
                                                                                         TestTimestampAuthority.TimestampUri,
                                                                                         options =>
                                                                                         {
                                                                                             options.HttpClientFactory = _ => new HttpClient(testAuthority, false);
                                                                                             options.RevocationMode = X509RevocationMode.NoCheck;
                                                                                             options.TrustedRoots.Add(testAuthority.Root);
                                                                                             options.PolicyId = TestTimestampAuthority.DefaultPolicyId;
                                                                                         })));
        var aggregateId = await context.AppendStreamAsync(1);
        await RunUntilAnchoredAsync(context, aggregateId, 1);

        var result = await context.CreateVerifier().VerifyEventAnchorAsync(aggregateId, 1);

        result.Status.Should().Be(EventAnchorStatus.Anchored);
    }

    [Test]
    public async Task VerifyEventAnchorAsync_VerificationOnlyContainer_VerifiesAnchorsOfAnotherContainer()
    {
        var databaseName = $"IntegrityTestDb_{Guid.NewGuid():N}";
        var (anchoring, testAuthority) = await CreateAnchoredContextAsync(databaseName);
        var aggregateId = await anchoring.AppendStreamAsync(2);
        await RunUntilAnchoredAsync(anchoring, aggregateId, 2);
        var verifying = await CreateContextAsync(mongo => mongo.WithIntegrityVerification(verification => verification
                                                                                                          .WithTimestampAuthorityTrust(
                                                                                                              AuthorityName, trust => trust.TrustedRoots.Add(testAuthority.Root))
                                                                                                          .WithRevocationGracePeriod(TimeSpan.FromHours(48))),
                                                 databaseName);

        var anchor = await verifying.CreateVerifier().VerifyEventAnchorAsync(aggregateId, 2);
        var stream = await verifying.CreateVerifier().VerifyStreamAsync(aggregateId);

        verifying.ServiceProvider.GetService<AnchoringRun>().Should().BeNull();
        anchor.Status.Should().Be(EventAnchorStatus.Anchored);
        stream.AnchorCoverageChecked.Should().BeTrue();
        stream.Events.Should().OnlyContain(e => e.SealingAnchor != null);
    }

    [Test]
    public async Task VerifyEventAnchorAsync_WithoutAnchoring_ReturnsNotChecked()
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(1);

        (await context.CreateVerifier().VerifyEventAnchorAsync(aggregateId, 1)).Should().Be(EventAnchorVerificationResult.NotChecked);
    }

    [Test]
    public async Task VerifyEventAnchorAsync_WithoutTrustSettings_ReportsNoValidTimestamp()
    {
        var testAuthority = CreateTestAuthority();
        var context = await CreateContextAsync(mongo => mongo.WithIntegrityAnchoring(anchoring => anchoring.WithTimestampAuthority(_ => testAuthority.CreateAuthority())));
        var aggregateId = await context.AppendStreamAsync(1);
        await RunUntilAnchoredAsync(context, aggregateId, 1);

        var result = await context.CreateVerifier().VerifyEventAnchorAsync(aggregateId, 1);

        result.Status.Should().Be(EventAnchorStatus.Failed);
        result.Failure.Should().Be(EventAnchorFailure.NoValidTimestamp);
        result.Timestamps.Should().ContainSingle().Which.Failure.Should().Contain("no trust settings");
    }

    [Test]
    public async Task VerifyStreamAsync_AnchoredStream_ReturnsPerVersionIntegrityData()
    {
        var (context, _) = await CreateAnchoredContextAsync();
        var verifier = context.CreateVerifier();
        var aggregateId = await context.AppendStreamAsync(3);
        await RunUntilAnchoredAsync(context, aggregateId, 3);
        await context.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(aggregateId, 4)]);
        var anchor = (await verifier.VerifyEventAnchorAsync(aggregateId, 1)).Anchor;

        var result = await verifier.VerifyStreamAsync(aggregateId);

        result.IsIntact.Should().BeTrue();
        result.AnchorCoverageChecked.Should().BeTrue();
        result.SealModeTransitions.Should().BeEmpty();
        result.Events.Select(e => e.Version).Should().Equal(1, 2, 3, 4);
        result.Events.Select(e => e.SealingAnchor).Should().Equal(anchor, anchor, anchor, null);
        for (var version = 1; version <= 4; version++)
        {
            var stored = LedgerStoreContext.ReadIntegrity(await context.FindEventAsync(aggregateId, version));
            var verified = result.Events[version - 1];
            verified.Hash.Should().Equal(stored.Hash);
            verified.PreviousHash.Should().Equal(stored.PreviousHash);
            verified.SealMode.Should().Be(IntegritySealMode.Append);
            verified.PersonalData.Should().BeEmpty();
        }
    }

    [Test]
    [TestCase(false, 3)]
    [TestCase(true, 1)]
    public async Task VerifyStreamAsync_AnchoredVersionDeleted_ReportsAnchoredVersionMissing(Boolean deleteWholeStream, Int64 brokenVersion)
    {
        var (context, _) = await CreateAnchoredContextAsync();
        var aggregateId = await context.AppendStreamAsync(3);
        await RunUntilAnchoredAsync(context, aggregateId, 3);
        await context.Events.DeleteManyAsync(Builders<BsonDocument>.Filter.And(
                                                 EventDocumentFields<LedgerAggregate>.ForAggregate<BsonDocument>(aggregateId),
                                                 Builders<BsonDocument>.Filter.Gt("Version", deleteWholeStream ? 0 : 2)));

        var result = await context.CreateVerifier().VerifyStreamAsync(aggregateId);

        result.Should().Be(StreamVerificationResult.Broken(brokenVersion, StreamVerificationFailure.AnchoredVersionMissing));
    }

    [Test]
    public async Task VerifyStreamAsync_ErasedValues_ReportsPersonalDataStatus()
    {
        var context = await CreateContextAsync(personalData: true);
        var aggregateId = await context.AppendStreamAsync(3);
        await context.Events.UpdateOneAsync(EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, 2),
                                            Builders<BsonDocument>.Update.Set(MemoPath, BsonNull.Value));
        await context.UnsetSaltAsync(aggregateId, 2, MemoPath);
        await context.Eraser.ErasePersonalDataAsync(aggregateId, new ErasureRequest("ticket", [1], [MemoPath]));

        var result = await context.CreateVerifier().VerifyStreamAsync(aggregateId);

        result.IsIntact.Should().BeTrue();
        result.Events.Should().HaveCount(4);
        StatusOf(result, 1, MemoPath).Should().Be(PersonalDataStatus.ErasedWithRecord);
        StatusOf(result, 1, "Counterparty.Name").Should().Be(PersonalDataStatus.Present);
        StatusOf(result, 2, MemoPath).Should().Be(PersonalDataStatus.ErasedWithoutRecord);
        StatusOf(result, 3, MemoPath).Should().Be(PersonalDataStatus.Present);
        result.Events[3].PersonalData.Should().BeEmpty();
        result.UnrecordedErasures.Should().Equal(new UnrecordedErasure(2, MemoPath));
    }

    [Test]
    public async Task VerifyStreamAsync_LeafRewrittenWithStream_ReportsAnchoredLeafInvalid()
    {
        var (context, _) = await CreateAnchoredContextAsync();
        var aggregateId = await context.AppendStreamAsync(3);
        await RunUntilAnchoredAsync(context, aggregateId, 3);
        await context.Events.DeleteManyAsync(EventDocumentFields<LedgerAggregate>.ForAggregate<BsonDocument>(aggregateId));
        await context.MongoHelper.Database.GetCollection<BsonDocument>(context.Options.ReadModelCollectionName)
                     .DeleteOneAsync(new BsonDocument("_id", new BsonBinaryData(aggregateId, GuidRepresentation.Standard)));
        await context.Store.AppendEventsAsync(Enumerable.Range(1, 3).Select(version => LedgerStoreContext.CreateEntry(aggregateId, version, memo: "rewritten")));
        var rewrittenHead = LedgerStoreContext.ReadIntegrity(await context.FindEventAsync(aggregateId, 3));
        await GetStore(context).Leaves.UpdateOneAsync(new BsonDocument("AggregateId", new BsonBinaryData(aggregateId, GuidRepresentation.Standard)),
                                                      Builders<BsonDocument>.Update.Set("Hash", new BsonBinaryData(rewrittenHead.Hash)));

        var result = await context.CreateVerifier().VerifyStreamAsync(aggregateId);
        var chain = await GetAnchorVerifier(context).VerifyAnchorChainAsync();

        result.Should().Be(StreamVerificationResult.Broken(3, StreamVerificationFailure.AnchoredLeafInvalid));
        chain.Failure.Should().Be(AnchorChainFailure.LeafSetMismatch);
    }

    [Test]
    public async Task VerifyStreamAsync_RetroactivelySealedSuffix_ReportsSealModeTransition()
    {
        var databaseName = $"IntegrityTestDb_{Guid.NewGuid():N}";
        var protectedContext = await CreateContextAsync(databaseName: databaseName);
        var unprotected = await LedgerStoreContext.CreateAsync(_container, false, databaseName: databaseName);
        _contexts.Add(unprotected);
        var aggregateId = await protectedContext.AppendStreamAsync(2);
        await unprotected.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(aggregateId, 3), LedgerStoreContext.CreateEntry(aggregateId, 4)]);
        await protectedContext.Sweep.RunPassAsync();

        var result = await protectedContext.CreateVerifier().VerifyStreamAsync(aggregateId);

        result.IsIntact.Should().BeTrue();
        result.SealModeTransitions.Should().Equal(3L);
        result.Events.Select(e => e.SealMode)
              .Should().Equal(IntegritySealMode.Append, IntegritySealMode.Append, IntegritySealMode.Retroactive, IntegritySealMode.Retroactive);
    }

    [Test]
    public async Task VerifyStreamAsync_RewrittenAnchoredStream_ReportsAnchoredHashMismatch()
    {
        var (context, _) = await CreateAnchoredContextAsync();
        var aggregateId = await context.AppendStreamAsync(3);
        await RunUntilAnchoredAsync(context, aggregateId, 3);
        await context.Events.DeleteManyAsync(EventDocumentFields<LedgerAggregate>.ForAggregate<BsonDocument>(aggregateId));
        await context.MongoHelper.Database.GetCollection<BsonDocument>(context.Options.ReadModelCollectionName)
                     .DeleteOneAsync(new BsonDocument("_id", new BsonBinaryData(aggregateId, GuidRepresentation.Standard)));
        await context.Store.AppendEventsAsync(Enumerable.Range(1, 3).Select(version => LedgerStoreContext.CreateEntry(aggregateId, version, memo: "rewritten")));

        var result = await context.CreateVerifier().VerifyStreamAsync(aggregateId);

        result.Should().Be(StreamVerificationResult.Broken(3, StreamVerificationFailure.AnchoredHashMismatch));
        result.Events.Should().HaveCount(3, "the rewritten chain itself is consistent");
    }

    [Test]
    public async Task VerifyStreamAsync_WithoutAnchoring_DoesNotCheckAnchorCoverage()
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(2);

        var result = await context.CreateVerifier().VerifyStreamAsync(aggregateId);

        result.IsIntact.Should().BeTrue();
        result.AnchorCoverageChecked.Should().BeFalse();
        result.Events.Should().HaveCount(2).And.OnlyContain(e => e.SealingAnchor == null);
    }

    private static async Task AnchorNewStreamAsync(LedgerStoreContext context)
    {
        var aggregateId = await context.AppendStreamAsync(1);
        await RunUntilAnchoredAsync(context, aggregateId, 1);
    }

    private static AnchorVerifier GetAnchorVerifier(LedgerStoreContext context) => context.ServiceProvider.GetRequiredService<AnchorVerifier>();

    private static AnchorStore GetStore(LedgerStoreContext context) => GetAnchorVerifier(context).Store;

    /// <summary>
    /// Runs the anchoring run until a completed anchor covers the version.
    /// </summary>
    private static async Task RunUntilAnchoredAsync(LedgerStoreContext context, Guid aggregateId, Int64 version)
    {
        var run = context.ServiceProvider.GetRequiredService<AnchoringRun>();
        var store = GetStore(context);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while ((await store.FindCompletedStreamLeavesAsync(nameof(LedgerAggregate), aggregateId, version, CancellationToken.None)).Count == 0)
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("The version was not anchored within the timeout.");
            }

            await run.RunAsync();
        }
    }

    private static PersonalDataStatus StatusOf(StreamVerificationResult result, Int64 version, String path)
        => result.Events.Single(e => e.Version == version).PersonalData.Single(p => p.Path == path).Status;

    /// <summary>
    /// Creates a protected store that anchors with a test authority, and trusts that authority for verification.
    /// </summary>
    private async Task<(LedgerStoreContext Context, TestTimestampAuthority Authority)> CreateAnchoredContextAsync(String? databaseName = null)
    {
        var testAuthority = CreateTestAuthority();
        var context = await CreateContextAsync(
            mongo => mongo.WithIntegrityAnchoring(anchoring => anchoring.WithTimestampAuthority(_ => testAuthority.CreateAuthority(name: AuthorityName)))
                          .WithIntegrityVerification(verification => verification.WithTimestampAuthorityTrust(
                                                         AuthorityName,
                                                         trust => trust.TrustedRoots.Add(testAuthority.Root))),
            databaseName);
        return (context, testAuthority);
    }

    private async Task<LedgerStoreContext> CreateContextAsync(Action<MongoBuilder>? configureMongo = null,
                                                              String? databaseName = null,
                                                              Boolean personalData = false)
    {
        var context = await LedgerStoreContext.CreateAsync(_container,
                                                           databaseName: databaseName,
                                                           configureMongo: configureMongo,
                                                           personalData: personalData);
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
