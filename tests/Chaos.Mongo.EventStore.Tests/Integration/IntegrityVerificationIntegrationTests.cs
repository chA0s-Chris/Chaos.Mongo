// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Chaos.Mongo.EventStore.Integrity;
using Chaos.Mongo.EventStore.Tests.Integrity;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using NUnit.Framework;
using System.Security.Cryptography.X509Certificates;
using System.Text;
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
        [Values("delete", "root", "previous", "unset", "format", "status", "anchorId", "sequenceNull", "timestamp", "statusNumber", "leafCountInt64")]
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

            // A null sequence number sorts before every number.
            "sequenceNull" => (1L, AnchorChainFailure.MalformedAnchor),
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
            case "sequenceNull":
                await anchors.UpdateOneAsync(bySequence(2), Builders<BsonDocument>.Update.Set(AnchorStore.SequenceField, BsonNull.Value));
                break;
            case "timestamp":
                await anchors.UpdateOneAsync(bySequence(2), Builders<BsonDocument>.Update.Set("Timestamps.0.TimestampUtc", "yesterday"));
                break;
            case "statusNumber":
                await anchors.UpdateOneAsync(bySequence(2), Builders<BsonDocument>.Update.Set(AnchorStore.StatusField, "2"));
                break;
            case "leafCountInt64":
                await anchors.UpdateOneAsync(bySequence(2), Builders<BsonDocument>.Update.Set("LeafCount", (Int64)1));
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
        [Values("hash", "delete", "add", "version", "anchorId", "sequence", "index", "indexString")]
        String change)
    {
        var (context, _) = await CreateAnchoredContextAsync();
        await AnchorNewStreamAsync(context);
        var aggregateId = await context.AppendStreamAsync(1);
        await context.RunUntilAnchoredAsync(aggregateId, 1);
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
            case "index":
                await leaves.UpdateOneAsync(leafFilter, Builders<BsonDocument>.Update.Set("Index", BsonNull.Value));
                break;
            case "indexString":
                await leaves.UpdateOneAsync(leafFilter, Builders<BsonDocument>.Update.Set("Index", "0"));
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
    [TestCase("anchor")]
    [TestCase("leaf")]
    [TestCase("evidence")]
    public async Task VerifyAnchorChainAsync_DocumentRepeatingAnElementName_ReportsItAsMalformed(String document)
    {
        var (context, _) = await CreateAnchoredContextAsync();
        var aggregateId = await context.AppendStreamAsync(3);
        await context.RunUntilAnchoredAsync(aggregateId, 3);
        var store = GetStore(context);
        var anchor = await store.Anchors.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        var evidence = new AnchorEvidenceStore(context.MongoHelper, context.ServiceProvider.GetRequiredService<IntegrityAnchoringOptions>()).Evidence;
        await (document switch
        {
            "anchor" => RepeatElementAsync(store.Anchors, new BsonDocument("_id", anchor["_id"]), AnchorStore.StatusField),
            "leaf" => RepeatElementAsync(store.Leaves, new BsonDocument(AnchorStore.AnchorIdField, anchor["_id"]), "Hash"),
            _ => RepeatElementAsync(evidence, new BsonDocument("_id", anchor["Timestamps"][0]["TokenHash"]), "Length")
        });

        var chain = await GetAnchorVerifier(context).VerifyAnchorChainAsync();
        var stream = await context.CreateVerifier().VerifyStreamAsync(aggregateId);
        var eventAnchor = await context.CreateVerifier().VerifyEventAnchorAsync(aggregateId, 1);

        switch (document)
        {
            case "anchor":
                chain.Should().Be(new AnchorChainVerificationResult(1, AnchorChainFailure.MalformedAnchor, 0));
                stream.Should().Be(StreamVerificationResult.Broken(3, StreamVerificationFailure.AnchoredLeafInvalid));
                break;
            case "leaf":
                chain.Should().Be(new AnchorChainVerificationResult(1, AnchorChainFailure.LeafSetMismatch, 0));
                stream.Should().Be(StreamVerificationResult.Broken(3, StreamVerificationFailure.AnchoredLeafInvalid));
                break;
            default:
                chain.IsIntact.Should().BeTrue();
                eventAnchor.Timestamps.Should().ContainSingle().Which.Failure.Should().Contain("malformed");
                break;
        }
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
    public async Task VerifyAnchorChainAsync_NewestAnchorAwaitingTimestampsWithTrustedHead_ReportsMalformedAnchor()
    {
        var (context, _) = await CreateAnchoredContextAsync();
        await AnchorNewStreamAsync(context);
        await AnchorNewStreamAsync(context);
        var store = GetStore(context);
        var head = AnchorStore.ReadAnchor(await store.Anchors.Find(new BsonDocument(AnchorStore.SequenceField, 2L)).SingleAsync());
        await store.Anchors.UpdateOneAsync(new BsonDocument(AnchorStore.SequenceField, 2L),
                                           Builders<BsonDocument>.Update.Set(AnchorStore.StatusField, nameof(AnchorStatus.AwaitingTimestamp)));
        var verifier = GetAnchorVerifier(context);

        var withoutHead = await verifier.VerifyAnchorChainAsync();
        var withHead = await verifier.VerifyAnchorChainAsync(new TrustedAnchorHead(2, head.AnchorDigest));

        withoutHead.IsIntact.Should().BeTrue("the newest anchor may await its timestamps");
        withHead.Should().Be(new AnchorChainVerificationResult(2, AnchorChainFailure.MalformedAnchor, 1));
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
        await context.RunUntilAnchoredAsync(aggregateId, 3);

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
        [Values("event", "leafHash", "proof", "digest", "evidence", "tokenHash", "length", "lengthAsString")]
        String change)
    {
        var (context, _) = await CreateAnchoredContextAsync();
        var aggregateId = await context.AppendStreamAsync(3);
        await context.RunUntilAnchoredAsync(aggregateId, 3);
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
            case "length" or "lengthAsString":
                var evidence = context.MongoHelper.Database.GetCollection<BsonDocument>(context.ServiceProvider.GetRequiredService<IntegrityAnchoringOptions>()
                                                                                               .AnchorEvidenceCollectionName);
                foreach (var item in await evidence.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync())
                {
                    // The driver's lenient conversion would read the length as a string, so only the exact types count.
                    BsonValue length = change == "length" ? BsonNull.Value : item["Length"].ToString();
                    await evidence.UpdateOneAsync(new BsonDocument("_id", item["_id"]), Builders<BsonDocument>.Update.Set("Length", length));
                }

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
        if (change is "evidence" or "tokenHash" or "length" or "lengthAsString")
        {
            result.Timestamps.Should().ContainSingle().Which.Failure.Should().Contain(change switch
            {
                "evidence" => "missing",
                "tokenHash" => "malformed evidence",
                _ => "malformed"
            });
        }
    }

    [Test]
    [TestCase("firstTokenHash", 2, 3)]
    [TestCase("everyTokenHash", 1, 2)]
    [TestCase("firstDigest", 1, 2)]
    public async Task VerifyEventAnchorAsync_EventCoveredByTwoAnchors_FallsBackOnlyWhenNoTokenIsValid(String change, Int64 sequence, Int64 leafVersion)
    {
        var (status, failure) = change switch
        {
            "firstTokenHash" => (EventAnchorStatus.Anchored, (EventAnchorFailure?)null),
            "everyTokenHash" => (EventAnchorStatus.Failed, EventAnchorFailure.NoValidTimestamp),
            _ => (EventAnchorStatus.Failed, EventAnchorFailure.AnchorDigestMismatch)
        };
        var (context, _) = await CreateAnchoredContextAsync();
        var aggregateId = await context.AppendStreamAsync(2);
        await context.RunUntilAnchoredAsync(aggregateId, 2);
        await context.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(aggregateId, 3)]);
        await context.RunUntilAnchoredAsync(aggregateId, 3);
        var firstAnchor = new BsonDocument(AnchorStore.SequenceField, 1L);
        await (change switch
        {
            "firstTokenHash" => GetStore(context).Anchors.UpdateOneAsync(firstAnchor,
                                                                         Builders<BsonDocument>.Update.Set("Timestamps.0.TokenHash", new BsonBinaryData(new Byte[32]))),
            "everyTokenHash" => GetStore(context).Anchors.UpdateManyAsync(FilterDefinition<BsonDocument>.Empty,
                                                                          Builders<BsonDocument>.Update.Set("Timestamps.0.TokenHash", new BsonBinaryData(new Byte[32]))),
            _ => GetStore(context).Anchors.UpdateOneAsync(firstAnchor,
                                                          Builders<BsonDocument>.Update.Set(AnchorStore.AnchorDigestField, new BsonBinaryData(new Byte[32])))
        });

        var result = await context.CreateVerifier().VerifyEventAnchorAsync(aggregateId, 1);

        (result.Status, result.Failure, result.Anchor?.Sequence, result.LeafVersion).Should().Be((status, failure, sequence, leafVersion));
    }

    [Test]
    public async Task VerifyEventAnchorAsync_ForgedEventsWithRebuiltFirstAnchor_DoNotVerifyThroughTheNextAnchor()
    {
        var (context, _) = await CreateAnchoredContextAsync();
        var aggregateId = await context.AppendStreamAsync(2);
        await context.RunUntilAnchoredAsync(aggregateId, 2);
        await context.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(aggregateId, 3)]);
        await context.RunUntilAnchoredAsync(aggregateId, 3);
        // Versions 1 and 2 are forged and chained again, and the first anchor is rebuilt over them; its tokens then no
        // longer cover its digest, so verification falls back to the second anchor, whose chain must start from the
        // forged hash and therefore breaks at version 3.
        Byte[]? previousHash = null;
        for (var version = 1L; version <= 2; version++)
        {
            var document = await context.FindEventAsync(aggregateId, version);
            var integrity = LedgerStoreContext.ReadIntegrity(document);
            if (version == 1)
            {
                document[MemoPath] = "forged";
            }

            previousHash ??= integrity.PreviousHash;
            var hash = EventIntegrityChain.ComputeHash(RawBsonElements.RemoveTopLevelElement(document.ToBson(), EventIntegrityChain.ElementName),
                                                       previousHash,
                                                       integrity.SealMode);
            document[EventIntegrityChain.ElementName]["PreviousHash"] = new BsonBinaryData(previousHash);
            document[EventIntegrityChain.ElementName]["Hash"] = new BsonBinaryData(hash);
            await context.Events.ReplaceOneAsync(EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, version), document);
            previousHash = hash;
        }

        var forgedHead = previousHash ?? throw new AssertionException("No event was forged.");
        var store = GetStore(context);
        var firstAnchor = new BsonDocument(AnchorStore.SequenceField, 1L);
        var anchorId = (await store.Anchors.Find(firstAnchor).SingleAsync())["_id"].AsBsonBinaryData.ToGuid(GuidRepresentation.Standard);
        var root = AnchorVerifier.HashLeaf(new AnchorLeaf(anchorId, 1, 0, nameof(LedgerAggregate), aggregateId, 2, forgedHead, []));
        await store.Leaves.UpdateOneAsync(new BsonDocument(AnchorStore.AnchorSequenceField, 1L),
                                          Builders<BsonDocument>.Update.Set("Hash", new BsonBinaryData(forgedHead)));
        await store.Anchors.UpdateOneAsync(firstAnchor,
                                           Builders<BsonDocument>.Update.Set("MerkleRoot", new BsonBinaryData(root))
                                                                 .Set(AnchorStore.AnchorDigestField,
                                                                      new BsonBinaryData(AnchorEncoding.ComputeAnchorDigest(1, 1, new Byte[32], root))));

        var result = await context.CreateVerifier().VerifyEventAnchorAsync(aggregateId, 1);

        (result.Status, result.Failure, result.Anchor?.Sequence).Should().Be((EventAnchorStatus.Failed, EventAnchorFailure.ChainBroken, 2));
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
        await context.RunUntilAnchoredAsync(aggregateId, 1);

        var result = await context.CreateVerifier().VerifyEventAnchorAsync(aggregateId, 1);

        result.Status.Should().Be(EventAnchorStatus.Anchored);
    }

    [Test]
    public async Task VerifyEventAnchorAsync_VerificationOnlyContainer_VerifiesAnchorsOfAnotherContainer()
    {
        var databaseName = $"IntegrityTestDb_{Guid.NewGuid():N}";
        var (anchoring, testAuthority) = await CreateAnchoredContextAsync(databaseName);
        var aggregateId = await anchoring.AppendStreamAsync(2);
        await anchoring.RunUntilAnchoredAsync(aggregateId, 2);
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
        await context.RunUntilAnchoredAsync(aggregateId, 1);

        var result = await context.CreateVerifier().VerifyEventAnchorAsync(aggregateId, 1);

        result.Status.Should().Be(EventAnchorStatus.Failed);
        result.Failure.Should().Be(EventAnchorFailure.NoValidTimestamp);
        result.Timestamps.Should().ContainSingle().Which.Failure.Should().Contain("no trust settings");
    }

    [Test]
    [TestCase("leaf", "undecodableName")]
    [TestCase("anchor", "undecodableName")]
    [TestCase("leaf", "dbPointer")]
    [TestCase("anchor", "dbPointer")]
    public async Task VerifyStreamAsync_AnchorDocumentThatCannotBeRead_ReportsAnchoredLeafInvalid(String document, String change)
    {
        var (context, _) = await CreateAnchoredContextAsync();
        var aggregateId = await context.AppendStreamAsync(3);
        await context.RunUntilAnchoredAsync(aggregateId, 3);
        var store = GetStore(context);
        var collection = document == "leaf" ? store.Leaves : store.Anchors;
        var raw = await collection.Database.GetCollection<RawBsonDocument>(collection.CollectionNamespace.CollectionName).Find(FilterDefinition<RawBsonDocument>.Empty)
                                  .SingleAsync();
        var bytes = raw.ToBson();
        // The second element's name comes before Sequence and Version; the identifier is an ObjectId or a UUID.
        var identifierEnd = 4 + 1 + 4 + (bytes[4] == 0x07 ? 12 : 5 + BitConverter.ToInt32(bytes, 4 + 1 + 4));
        if (change == "dbPointer")
        {
            bytes = WithDbPointer(bytes, identifierEnd);
        }
        else
        {
            bytes[identifierEnd + 1] = 0xFF;
        }

        await collection.DeleteOneAsync(new BsonDocument("_id", raw["_id"]));
        await collection.Database.GetCollection<RawBsonDocument>(collection.CollectionNamespace.CollectionName).InsertOneAsync(new RawBsonDocument(bytes));

        var stream = await context.CreateVerifier().VerifyStreamAsync(aggregateId);
        var eventAnchor = await context.CreateVerifier().VerifyEventAnchorAsync(aggregateId, 1);
        var chain = await GetAnchorVerifier(context).VerifyAnchorChainAsync();

        stream.Should().Be(StreamVerificationResult.Broken(3, StreamVerificationFailure.AnchoredLeafInvalid));
        (eventAnchor.Status, eventAnchor.Failure).Should().Be((EventAnchorStatus.Failed, EventAnchorFailure.AnchorDataUnreadable));
        chain.IsIntact.Should().BeFalse();
    }

    [Test]
    public async Task VerifyStreamAsync_AnchoredStream_ReturnsPerVersionIntegrityData()
    {
        var (context, _) = await CreateAnchoredContextAsync();
        var verifier = context.CreateVerifier();
        var aggregateId = await context.AppendStreamAsync(3);
        await context.RunUntilAnchoredAsync(aggregateId, 3);
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
        await context.RunUntilAnchoredAsync(aggregateId, 3);
        await context.Events.DeleteManyAsync(Builders<BsonDocument>.Filter.And(
                                                 EventDocumentFields<LedgerAggregate>.ForAggregate<BsonDocument>(aggregateId),
                                                 Builders<BsonDocument>.Filter.Gt("Version", deleteWholeStream ? 0 : 2)));

        var result = await context.CreateVerifier().VerifyStreamAsync(aggregateId);

        result.Should().Be(StreamVerificationResult.Broken(brokenVersion, StreamVerificationFailure.AnchoredVersionMissing));
    }

    [Test]
    public async Task VerifyStreamAsync_ElementNameNotValidUtf8_ReportsMalformedIntegrity()
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(3);
        var bytes = await context.ReadRawEventAsync(aggregateId, 2);
        Byte[] name = [0x03, .. Encoding.UTF8.GetBytes("Counterparty"), 0x00];
        var nameStart = bytes.AsSpan().IndexOf(name) + 1;
        bytes[nameStart] = 0xFF;
        await context.Events.DeleteOneAsync(EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, 2));
        await context.MongoHelper.Database.GetCollection<RawBsonDocument>(context.Options.EventsCollectionName).InsertOneAsync(new RawBsonDocument(bytes));

        var result = await context.CreateVerifier().VerifyStreamAsync(aggregateId);

        result.Should().Be(StreamVerificationResult.Broken(2, StreamVerificationFailure.MalformedIntegrity));
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
    public async Task VerifyStreamAsync_ErasureEntryTheReaderRejects_ReportsMalformedIntegrity()
    {
        var context = await CreateContextAsync(personalData: true);
        var aggregateId = await context.AppendStreamAsync(3);
        await context.Eraser.ErasePersonalDataAsync(aggregateId, new ErasureRequest("TICKET-1", [1], ["Memo"]));
        var document = await context.FindEventAsync(aggregateId, 4);
        document[PersonalDataErased<LedgerAggregate>.EntriesElement].AsBsonArray.Add(new BsonBinaryData(new Byte[15]));
        var bytes = document.ToBson();
        // The driver writes no UUID of another length, so the item's subtype is changed to UUID in place.
        Byte[] item = [0x0F, 0x00, 0x00, 0x00, 0x00, .. new Byte[15]];
        bytes[bytes.AsSpan().IndexOf(item) + 4] = (Byte)BsonBinarySubType.UuidStandard;
        var integrity = LedgerStoreContext.ReadIntegrity(document);
        EventIntegrityChain.ComputeHash(RawBsonElements.RemoveTopLevelElement(bytes, EventIntegrityChain.ElementName), integrity.PreviousHash, integrity.SealMode)
                           .CopyTo(bytes, bytes.AsSpan().IndexOf(integrity.Hash));
        await ReplaceRawEventAsync(context, aggregateId, 4, bytes);

        var result = await context.CreateVerifier().VerifyStreamAsync(aggregateId);

        result.Should().Be(StreamVerificationResult.Broken(4, StreamVerificationFailure.MalformedIntegrity));
    }

    [Test]
    public async Task VerifyStreamAsync_IntegrityHashStoredAsOldBinary_ReportsMalformedIntegrity()
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(3);
        var document = await context.FindEventAsync(aggregateId, 2);
        document[EventIntegrityChain.ElementName]["Hash"] = new BsonBinaryData(document[EventIntegrityChain.ElementName]["Hash"].AsByteArray, BsonBinarySubType.OldBinary);
        using var stream = new MemoryStream();
        using (var writer = new BsonBinaryWriter(stream, new BsonBinaryWriterSettings
               {
                   FixOldBinarySubTypeOnOutput = false
               }))
        {
            BsonDocumentSerializer.Instance.Serialize(BsonSerializationContext.CreateRoot(writer), document);
        }

        await ReplaceRawEventAsync(context, aggregateId, 2, stream.ToArray());

        var result = await context.CreateVerifier().VerifyStreamAsync(aggregateId);

        result.Should().Be(StreamVerificationResult.Broken(2, StreamVerificationFailure.MalformedIntegrity));
    }

    [Test]
    public async Task VerifyStreamAsync_LeafRewrittenWithStream_ReportsAnchoredLeafInvalid()
    {
        var (context, _) = await CreateAnchoredContextAsync();
        var aggregateId = await context.AppendStreamAsync(3);
        await context.RunUntilAnchoredAsync(aggregateId, 3);
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
    public async Task VerifyStreamAsync_ResealedPrefixWithLaterBreak_ReportsAnchoredMismatchBelowTheBreak()
    {
        var (context, _) = await CreateAnchoredContextAsync();
        var aggregateId = await context.AppendStreamAsync(3);
        await context.RunUntilAnchoredAsync(aggregateId, 3);
        await context.Events.DeleteManyAsync(EventDocumentFields<LedgerAggregate>.ForAggregate<BsonDocument>(aggregateId));
        await context.MongoHelper.Database.GetCollection<BsonDocument>(context.Options.ReadModelCollectionName)
                     .DeleteOneAsync(new BsonDocument("_id", new BsonBinaryData(aggregateId, GuidRepresentation.Standard)));
        await context.Store.AppendEventsAsync(Enumerable.Range(1, 4).Select(version => LedgerStoreContext.CreateEntry(aggregateId, version, memo: "rewritten")));
        await context.Events.UpdateOneAsync(EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, 4),
                                            Builders<BsonDocument>.Update.Set(MemoPath, "forged"));

        var result = await context.CreateVerifier().VerifyStreamAsync(aggregateId);

        result.Should().Be(StreamVerificationResult.Broken(3, StreamVerificationFailure.AnchoredHashMismatch));
        result.Events.Should().HaveCount(3, "the chain itself breaks at version 4");
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
        await context.RunUntilAnchoredAsync(aggregateId, 3);
        await context.Events.DeleteManyAsync(EventDocumentFields<LedgerAggregate>.ForAggregate<BsonDocument>(aggregateId));
        await context.MongoHelper.Database.GetCollection<BsonDocument>(context.Options.ReadModelCollectionName)
                     .DeleteOneAsync(new BsonDocument("_id", new BsonBinaryData(aggregateId, GuidRepresentation.Standard)));
        await context.Store.AppendEventsAsync(Enumerable.Range(1, 3).Select(version => LedgerStoreContext.CreateEntry(aggregateId, version, memo: "rewritten")));

        var result = await context.CreateVerifier().VerifyStreamAsync(aggregateId);

        result.Should().Be(StreamVerificationResult.Broken(3, StreamVerificationFailure.AnchoredHashMismatch));
        result.Events.Should().HaveCount(3, "the rewritten chain itself is consistent");
    }

    [Test]
    public async Task VerifyStreamAsync_StreamCoveredByTwoAnchors_AssignsTheFirstCoveringAnchor()
    {
        var (context, _) = await CreateAnchoredContextAsync();
        var aggregateId = await context.AppendStreamAsync(2);
        await context.RunUntilAnchoredAsync(aggregateId, 2);
        await context.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(aggregateId, 3)]);
        await context.RunUntilAnchoredAsync(aggregateId, 3);
        var verifier = context.CreateVerifier();
        var first = (await verifier.VerifyEventAnchorAsync(aggregateId, 1)).Anchor;
        var second = (await verifier.VerifyEventAnchorAsync(aggregateId, 3)).Anchor;

        var result = await verifier.VerifyStreamAsync(aggregateId);

        result.IsIntact.Should().BeTrue();
        first.Should().NotBe(second);
        result.Events.Select(e => e.SealingAnchor).Should().Equal(first, first, second);
    }

    [Test]
    [TestCase("nestedPersonalDataName", 2)]
    [TestCase("discriminator", 3)]
    [TestCase("erasurePath", 4)]
    public async Task VerifyStreamAsync_StringOrNestedNameNotValidUtf8_ReportsMalformedIntegrity(String place, Int64 version)
    {
        // Events with personal data are sealed with a binding, so the discriminator case uses a stream without it.
        var context = await CreateContextAsync(personalData: place != "discriminator");
        var aggregateId = await context.AppendStreamAsync(3);
        if (place == "erasurePath")
        {
            await context.Eraser.ErasePersonalDataAsync(aggregateId, new ErasureRequest("TICKET-1", [1], ["Memo"]));
        }

        var bytes = await context.ReadRawEventAsync(aggregateId, version);
        Byte[] pattern = place switch
        {
            "nestedPersonalDataName" => [0x02, .. "Name"u8, 0x00],
            "discriminator" => [.. "LedgerEntryRecorded"u8],
            _ => [0x05, 0x00, 0x00, 0x00, .. "Memo"u8, 0x00]
        };
        var offset = place == "discriminator" ? 0 : place == "erasurePath" ? 4 : 1;
        bytes[bytes.AsSpan().IndexOf(pattern) + offset] = 0xFF;
        if (place != "nestedPersonalDataName")
        {
            // The changed string is covered by the hash, so the event is sealed again to reach the string's decoding.
            var integrity = LedgerStoreContext.ReadIntegrity(await context.FindEventAsync(aggregateId, version));
            var resealed = EventIntegrityChain.ComputeHash(RawBsonElements.RemoveTopLevelElement(bytes, EventIntegrityChain.ElementName),
                                                           integrity.PreviousHash,
                                                           integrity.SealMode);
            resealed.CopyTo(bytes, bytes.AsSpan().IndexOf(integrity.Hash));
        }

        await ReplaceRawEventAsync(context, aggregateId, version, bytes);

        var result = await context.CreateVerifier().VerifyStreamAsync(aggregateId);

        result.Should().Be(StreamVerificationResult.Broken(version, StreamVerificationFailure.MalformedIntegrity));
    }

    [Test]
    [TestCase("leafHash")]
    [TestCase("anchorCreatedUtc")]
    public async Task VerifyStreamAsync_UnreadableLeafOrAnchor_ReportsAnchoredLeafInvalid(String change)
    {
        var (context, _) = await CreateAnchoredContextAsync();
        var aggregateId = await context.AppendStreamAsync(3);
        await context.RunUntilAnchoredAsync(aggregateId, 3);
        var store = GetStore(context);
        await (change == "leafHash"
            ? (Task)store.Leaves.UpdateOneAsync(FilterDefinition<BsonDocument>.Empty, Builders<BsonDocument>.Update.Set("Hash", "unreadable"))
            : store.Anchors.UpdateOneAsync(FilterDefinition<BsonDocument>.Empty, Builders<BsonDocument>.Update.Set("CreatedUtc", "unreadable")));

        var stream = await context.CreateVerifier().VerifyStreamAsync(aggregateId);
        var anchor = await context.CreateVerifier().VerifyEventAnchorAsync(aggregateId, 1);

        stream.Should().Be(StreamVerificationResult.Broken(3, StreamVerificationFailure.AnchoredLeafInvalid));
        anchor.Status.Should().Be(EventAnchorStatus.Failed);
        anchor.Failure.Should().Be(EventAnchorFailure.AnchorDataUnreadable);
    }

    [Test]
    [TestCase("versionNotValidUtf8", 4, nameof(StreamVerificationFailure.VersionGap))]
    [TestCase("dbPointer", 2, nameof(StreamVerificationFailure.MalformedIntegrity))]
    [TestCase("integrityNotValidUtf8", 2, nameof(StreamVerificationFailure.MalformedIntegrity))]
    [TestCase("discriminatorScopeRepeatingName", 3, nameof(StreamVerificationFailure.MalformedIntegrity))]
    public async Task VerifyStreamAsync_ValueTheReaderRejects_ReportsTheSpecifiedFailure(String place, Int64 version, String failure)
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(3);
        var events = context.MongoHelper.Database.GetCollection<RawBsonDocument>(context.Options.EventsCollectionName);
        switch (place)
        {
            case "versionNotValidUtf8":
                // A document of the stream whose version is a string the reader rejects sorts after the numbers.
                var junk = new BsonDocument
                {
                    { EventDocumentFields<LedgerAggregate>.AggregateId, new BsonBinaryData(aggregateId, GuidRepresentation.Standard) },
                    { EventDocumentFields<LedgerAggregate>.Version, "x" }
                }.ToBson();
                junk[junk.AsSpan().IndexOf([(Byte)0x02, (Byte)0x00, (Byte)0x00, (Byte)0x00, (Byte)'x', (Byte)0x00]) + 4] = 0xFF;
                await events.InsertOneAsync(new RawBsonDocument(junk));
                break;
            case "dbPointer":
                var stored = await context.ReadRawEventAsync(aggregateId, 2);
                await ReplaceRawEventAsync(context, aggregateId, 2, WithDbPointer(stored, stored.Length - 1));
                break;
            case "integrityNotValidUtf8":
                var document = await context.FindEventAsync(aggregateId, 2);
                document[EventIntegrityChain.ElementName] = "x";
                var bytes = document.ToBson();
                bytes[bytes.AsSpan().IndexOf([(Byte)0x02, (Byte)0x00, (Byte)0x00, (Byte)0x00, (Byte)'x', (Byte)0x00]) + 4] = 0xFF;
                await ReplaceRawEventAsync(context, aggregateId, 2, bytes);
                break;
            default:
                // The discriminator is covered by the hash, so the event is sealed again to reach its decoding.
                var last = await context.FindEventAsync(aggregateId, 3);
                var integrity = LedgerStoreContext.ReadIntegrity(last);
                last["_t"] = new BsonJavaScriptWithScope("c", new BsonDocument
                {
                    AllowDuplicateNames = true
                }.Add("s", 1).Add("s", 2));
                var resealed = last.ToBson();
                EventIntegrityChain.ComputeHash(RawBsonElements.RemoveTopLevelElement(resealed, EventIntegrityChain.ElementName), integrity.PreviousHash, integrity.SealMode)
                                   .CopyTo(resealed, resealed.AsSpan().IndexOf(integrity.Hash));
                await ReplaceRawEventAsync(context, aggregateId, 3, resealed);
                break;
        }

        var result = await context.CreateVerifier().VerifyStreamAsync(aggregateId);

        result.Should().Be(StreamVerificationResult.Broken(version, Enum.Parse<StreamVerificationFailure>(failure)));
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
        await context.RunUntilAnchoredAsync(aggregateId, 1);
    }

    private static AnchorVerifier GetAnchorVerifier(LedgerStoreContext context) => context.ServiceProvider.GetRequiredService<AnchorVerifier>();

    private static AnchorStore GetStore(LedgerStoreContext context) => GetAnchorVerifier(context).Store;

    /// <summary>
    /// Replaces a stored document by one that repeats an element, which the driver cannot read into a document.
    /// </summary>
    private static async Task RepeatElementAsync(IMongoCollection<BsonDocument> collection, BsonDocument filter, String name)
    {
        var document = await collection.Find(filter).FirstAsync();
        var repeated = new BsonDocument
        {
            AllowDuplicateNames = true
        };
        repeated.AddRange(document);
        repeated.Add(name, document[name]);
        await collection.DeleteOneAsync(new BsonDocument("_id", document["_id"]));
        await collection.Database.GetCollection<RawBsonDocument>(collection.CollectionNamespace.CollectionName).InsertOneAsync(new RawBsonDocument(repeated.ToBson()));
    }

    private static async Task ReplaceRawEventAsync(LedgerStoreContext context, Guid aggregateId, Int64 version, Byte[] bytes)
    {
        await context.Events.DeleteOneAsync(EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, version));
        await context.MongoHelper.Database.GetCollection<RawBsonDocument>(context.Options.EventsCollectionName).InsertOneAsync(new RawBsonDocument(bytes));
    }

    private static PersonalDataStatus StatusOf(StreamVerificationResult result, Int64 version, String path)
        => result.Events.Single(e => e.Version == version).PersonalData.Single(p => p.Path == path).Status;

    /// <summary>
    /// Inserts a DBPointer element, a type MongoDB stores but the driver cannot read, at a position of a document.
    /// </summary>
    private static Byte[] WithDbPointer(Byte[] document, Int32 position)
    {
        Byte[] element = [0x0C, (Byte)'p', 0x00, 0x02, 0x00, 0x00, 0x00, (Byte)'x', 0x00, .. new Byte[12]];
        Byte[] result = [.. document.AsSpan(0, position), .. element, .. document.AsSpan(position)];
        BitConverter.TryWriteBytes(result.AsSpan(0, 4), result.Length);
        return result;
    }

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
