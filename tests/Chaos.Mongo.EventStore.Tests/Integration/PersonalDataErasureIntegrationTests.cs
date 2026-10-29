// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Chaos.Mongo.EventStore.Errors;
using Chaos.Mongo.EventStore.Integrity;
using Chaos.Mongo.EventStore.Tests.Integrity;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using NUnit.Framework;
using Testcontainers.MongoDb;

/// <summary>
/// Transactional erasure of personal data values, the rebuild it performs and how verification reports erasures.
/// </summary>
public class PersonalDataErasureIntegrationTests
{
    private const String AccountPath = "Counterparty.Account";
    private const String DefaultMemo = "monthly fee";
    private const String MemoPath = "Memo";
    private const String NamePath = "Counterparty.Name";
    private readonly List<LedgerStoreContext> _contexts = [];
    private readonly List<TestTimestampAuthority> _testAuthorities = [];
    private MongoDbContainer _container;

    [Test]
    public async Task AppendEventsAsync_ErasureEventNotCreatedByTheEraser_ThrowsArgumentExceptionAndPersistsNothing()
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(1);
        var forged = new PersonalDataErased<LedgerAggregate>
        {
            AggregateId = aggregateId,
            Reference = "forged",
            Entries =
            [
                new PersonalDataErasureEntry
                {
                    Version = 1,
                    Paths = [MemoPath]
                }
            ]
        };

        var act = () => context.Store.AppendEventsAsync([forged]);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*appended only by*");
        (await context.CountEventsAsync(aggregateId)).Should().Be(1);
        (await context.Eraser.ErasePersonalDataAsync(aggregateId, new ErasureRequest("ticket", [1], [MemoPath]))).Should().ContainSingle();
        (await context.CountEventsAsync(aggregateId)).Should().Be(2);
    }

    [Test]
    public async Task ErasePersonalDataAsync_AlreadyErasedValues_SkipsThemAndAppendsNothingWhenNoneLeft()
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(2);

        var first = await context.Eraser.ErasePersonalDataAsync(aggregateId, new ErasureRequest("first", [1], [MemoPath]));
        var second = await context.Eraser.ErasePersonalDataAsync(aggregateId, new ErasureRequest("second"));
        var third = await context.Eraser.ErasePersonalDataAsync(aggregateId, new ErasureRequest("third"));

        Describe(first).Should().Equal((1, MemoPath));
        Describe(second).Should().Equal((1, NamePath), (2, $"{MemoPath},{NamePath}"));
        third.Should().BeEmpty();
        (await context.CountEventsAsync(aggregateId)).Should().Be(4, "the third erasure appends no event");
        (await context.CreateVerifier().VerifyStreamAsync(aggregateId)).Should().Be(StreamVerificationResult.Intact);
    }

    [Test]
    public async Task ErasePersonalDataAsync_AnchoredHead_KeepsStoredHashAndInclusionProof()
    {
        var testAuthority = CreateTestAuthority();
        var context = await CreateContextAsync(configureMongo: mongo =>
                                                   mongo.WithIntegrityAnchoring(anchoring => anchoring.WithTimestampAuthority(_ => testAuthority.CreateAuthority())));
        var store = new AnchorStore(context.MongoHelper, context.ServiceProvider.GetRequiredService<IntegrityAnchoringOptions>());
        var run = context.ServiceProvider.GetRequiredService<AnchoringRun>();
        var lookup = context.ServiceProvider.GetRequiredService<SealingAnchorLookup>();
        var aggregateId = await context.AppendStreamAsync(2);
        await RunUntilAsync(run, _ => IsAnchoredAsync(store, aggregateId, 2));
        var anchored = await lookup.FindAsync(nameof(LedgerAggregate), aggregateId, 2);

        await context.Eraser.ErasePersonalDataAsync(aggregateId, new ErasureRequest("ticket"));

        var integrity = LedgerStoreContext.ReadIntegrity(await context.FindEventAsync(aggregateId, 2));
        anchored.Should().NotBeNull();
        anchored.Leaf.Version.Should().Be(2);
        integrity.Hash.Should().Equal(anchored.Leaf.Hash);
        var leafHash = MerkleTree.HashLeaf(AnchorEncoding.EncodeLeaf(new AnchorCandidate(anchored.Leaf.AggregateType,
                                                                                         anchored.Leaf.AggregateId,
                                                                                         anchored.Leaf.Version,
                                                                                         integrity.Hash,
                                                                                         default)));
        MerkleTree.VerifyInclusion(leafHash, anchored.Leaf.Index, anchored.Anchor.LeafCount, anchored.Leaf.Proof, anchored.Anchor.MerkleRoot)
                  .Should().BeTrue();
        (await context.CreateVerifier().VerifyStreamAsync(aggregateId)).Should().Be(StreamVerificationResult.Intact);
    }

    [Test]
    public async Task ErasePersonalDataAsync_CheckpointAtObservationalVersion_ThrowsMongoEventStoreExceptionAndPersistsNothing()
    {
        var context = await CreateContextAsync(configureOptions: options => options.CheckpointInterval = 1);
        var aggregateId = Guid.NewGuid();
        await context.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(aggregateId, 1)]);
        await context.Store.AppendEventsAsync([LedgerStoreContext.CreateAudit(aggregateId, 2)]);
        await context.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(aggregateId, 3)]);
        var bogus = (await context.ReadRawCheckpointsAsync(aggregateId))[0].DeepClone().AsBsonDocument;
        bogus["_id"]["Version"] = new BsonInt64(2);
        await context.MongoHelper.Database.GetCollection<BsonDocument>(context.Options.CheckpointCollectionName).InsertOneAsync(bogus);

        var act = () => context.Eraser.ErasePersonalDataAsync(aggregateId, new ErasureRequest("ticket", [1]));

        await act.Should().ThrowAsync<MongoEventStoreException>().WithMessage("*checkpoint at version 2*no state-changing event*");
        await AssertUntouchedAsync(context, aggregateId, 3);
    }

    [Test]
    public async Task ErasePersonalDataAsync_CheckpointsDisabledWithStoredCheckpoints_RewritesAffectedCheckpoints()
    {
        var databaseName = $"IntegrityTestDb_{Guid.NewGuid():N}";
        var writer = await CreateContextAsync(databaseName: databaseName, configureOptions: options => options.CheckpointInterval = 2);
        var aggregateId = Guid.NewGuid();
        for (var version = 1; version <= 4; version++)
        {
            await writer.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(aggregateId, version)]);
        }

        var checkpointsBefore = await writer.ReadRawCheckpointsAsync(aggregateId);
        var context = await CreateContextAsync(databaseName: databaseName);

        await context.Eraser.ErasePersonalDataAsync(aggregateId, new ErasureRequest("ticket", [3], [MemoPath]));

        var checkpoints = await context.ReadRawCheckpointsAsync(aggregateId);
        checkpoints.Select(checkpoint => checkpoint["_id"]["Version"].ToInt64()).Should().Equal(2, 4);
        checkpoints[0].Equals(checkpointsBefore[0]).Should().BeTrue("the checkpoint below the erased version is untouched");
        Memos(checkpoints[1]).Should().Equal(DefaultMemo, DefaultMemo, null, DefaultMemo);
        Proven(await context.Repository.GetAsync(aggregateId)).Memos.Should().Equal(DefaultMemo, DefaultMemo, null, DefaultMemo);
        (await context.CreateVerifier().VerifyStreamAsync(aggregateId)).Should().Be(StreamVerificationResult.Intact);
    }

    [Test]
    public async Task ErasePersonalDataAsync_CheckpointsWithDivergingRevisions_RebuildsFromCheckpointBelowFirstErasedVersion()
    {
        var context = await CreateContextAsync(configureOptions: options => options.CheckpointInterval = 2);
        var aggregateId = Guid.NewGuid();
        Event<LedgerAggregate>[] stream =
        [
            LedgerStoreContext.CreateEntry(aggregateId, 1),
            LedgerStoreContext.CreateEntry(aggregateId, 2),
            LedgerStoreContext.CreateAudit(aggregateId, 3),
            LedgerStoreContext.CreateEntry(aggregateId, 4),
            LedgerStoreContext.CreateAudit(aggregateId, 5),
            LedgerStoreContext.CreateEntry(aggregateId, 6),
            LedgerStoreContext.CreateEntry(aggregateId, 7),
            LedgerStoreContext.CreateEntry(aggregateId, 8)
        ];
        foreach (var @event in stream)
        {
            await context.Store.AppendEventsAsync([@event]);
        }

        var checkpointsBefore = await context.ReadRawCheckpointsAsync(aggregateId);

        await context.Eraser.ErasePersonalDataAsync(aggregateId, new ErasureRequest("ticket", [6], [MemoPath]));

        // The checkpoint at version 6 has revision 4, so only a boundary by version leaves it out of the start state.
        var checkpoints = await context.ReadRawCheckpointsAsync(aggregateId);
        checkpointsBefore.Select(checkpoint => checkpoint["_id"]["Version"].ToInt64()).Should().Equal(2, 6, 8);
        checkpointsBefore.Select(checkpoint => checkpoint["Revision"].ToInt64()).Should().Equal(2, 4, 6);
        checkpoints.Select(checkpoint => checkpoint["_id"]["Version"].ToInt64()).Should().Equal(2, 6, 8);
        checkpoints.Select(checkpoint => checkpoint["Revision"].ToInt64()).Should().Equal(2, 4, 6);
        checkpoints[0].Equals(checkpointsBefore[0]).Should().BeTrue("the checkpoint below the erased version is untouched");
        Memos(checkpoints[1]).Should().Equal(DefaultMemo, DefaultMemo, DefaultMemo, null);
        Memos(checkpoints[2]).Should().Equal(DefaultMemo, DefaultMemo, DefaultMemo, null, DefaultMemo, DefaultMemo);

        var readModel = Proven(await context.Repository.GetAsync(aggregateId));
        readModel.Memos.Should().Equal(DefaultMemo, DefaultMemo, DefaultMemo, null, DefaultMemo, DefaultMemo);
        readModel.Version.Should().Be(8);
        readModel.Revision.Should().Be(6);

        await context.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(aggregateId, 10)]);
        Proven(await context.Repository.GetAsync(aggregateId)).Memos
                                                              .Should().Equal(DefaultMemo, DefaultMemo, DefaultMemo, null, DefaultMemo, DefaultMemo, DefaultMemo);
        (await context.CreateVerifier().VerifyStreamAsync(aggregateId)).Should().Be(StreamVerificationResult.Intact);
    }

    [Test]
    public async Task ErasePersonalDataAsync_ConcurrentErasure_ThrowsMongoConcurrencyExceptionAndPersistsNothing()
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(2);
        ((MongoEventStore<LedgerAggregate>)context.Store).AfterStreamHeadRead = _ => context.UnsetSaltAsync(aggregateId, 1, MemoPath);

        var act = () => context.Eraser.ErasePersonalDataAsync(aggregateId, new ErasureRequest("ticket"));

        await act.Should().ThrowAsync<MongoConcurrencyException>().WithMessage("*erased concurrently*");
        (await context.CountEventsAsync(aggregateId)).Should().Be(2);
        (await context.FindEventAsync(aggregateId, 2))[MemoPath].AsString.Should().Be(DefaultMemo);
    }

    [Test]
    public async Task ErasePersonalDataAsync_EmptyAggregateId_ThrowsArgumentException()
    {
        var context = await CreateContextAsync();

        var act = () => context.Eraser.ErasePersonalDataAsync(Guid.Empty, new ErasureRequest("ticket"));

        await act.Should().ThrowAsync<ArgumentException>().WithParameterName("aggregateId");
    }

    [Test]
    public async Task ErasePersonalDataAsync_EventAppendedAfterWholeStreamSelection_ThrowsMongoConcurrencyExceptionAndPersistsNothing()
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(2);
        var store = (MongoEventStore<LedgerAggregate>)context.Store;
        store.AfterStreamHeadRead = async ct =>
        {
            store.AfterStreamHeadRead = null;
            await store.AppendEventsAsync([LedgerStoreContext.CreateEntry(aggregateId, 3)], cancellationToken: ct);
        };

        var act = () => context.Eraser.ErasePersonalDataAsync(aggregateId, new ErasureRequest("ticket"));

        await act.Should().ThrowAsync<MongoConcurrencyException>().WithMessage("*after the personal data to erase was selected*");
        await AssertUntouchedAsync(context, aggregateId, 3);
        Proven(await context.Repository.GetAsync(aggregateId)).Memos.Should().Equal(DefaultMemo, DefaultMemo, DefaultMemo);
    }

    [Test]
    public async Task ErasePersonalDataAsync_MalformedCommitments_ThrowsMongoEventStoreExceptionAndPersistsNothing()
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(2);
        var duplicate = (await context.FindEventAsync(aggregateId, 2))[EventIntegrityChain.ElementName]["PersonalData"].AsBsonArray[0].AsBsonDocument;
        await context.Events.UpdateOneAsync(
            EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, 2),
            Builders<BsonDocument>.Update.Push($"{EventIntegrityChain.ElementName}.PersonalData", duplicate));

        var act = () => context.Eraser.ErasePersonalDataAsync(aggregateId, new ErasureRequest("ticket"));

        await act.Should().ThrowAsync<MongoEventStoreException>().WithMessage("*version 2*malformed*");
        (await context.CountEventsAsync(aggregateId)).Should().Be(2);
        (await context.FindEventAsync(aggregateId, 1))[MemoPath].AsString.Should().Be(DefaultMemo);
    }

    [Test]
    public async Task ErasePersonalDataAsync_NonexistentStream_ThrowsMongoEventStoreException()
    {
        var context = await CreateContextAsync();
        var aggregateId = Guid.NewGuid();

        var actAll = () => context.Eraser.ErasePersonalDataAsync(aggregateId, new ErasureRequest("ticket"));
        var actPaths = () => context.Eraser.ErasePersonalDataAsync(aggregateId, new ErasureRequest("ticket", paths: [MemoPath]));

        await actAll.Should().ThrowAsync<MongoEventStoreException>().WithMessage("*no events*");
        await actPaths.Should().ThrowAsync<MongoEventStoreException>().WithMessage("*no events*");
    }

    [Test]
    public async Task ErasePersonalDataAsync_PathNotRecorded_ThrowsMongoEventStoreExceptionAndPersistsNothing()
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(2);

        var act = () => context.Eraser.ErasePersonalDataAsync(aggregateId, new ErasureRequest("ticket", paths: [MemoPath, AccountPath]));

        await act.Should().ThrowAsync<MongoEventStoreException>().WithMessage($"*'{AccountPath}'*not recorded*");
        await AssertUntouchedAsync(context, aggregateId, 2);
    }

    [Test]
    public async Task ErasePersonalDataAsync_PathRecordedOnlyWithoutSalt_SucceedsWithoutAppending()
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(2);
        await context.Eraser.ErasePersonalDataAsync(aggregateId, new ErasureRequest("first", paths: [MemoPath]));

        var erased = await context.Eraser.ErasePersonalDataAsync(aggregateId, new ErasureRequest("second", paths: [MemoPath]));

        erased.Should().BeEmpty();
        (await context.CountEventsAsync(aggregateId)).Should().Be(3, "the second request appends no event");
    }

    [Test]
    public async Task ErasePersonalDataAsync_ProtectionDisabled_ThrowsInvalidOperationException()
    {
        var context = await CreateContextAsync(false);
        var aggregateId = await context.AppendStreamAsync(1);
        var eraser = new PersonalDataEraser<LedgerAggregate>(context.MongoHelper, context.Options, context.Store);

        var act = () => eraser.ErasePersonalDataAsync(aggregateId, new ErasureRequest("ticket"));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*integrity protection is disabled*");
        context.ServiceProvider.GetService<PersonalDataEraser<LedgerAggregate>>().Should().BeNull();
        (await context.CountEventsAsync(aggregateId)).Should().Be(1);
    }

    [Test]
    public async Task ErasePersonalDataAsync_ReadModelAtOtherVersion_ThrowsMongoEventStoreExceptionAndPersistsNothing()
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(2);
        await context.MongoHelper.Database.GetCollection<BsonDocument>(context.Options.ReadModelCollectionName)
                     .UpdateOneAsync(Builders<BsonDocument>.Filter.Eq("_id", new BsonBinaryData(aggregateId, GuidRepresentation.Standard)),
                                     Builders<BsonDocument>.Update.Set("Version", 99L));

        var act = () => context.Eraser.ErasePersonalDataAsync(aggregateId, new ErasureRequest("ticket"));

        await act.Should().ThrowAsync<MongoEventStoreException>().WithMessage("*reached version 2*stored read model is at version 99*");
        await AssertUntouchedAsync(context, aggregateId, 2);
    }

    [Test]
    public async Task ErasePersonalDataAsync_ReplayThrows_PersistsNothing()
    {
        var context = await CreateContextAsync();
        var aggregateId = Guid.NewGuid();
        var entry = LedgerStoreContext.CreateEntry(aggregateId, 1);
        entry.Details.Category = LedgerEntryRecordedEvent.RequiresMemoCategory;
        await context.Store.AppendEventsAsync([entry, LedgerStoreContext.CreateEntry(aggregateId, 2)]);

        var act = () => context.Eraser.ErasePersonalDataAsync(aggregateId, new ErasureRequest("ticket"));

        await act.Should().ThrowAsync<MongoEventValidationException>().WithMessage("*requires a memo*");
        await AssertUntouchedAsync(context, aggregateId, 2);
        Proven(await context.Repository.GetAsync(aggregateId)).Memos.Should().Equal(DefaultMemo, DefaultMemo);
    }

    [Test]
    public async Task ErasePersonalDataAsync_RequestedVersionMissing_ThrowsMongoEventStoreExceptionAndPersistsNothing()
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(2);

        var act = () => context.Eraser.ErasePersonalDataAsync(aggregateId, new ErasureRequest("ticket", [2, 9]));

        await act.Should().ThrowAsync<MongoEventStoreException>().WithMessage("*Version 9*does not exist*");
        await AssertUntouchedAsync(context, aggregateId, 2);
    }

    [Test]
    public async Task ErasePersonalDataAsync_RequestedVersionWithoutCommitments_ThrowsMongoEventStoreExceptionAndPersistsNothing()
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(2);
        await context.Store.AppendEventsAsync([LedgerStoreContext.CreateAudit(aggregateId, 3)]);

        var act = () => context.Eraser.ErasePersonalDataAsync(aggregateId, new ErasureRequest("ticket", [3]));

        await act.Should().ThrowAsync<MongoEventStoreException>().WithMessage("*Version 3*no recorded personal data*");
        await AssertUntouchedAsync(context, aggregateId, 3);
    }

    [Test]
    public async Task ErasePersonalDataAsync_SelectedVersionsAndPaths_ErasesOnlyTheSelection()
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(3);

        var erased = await context.Eraser.ErasePersonalDataAsync(aggregateId, new ErasureRequest("ticket", [2], [MemoPath]));

        Describe(erased).Should().Equal((2, MemoPath));
        var second = await context.FindEventAsync(aggregateId, 2);
        second[MemoPath].Should().Be(BsonNull.Value);
        second["Counterparty"]["Name"].AsString.Should().Be("Alice Example");
        var commitments = LedgerStoreContext.ReadIntegrity(second).PersonalData;
        commitments.Should().NotBeNull();
        commitments.Single(c => c.Path == MemoPath).Salt.Should().BeNull();
        commitments.Single(c => c.Path == NamePath).Salt.Should().HaveCount(PersonalDataCommitments.SaltLength);
        foreach (var version in new[]
                 {
                     1L,
                     3L
                 })
        {
            (await context.FindEventAsync(aggregateId, version))[MemoPath].AsString.Should().Be(DefaultMemo);
        }

        Proven(await context.Repository.GetAsync(aggregateId)).Memos.Should().Equal(DefaultMemo, null, DefaultMemo);
        (await context.CreateVerifier().VerifyStreamAsync(aggregateId)).Should().Be(StreamVerificationResult.Intact);
    }

    [Test]
    public async Task ErasePersonalDataAsync_TamperedValueWithSalt_ThrowsMongoEventStoreExceptionAndKeepsTheEvidence()
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(2);
        await context.Events.UpdateOneAsync(
            EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, 1),
            Builders<BsonDocument>.Update.Set(MemoPath, "forged"));

        var act = () => context.Eraser.ErasePersonalDataAsync(aggregateId, new ErasureRequest("ticket"));

        await act.Should().ThrowAsync<MongoEventStoreException>().WithMessage("*'Memo' at version 1*does not match its commitment*");
        (await context.CountEventsAsync(aggregateId)).Should().Be(2);
        var tampered = await context.FindEventAsync(aggregateId, 1);
        tampered[MemoPath].AsString.Should().Be("forged");
        LedgerStoreContext.ReadIntegrity(tampered).PersonalData.Should().AllSatisfy(commitment => commitment.Salt.Should().NotBeNull());
        (await context.CreateVerifier().VerifyStreamAsync(aggregateId))
            .Should().Be(StreamVerificationResult.Broken(1, StreamVerificationFailure.PersonalDataMismatch));
    }

    [Test]
    public async Task ErasePersonalDataAsync_UnsealedSelectedEvent_ThrowsMongoEventStoreExceptionAndPersistsNothing()
    {
        var databaseName = $"IntegrityTestDb_{Guid.NewGuid():N}";
        var unprotected = await CreateContextAsync(false, false, databaseName);
        var context = await CreateContextAsync(databaseName: databaseName);
        var aggregateId = await unprotected.AppendStreamAsync(2);

        var act = () => context.Eraser.ErasePersonalDataAsync(aggregateId, new ErasureRequest("ticket"));

        await act.Should().ThrowAsync<MongoEventStoreException>().WithMessage("*Version 1*not sealed*");
        (await context.CountEventsAsync(aggregateId)).Should().Be(2);
        (await context.FindEventAsync(aggregateId, 1)).Contains(EventIntegrityChain.ElementName).Should().BeFalse("selection fails before the append");
    }

    [Test]
    [TestCase("FormatVersion", 2)]
    [TestCase("Algorithm", "SHA-512")]
    public async Task ErasePersonalDataAsync_UnsupportedIntegrityFormat_ThrowsMongoEventStoreExceptionAndPersistsNothing(String field, Object value)
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(2);
        await context.Events.UpdateOneAsync(
            EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, 2),
            Builders<BsonDocument>.Update.Set($"{EventIntegrityChain.ElementName}.{field}", BsonValue.Create(value)));

        var act = () => context.Eraser.ErasePersonalDataAsync(aggregateId, new ErasureRequest("ticket"));

        await act.Should().ThrowAsync<MongoEventStoreException>().WithMessage("*Version 2*unsupported integrity format*");
        await AssertUntouchedAsync(context, aggregateId, 2);
    }

    [Test]
    public async Task ErasePersonalDataAsync_UpdatesOfErasedEvents_AreNotAnchorCandidates()
    {
        var context = await CreateContextAsync(anchorCandidates: true);
        var aggregateId = await context.AppendStreamAsync(2);
        await context.CreateConsumer().DrainAsync(10);

        await context.Eraser.ErasePersonalDataAsync(aggregateId, new ErasureRequest("ticket"));

        var deadline = DateTime.UtcNow.AddSeconds(15);
        AnchorCandidateBatch batch;
        do
        {
            batch = await context.CandidateSource.ReadBatchAsync(10, CancellationToken.None);
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("The erasure event was not read as a candidate within the timeout.");
            }
        } while (batch.Candidates.Count == 0);

        batch.Candidates.Should().OnlyContain(candidate => candidate.AggregateId == aggregateId && candidate.Version == 3);
        (await context.CandidateSource.ReadBatchAsync(10, CancellationToken.None)).Candidates.Should().BeEmpty();
    }

    [Test]
    public async Task ErasePersonalDataAsync_ValueRestoredWithoutSalt_ThrowsMongoEventStoreExceptionAndPersistsNothing()
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(2);
        await context.Eraser.ErasePersonalDataAsync(aggregateId, new ErasureRequest("ticket", [1], [MemoPath]));
        await context.Events.UpdateOneAsync(
            EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, 1),
            Builders<BsonDocument>.Update.Set(MemoPath, "restored"));

        var act = () => context.Eraser.ErasePersonalDataAsync(aggregateId, new ErasureRequest("ticket"));

        await act.Should().ThrowAsync<MongoEventStoreException>().WithMessage("*'Memo' at version 1*without its salt but is not erased*");
        (await context.CountEventsAsync(aggregateId)).Should().Be(3);
        (await context.FindEventAsync(aggregateId, 1))[MemoPath].AsString.Should().Be("restored");
        var second = await context.FindEventAsync(aggregateId, 2);
        second[MemoPath].AsString.Should().Be(DefaultMemo);
        LedgerStoreContext.ReadIntegrity(second).PersonalData.Should().AllSatisfy(commitment => commitment.Salt.Should().NotBeNull());
    }

    [Test]
    public async Task ErasePersonalDataAsync_ValuesErasedOutsideTheEraser_RebuildsFromTheirVersion()
    {
        var context = await CreateContextAsync(configureOptions: options => options.CheckpointInterval = 2);
        var aggregateId = Guid.NewGuid();
        for (var version = 1; version <= 6; version++)
        {
            await context.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(aggregateId, version)]);
        }

        for (var version = 1; version <= 3; version++)
        {
            await context.Events.UpdateOneAsync(
                EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, version),
                Builders<BsonDocument>.Update.Set(MemoPath, BsonNull.Value));
            await context.UnsetSaltAsync(aggregateId, version, MemoPath);
        }

        var erased = await context.Eraser.ErasePersonalDataAsync(aggregateId, new ErasureRequest("ticket", paths: [MemoPath]));

        Describe(erased).Should().Equal((4, MemoPath), (5, MemoPath), (6, MemoPath));
        var checkpoints = await context.ReadRawCheckpointsAsync(aggregateId);
        checkpoints.Select(checkpoint => checkpoint["_id"]["Version"].ToInt64()).Should().Equal(2, 4, 6);
        Memos(checkpoints[0]).Should().Equal(null, null);
        Memos(checkpoints[1]).Should().Equal(null, null, null, null);
        Proven(await context.Repository.GetAsync(aggregateId)).Memos.Should().Equal(null, null, null, null, null, null);
        (await context.CreateVerifier().VerifyStreamAsync(aggregateId))
            .Should().Be(StreamVerificationResult.IntactWith([
                new UnrecordedErasure
                {
                    Version = 1,
                    Path = MemoPath
                },
                new UnrecordedErasure
                {
                    Version = 2,
                    Path = MemoPath
                },
                new UnrecordedErasure
                {
                    Version = 3,
                    Path = MemoPath
                }
            ]));
    }

    [Test]
    [TestCase(false)]
    [TestCase(true)]
    public async Task ErasePersonalDataAsync_WholeStream_NullsValuesRemovesSaltsAndRecordsErasure(Boolean bulkWrite)
    {
        var context = await CreateContextAsync(bulkWrite: bulkWrite);
        var aggregateId = await context.AppendStreamAsync(2);
        await context.Store.AppendEventsAsync([LedgerStoreContext.CreateAudit(aggregateId, 3)]);

        var erased = await context.Eraser.ErasePersonalDataAsync(aggregateId, new ErasureRequest("ticket-42"));

        Describe(erased).Should().Equal((1, $"{MemoPath},{NamePath}"), (2, $"{MemoPath},{NamePath}"));
        for (var version = 1; version <= 2; version++)
        {
            var stored = await context.FindEventAsync(aggregateId, version);
            stored[MemoPath].Should().Be(BsonNull.Value);
            stored["Counterparty"]["Name"].Should().Be(BsonNull.Value);
            stored["Counterparty"]["Account"].AsString.Should().Be("DE00 1234 5678");
            var commitments = LedgerStoreContext.ReadIntegrity(stored).PersonalData;
            commitments.Should().NotBeNull();
            commitments.Select(c => c.Path).Should().Equal(MemoPath, NamePath);
            commitments.Should().AllSatisfy(commitment =>
            {
                commitment.Salt.Should().BeNull();
                commitment.Commitment.Should().HaveCount(PersonalDataCommitments.CommitmentLength);
            });
        }

        var audit = await context.FindEventAsync(aggregateId, 3);
        audit[EventIntegrityChain.ElementName].AsBsonDocument.Contains("PersonalData").Should().BeFalse();
        var erasure = await context.FindEventAsync(aggregateId, 4);
        erasure[EventDocumentFields<LedgerAggregate>.Discriminator].AsBsonArray.Last().AsString
                                                                   .Should().Be(PersonalDataErased<LedgerAggregate>.Discriminator);
        erasure["Reference"].AsString.Should().Be("ticket-42");
        erasure["Entries"].AsBsonArray.Select(entry => entry["Version"].ToInt64()).Should().Equal(1, 2);
        erasure["Entries"][0]["Paths"].AsBsonArray.Select(path => path.AsString).Should().Equal(MemoPath, NamePath);
        erasure["Revision"].ToInt64().Should().Be(2);
        LedgerStoreContext.ReadIntegrity(erasure).SealMode.Should().Be(IntegritySealMode.Append);

        var events = new List<Event<LedgerAggregate>>();
        await foreach (var @event in context.Store.GetEventStreamAsync(aggregateId))
        {
            events.Add(@event);
        }

        events.Should().HaveCount(4);
        events[3].Should().BeOfType<PersonalDataErased<LedgerAggregate>>().Which.Reference.Should().Be("ticket-42");
        ((LedgerEntryRecordedEvent)events[0]).Memo.Should().BeNull();
        var readModel = await context.Repository.GetAsync(aggregateId);
        readModel.Should().NotBeNull();
        readModel.Memos.Should().Equal(null, null);
        readModel.EntryCount.Should().Be(2);
        readModel.Revision.Should().Be(2);
        readModel.Version.Should().Be(2);
        (await context.CreateVerifier().VerifyStreamAsync(aggregateId)).Should().Be(StreamVerificationResult.Intact);
    }

    [Test]
    public async Task ErasePersonalDataAsync_WithCheckpoints_RebuildsReadModelAndAffectedCheckpointsOnly()
    {
        var context = await CreateContextAsync(configureOptions: options => options.CheckpointInterval = 2);
        var aggregateId = Guid.NewGuid();
        for (var version = 1; version <= 5; version++)
        {
            await context.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(aggregateId, version)]);
        }

        var checkpointsBefore = await context.ReadRawCheckpointsAsync(aggregateId);

        await context.Eraser.ErasePersonalDataAsync(aggregateId, new ErasureRequest("ticket", [3]));

        var checkpoints = await context.ReadRawCheckpointsAsync(aggregateId);
        checkpointsBefore.Should().HaveCount(2);
        checkpoints.Should().HaveCount(2);
        checkpoints[0].Equals(checkpointsBefore[0]).Should().BeTrue("the checkpoint below the erased version is untouched");
        checkpoints[1]["_id"]["Version"].AsInt64.Should().Be(4);
        checkpoints[1]["Revision"].AsInt64.Should().Be(4);
        Memos(checkpoints[1]).Should().Equal(DefaultMemo, DefaultMemo, null, DefaultMemo);
        checkpoints[1]["State"]["CreatedUtc"].Should().Be(checkpointsBefore[1]["State"]["CreatedUtc"]);

        var readModel = await context.Repository.GetAsync(aggregateId);
        readModel!.Memos.Should().Equal(DefaultMemo, DefaultMemo, null, DefaultMemo, DefaultMemo);
        Proven(await context.Repository.GetAtVersionAsync(aggregateId, 3)).Memos.Should().Equal(DefaultMemo, DefaultMemo, null);
        Proven(await context.Repository.GetAtRevisionAsync(aggregateId, 4)).Memos.Should().Equal(DefaultMemo, DefaultMemo, null, DefaultMemo);

        await context.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(aggregateId, 7)]);
        Proven(await context.Repository.GetAsync(aggregateId)).Memos.Should().Equal(DefaultMemo, DefaultMemo, null, DefaultMemo, DefaultMemo, DefaultMemo);
        (await context.CreateVerifier().VerifyStreamAsync(aggregateId)).Should().Be(StreamVerificationResult.Intact);
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

    [Test]
    public async Task VerifyStreamAsync_ErasedValueWithoutErasureRecord_ReportsUnrecordedErasure()
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(3);
        await context.Events.UpdateOneAsync(
            EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, 2),
            Builders<BsonDocument>.Update.Set(MemoPath, BsonNull.Value));
        await context.UnsetSaltAsync(aggregateId, 2, MemoPath);

        var beforeErasure = await context.CreateVerifier().VerifyStreamAsync(aggregateId);
        await context.Eraser.ErasePersonalDataAsync(aggregateId, new ErasureRequest("ticket"));
        var afterErasure = await context.CreateVerifier().VerifyStreamAsync(aggregateId);

        beforeErasure.Should().Be(StreamVerificationResult.IntactWith([
            new UnrecordedErasure
            {
                Version = 2,
                Path = MemoPath
            }
        ]));
        beforeErasure.IsIntact.Should().BeTrue();
        afterErasure.Should().Be(StreamVerificationResult.IntactWith([
                                     new UnrecordedErasure
                                     {
                                         Version = 2,
                                         Path = MemoPath
                                     }
                                 ]),
                                 "the erasure event records only the values it erased");
    }

    [Test]
    public async Task VerifyStreamAsync_ErasureRecordsOfMalformedOrLaterEntries_AreIgnored()
    {
        var databaseName = $"IntegrityTestDb_{Guid.NewGuid():N}";
        var unprotected = await CreateContextAsync(false, false, databaseName);
        var sealing = await CreateContextAsync(databaseName: databaseName);
        var aggregateId = Guid.NewGuid();
        await unprotected.Store.AppendEventsAsync([
            LedgerStoreContext.CreateEntry(aggregateId, 1, memo: null),
            // Crafted like a tampered erasure event; only the eraser may append one, so the test acts as it.
            new PersonalDataErased<LedgerAggregate>
            {
                AggregateId = aggregateId,
                Version = 2,
                Reference = "ticket",
                CreatedByEraser = true
            },
            LedgerStoreContext.CreateEntry(aggregateId, 3, memo: null)
        ]);
        var entries = new BsonArray
        {
            new BsonDocument
            {
                { "Version", 1L },
                {
                    "Paths", new BsonArray
                    {
                        MemoPath
                    }
                }
            },
            "not an entry",
            new BsonDocument("Paths", new BsonArray
            {
                MemoPath
            }),
            new BsonDocument
            {
                { "Version", "3" },
                {
                    "Paths", new BsonArray
                    {
                        MemoPath
                    }
                }
            },
            new BsonDocument
            {
                { "Version", 3L },
                { "Paths", MemoPath }
            },
            new BsonDocument
            {
                { "Version", 3L },
                {
                    "Paths", new BsonArray
                    {
                        3
                    }
                }
            },
            new BsonDocument
            {
                { "Version", 3L },
                {
                    "Paths", new BsonArray
                    {
                        MemoPath
                    }
                }
            }
        };
        await unprotected.Events.UpdateOneAsync(EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, 2),
                                                Builders<BsonDocument>.Update.Set("Entries", entries));
        await sealing.Sweep.RunPassAsync();
        await sealing.UnsetSaltAsync(aggregateId, 1, MemoPath);
        await sealing.UnsetSaltAsync(aggregateId, 3, MemoPath);

        var result = await sealing.CreateVerifier().VerifyStreamAsync(aggregateId);

        result.Should().Be(StreamVerificationResult.IntactWith([
                               new UnrecordedErasure
                               {
                                   Version = 3,
                                   Path = MemoPath
                               }
                           ]),
                           "only a well-formed record of an earlier version documents an erasure");
    }

    [Test]
    public async Task VerifyStreamAsync_TamperedErasureEvent_ReportsHashMismatch()
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(2);
        await context.Eraser.ErasePersonalDataAsync(aggregateId, new ErasureRequest("ticket", [1]));
        await context.Events.UpdateOneAsync(
            EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, 3),
            Builders<BsonDocument>.Update.Push("Entries", new BsonDocument
            {
                { "Version", 2L },
                {
                    "Paths", new BsonArray
                    {
                        MemoPath
                    }
                }
            }));

        var result = await context.CreateVerifier().VerifyStreamAsync(aggregateId);

        result.Should().Be(StreamVerificationResult.Broken(3, StreamVerificationFailure.HashMismatch));
    }

    private static async Task AssertUntouchedAsync(LedgerStoreContext context, Guid aggregateId, Int64 expectedCount)
    {
        (await context.CountEventsAsync(aggregateId)).Should().Be(expectedCount);
        for (var version = 1; version <= expectedCount; version++)
        {
            var stored = await context.FindEventAsync(aggregateId, version);
            if (LedgerStoreContext.ReadIntegrity(stored).PersonalData is not { } commitments)
            {
                continue;
            }

            stored[MemoPath].AsString.Should().Be(DefaultMemo);
            commitments.Should().AllSatisfy(commitment => commitment.Salt.Should().NotBeNull());
        }
    }

    private static IEnumerable<(Int64 Version, String Paths)> Describe(IReadOnlyList<PersonalDataErasureEntry> entries)
        => entries.Select(entry => (entry.Version, String.Join(",", entry.Paths)));

    private static async Task<Boolean> IsAnchoredAsync(AnchorStore store, Guid stream, Int64 version)
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

    private static IEnumerable<String?> Memos(BsonDocument checkpoint)
        => checkpoint["State"]["Memos"].AsBsonArray.Select(memo => memo.IsBsonNull ? null : memo.AsString);

    private static LedgerAggregate Proven(LedgerAggregate? aggregate)
    {
        aggregate.Should().NotBeNull();
        return aggregate;
    }

    private static async Task RunUntilAsync(AnchoringRun run, Func<AnchoringRunResult, Task<Boolean>> isDone)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await isDone(await run.RunAsync()))
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("The expected anchoring state was not reached within the timeout.");
            }

            await Task.Delay(50);
        }
    }

    private async Task<LedgerStoreContext> CreateContextAsync(Boolean integrityProtection = true,
                                                              Boolean personalData = true,
                                                              String? databaseName = null,
                                                              Action<MongoEventStoreOptions<LedgerAggregate>>? configureOptions = null,
                                                              Boolean anchorCandidates = false,
                                                              Action<MongoBuilder>? configureMongo = null,
                                                              Boolean bulkWrite = false)
    {
        var context = await LedgerStoreContext.CreateAsync(_container,
                                                           integrityProtection,
                                                           bulkWrite,
                                                           databaseName,
                                                           configureOptions,
                                                           anchorCandidates: anchorCandidates,
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
