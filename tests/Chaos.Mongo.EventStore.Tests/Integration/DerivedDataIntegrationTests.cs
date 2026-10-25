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

public class DerivedDataIntegrationTests
{
    private const String MemoPath = "Memo";
    private readonly List<LedgerStoreContext> _contexts = [];
    private MongoDbContainer _container;

    [Test]
    public async Task AppendEventsAsync_Checkpoints_RecordChainHashOnlyWhenProtected()
    {
        var databaseName = $"IntegrityTestDb_{Guid.NewGuid():N}";
        var protectedContext = await CreateContextAsync(databaseName: databaseName);
        var unprotected = await CreateContextAsync(false, databaseName);
        var protectedStream = await AppendOneByOneAsync(protectedContext, 4);
        var unprotectedStream = await unprotected.AppendStreamAsync(2);

        var checkpoints = await protectedContext.ReadRawCheckpointsAsync(protectedStream);
        var unprotectedCheckpoints = await unprotected.ReadRawCheckpointsAsync(unprotectedStream);

        checkpoints.Should().HaveCount(2);
        foreach (var checkpoint in checkpoints)
        {
            var version = checkpoint["_id"]["Version"].ToInt64();
            var integrity = LedgerStoreContext.ReadIntegrity(await protectedContext.FindEventAsync(protectedStream, version));
            checkpoint[CheckpointDocument<LedgerAggregate>.ChainHashElementName].AsByteArray.Should().Equal(integrity.Hash);
        }

        unprotectedCheckpoints.Should().ContainSingle().Which.Contains(CheckpointDocument<LedgerAggregate>.ChainHashElementName).Should().BeFalse();
    }

    [Test]
    public async Task ErasePersonalDataAsync_RebuiltCheckpoints_KeepChainHashAndStayConsistent()
    {
        var context = await CreateContextAsync(personalData: true);
        var aggregateId = await AppendOneByOneAsync(context, 4);
        var before = await context.ReadRawCheckpointsAsync(aggregateId);

        await context.Eraser.ErasePersonalDataAsync(aggregateId, new ErasureRequest("ticket", [1], [MemoPath]));
        var after = await context.ReadRawCheckpointsAsync(aggregateId);
        var result = await GetVerifier(context).VerifyAsync(aggregateId);

        after.Select(c => c[CheckpointDocument<LedgerAggregate>.ChainHashElementName])
             .Should().Equal(before.Select(c => c[CheckpointDocument<LedgerAggregate>.ChainHashElementName]));
        result.IsConsistent.Should().BeTrue();
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
    public async Task VerifyAsync_AppendDuringCheck_ReadsOneSnapshot()
    {
        var context = await CreateContextAsync();
        var aggregateId = await AppendOneByOneAsync(context, 4);
        var verifier = GetVerifier(context);
        verifier.StreamVerified = async () =>
        {
            // A checkpoint at version 6 records a chain hash the verified stream does not contain.
            await context.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(aggregateId, 5)]);
            await context.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(aggregateId, 6)]);
        };

        var result = await verifier.VerifyAsync(aggregateId);

        result.IsConsistent.Should().BeTrue();
        result.Checkpoints.Select(c => c.Version).Should().Equal(2, 4);
        (await context.CountEventsAsync(aggregateId)).Should().Be(6);
    }

    [Test]
    public async Task VerifyAsync_BrokenChain_ComparesNoDerivedData()
    {
        var context = await CreateContextAsync();
        var aggregateId = await AppendOneByOneAsync(context, 4);
        await context.Events.UpdateOneAsync(EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, 3),
                                            Builders<BsonDocument>.Update.Set(MemoPath, "forged"));

        var result = await GetVerifier(context).VerifyAsync(aggregateId);

        result.Stream.Should().Be(StreamVerificationResult.Broken(3, StreamVerificationFailure.HashMismatch));
        result.ReadModel.Should().Be(ReadModelStatus.NotChecked);
        result.CreationTimeConsistent.Should().BeTrue();
        result.Checkpoints.Should().BeEmpty();
        result.IsConsistent.Should().BeFalse();
    }

    [Test]
    public async Task VerifyAsync_ChangedReadModelCreationTime_ReportsInconsistentCreationTime()
    {
        var context = await CreateContextAsync();
        var aggregateId = await AppendOneByOneAsync(context, 4);
        await context.MongoHelper.Database.GetCollection<BsonDocument>(context.Options.ReadModelCollectionName)
                     .UpdateOneAsync(new BsonDocument("_id", new BsonBinaryData(aggregateId, GuidRepresentation.Standard)),
                                     Builders<BsonDocument>.Update.Set("CreatedUtc", new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

        var result = await GetVerifier(context).VerifyAsync(aggregateId);

        result.ReadModel.Should().Be(ReadModelStatus.Matches);
        result.Checkpoints.Should().OnlyContain(c => c.IsConsistent);
        result.CreationTimeConsistent.Should().BeFalse();
        result.IsConsistent.Should().BeFalse();
    }

    [Test]
    public async Task VerifyAsync_CheckpointMovedToAnotherVersion_ReportsHashAndStateMismatch()
    {
        var context = await CreateContextAsync();
        var aggregateId = await AppendOneByOneAsync(context, 4);
        var checkpoints = GetCheckpoints(context);
        var original = (await context.ReadRawCheckpointsAsync(aggregateId))[0];
        var moved = original.DeepClone().AsBsonDocument;
        moved["_id"]["Version"] = 3L;
        await checkpoints.DeleteOneAsync(new BsonDocument("_id", original["_id"]));
        await checkpoints.InsertOneAsync(moved);

        var result = await GetVerifier(context).VerifyAsync(aggregateId);

        result.Checkpoints.Should().Equal(new CheckpointVerification(3, CheckpointHashStatus.Mismatch, false),
                                          new CheckpointVerification(4, CheckpointHashStatus.Matches, true));
        result.IsConsistent.Should().BeFalse();
    }

    [Test]
    public async Task VerifyAsync_CheckpointWithoutChainHash_IsUnverifiableButConsistent()
    {
        var databaseName = $"IntegrityTestDb_{Guid.NewGuid():N}";
        var unprotected = await CreateContextAsync(false, databaseName);
        var protectedContext = await CreateContextAsync(databaseName: databaseName);
        var aggregateId = await unprotected.AppendStreamAsync(2);
        await protectedContext.Sweep.RunPassAsync();
        await protectedContext.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(aggregateId, 3)]);
        await protectedContext.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(aggregateId, 4)]);

        var result = await GetVerifier(protectedContext).VerifyAsync(aggregateId);

        result.Checkpoints.Should().Equal(new CheckpointVerification(2, CheckpointHashStatus.Unverifiable, true),
                                          new CheckpointVerification(4, CheckpointHashStatus.Matches, true));
        result.ReadModel.Should().Be(ReadModelStatus.Matches);
        result.IsConsistent.Should().BeTrue();
    }

    [Test]
    public async Task VerifyAsync_ConsistentStreamWithObservationalEvents_MatchesReadModelAndCheckpoints()
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(2);
        await context.Store.AppendEventsAsync([LedgerStoreContext.CreateAudit(aggregateId, 3), LedgerStoreContext.CreateEntry(aggregateId, 4)]);

        var result = await GetVerifier(context).VerifyAsync(aggregateId);
        var unknown = await GetVerifier(context).VerifyAsync(Guid.NewGuid());

        result.Stream.IsIntact.Should().BeTrue();
        result.ReadModel.Should().Be(ReadModelStatus.Matches);
        result.Checkpoints.Should().OnlyContain(c => c.HashStatus == CheckpointHashStatus.Matches && c.StateMatches).And.HaveCount(1);
        result.IsConsistent.Should().BeTrue();
        unknown.ReadModel.Should().Be(ReadModelStatus.Matches, "a stream without events needs no read model");
        unknown.IsConsistent.Should().BeTrue();
    }

    [Test]
    [TestCase("readModel")]
    [TestCase("checkpoint")]
    public async Task VerifyAsync_DocumentRepeatingAnElementName_ReportsItInsteadOfFailing(String document)
    {
        var context = await CreateContextAsync();
        var aggregateId = await AppendOneByOneAsync(context, 4);
        var collection = document == "readModel"
            ? context.MongoHelper.Database.GetCollection<BsonDocument>(context.Options.ReadModelCollectionName)
            : GetCheckpoints(context);
        var stored = document == "readModel"
            ? await collection.Find(new BsonDocument("_id", new BsonBinaryData(aggregateId, GuidRepresentation.Standard))).SingleAsync()
            : (await context.ReadRawCheckpointsAsync(aggregateId))[0];
        var repeated = new BsonDocument
        {
            AllowDuplicateNames = true
        };
        repeated.AddRange(stored);
        repeated.Add("Revision", stored["Revision"]);
        await collection.DeleteOneAsync(new BsonDocument("_id", stored["_id"]));
        await collection.Database.GetCollection<RawBsonDocument>(collection.CollectionNamespace.CollectionName).InsertOneAsync(new RawBsonDocument(repeated.ToBson()));

        var result = await GetVerifier(context).VerifyAsync(aggregateId);

        result.IsConsistent.Should().BeFalse();
        if (document == "readModel")
        {
            result.ReadModel.Should().Be(ReadModelStatus.Differs);
            result.Checkpoints.Should().OnlyContain(c => c.IsConsistent);
        }
        else
        {
            result.ReadModel.Should().Be(ReadModelStatus.Matches);
            result.Checkpoints.Should().Equal(new CheckpointVerification(2, CheckpointHashStatus.Mismatch, false),
                                              new CheckpointVerification(4, CheckpointHashStatus.Matches, true));
        }
    }

    [Test]
    public async Task VerifyAsync_ManipulatedCheckpointState_ReportsStateMismatch()
    {
        var context = await CreateContextAsync();
        var aggregateId = await AppendOneByOneAsync(context, 4);
        var original = (await context.ReadRawCheckpointsAsync(aggregateId))[0];
        await GetCheckpoints(context).UpdateOneAsync(new BsonDocument("_id", original["_id"]),
                                                     Builders<BsonDocument>.Update.Set("State.Balance", new Decimal128(1000m)));

        var result = await GetVerifier(context).VerifyAsync(aggregateId);

        result.Checkpoints.Should().Equal(new CheckpointVerification(2, CheckpointHashStatus.Matches, false),
                                          new CheckpointVerification(4, CheckpointHashStatus.Matches, true));
        result.ReadModel.Should().Be(ReadModelStatus.Matches);
    }

    [Test]
    [TestCase(false)]
    [TestCase(true)]
    public async Task VerifyAsync_ManipulatedReadModel_ReportsIt(Boolean deleteReadModel)
    {
        var context = await CreateContextAsync();
        var aggregateId = await AppendOneByOneAsync(context, 4);
        var readModels = context.MongoHelper.Database.GetCollection<BsonDocument>(context.Options.ReadModelCollectionName);
        var filter = new BsonDocument("_id", new BsonBinaryData(aggregateId, GuidRepresentation.Standard));
        await (deleteReadModel
            ? (Task)readModels.DeleteOneAsync(filter)
            : readModels.UpdateOneAsync(filter, Builders<BsonDocument>.Update.Set("Balance", new Decimal128(1000m))));

        var result = await GetVerifier(context).VerifyAsync(aggregateId);

        result.ReadModel.Should().Be(deleteReadModel ? ReadModelStatus.Missing : ReadModelStatus.Differs);
        result.Checkpoints.Should().OnlyContain(c => c.IsConsistent);
        result.CreationTimeConsistent.Should().BeTrue();
        result.IsConsistent.Should().BeFalse();
    }

    [Test]
    public async Task VerifyAsync_ReplayReadingCreationTime_StartsWithTheStoredCreationTime()
    {
        var context = await CreateContextAsync();
        var aggregateId = Guid.NewGuid();
        for (var version = 1; version <= 4; version++)
        {
            var entry = LedgerStoreContext.CreateEntry(aggregateId, version);
            entry.Details.Category = LedgerEntryRecordedEvent.RecordsCreationDateCategory;
            await context.Store.AppendEventsAsync([entry]);
        }

        var result = await GetVerifier(context).VerifyAsync(aggregateId);

        result.ReadModel.Should().Be(ReadModelStatus.Matches);
        result.Checkpoints.Should().HaveCount(2).And.OnlyContain(c => c.IsConsistent);
        result.IsConsistent.Should().BeTrue();
    }

    [Test]
    public async Task VerifyAsync_RevisionRemovedAfterObservationalEvent_ReportsReadModelAndCheckpoints()
    {
        var context = await CreateContextAsync();
        var aggregateId = Guid.NewGuid();
        await context.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(aggregateId, 1)]);
        await context.Store.AppendEventsAsync([LedgerStoreContext.CreateAudit(aggregateId, 2)]);
        for (var version = 3; version <= 5; version++)
        {
            await context.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(aggregateId, version)]);
        }

        var intact = await GetVerifier(context).VerifyAsync(aggregateId);
        var idFilter = new BsonDocument("_id", new BsonBinaryData(aggregateId, GuidRepresentation.Standard));
        await context.MongoHelper.Database.GetCollection<BsonDocument>(context.Options.ReadModelCollectionName)
                     .UpdateOneAsync(idFilter, Builders<BsonDocument>.Update.Unset("Revision"));
        await GetCheckpoints(context).UpdateManyAsync(FilterDefinition<BsonDocument>.Empty,
                                                      Builders<BsonDocument>.Update.Unset("Revision").Unset("State.Revision"));

        var result = await GetVerifier(context).VerifyAsync(aggregateId);

        intact.IsConsistent.Should().BeTrue();
        intact.Checkpoints.Should().NotBeEmpty();
        result.ReadModel.Should().Be(ReadModelStatus.Differs, "version 5 is revision 4, so the read model cannot predate revisions");
        result.Checkpoints.Should().OnlyContain(c => !c.StateMatches);
        result.IsConsistent.Should().BeFalse();
    }

    [Test]
    public async Task VerifyAsync_RevisionRemovedFromStreamWithoutObservationalEvents_MatchesAsDocumentsWrittenBeforeRevisions()
    {
        var context = await CreateContextAsync();
        var aggregateId = await AppendOneByOneAsync(context, 4);
        var idFilter = new BsonDocument("_id", new BsonBinaryData(aggregateId, GuidRepresentation.Standard));
        await context.MongoHelper.Database.GetCollection<BsonDocument>(context.Options.ReadModelCollectionName)
                     .UpdateOneAsync(idFilter, Builders<BsonDocument>.Update.Unset("Revision"));
        await GetCheckpoints(context).UpdateManyAsync(FilterDefinition<BsonDocument>.Empty,
                                                      Builders<BsonDocument>.Update.Unset("Revision").Unset("State.Revision"));

        var result = await GetVerifier(context).VerifyAsync(aggregateId);

        result.ReadModel.Should().Be(ReadModelStatus.Matches);
        result.Checkpoints.Should().HaveCount(2).And.OnlyContain(c => c.IsConsistent);
        result.IsConsistent.Should().BeTrue();
    }

    [Test]
    public async Task VerifyAsync_SecondCheckpointWithReorderedIdentifier_IsComparedOnItsOwn()
    {
        var context = await CreateContextAsync();
        var aggregateId = await AppendOneByOneAsync(context, 4);
        var original = (await context.ReadRawCheckpointsAsync(aggregateId))[0];
        var forged = new BsonDocument
        {
            { "_id", new BsonDocument(original["_id"].AsBsonDocument.Elements.Reverse()) }
        };
        forged.AddRange(original.Elements.Where(element => element.Name != "_id"));
        forged["State"]["Balance"] = new Decimal128(1000m);
        await GetCheckpoints(context).InsertOneAsync(forged);

        var result = await GetVerifier(context).VerifyAsync(aggregateId);

        result.Checkpoints.Should().HaveCount(3);
        result.Checkpoints.Where(c => c.Version == 2).Select(c => c.StateMatches).Should().BeEquivalentTo([true, false]);
        result.Checkpoints.Should().OnlyContain(c => c.HashStatus == CheckpointHashStatus.Matches);
        result.IsConsistent.Should().BeFalse();
    }

    /// <summary>
    /// Appends a stream one event per batch, so every multiple of the checkpoint interval gets a checkpoint.
    /// </summary>
    private static async Task<Guid> AppendOneByOneAsync(LedgerStoreContext context, Int32 eventCount)
    {
        var aggregateId = Guid.NewGuid();
        for (var version = 1; version <= eventCount; version++)
        {
            await context.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(aggregateId, version)]);
        }

        return aggregateId;
    }

    private static IMongoCollection<BsonDocument> GetCheckpoints(LedgerStoreContext context)
        => context.MongoHelper.Database.GetCollection<BsonDocument>(context.Options.CheckpointCollectionName);

    private static DerivedDataVerifier<LedgerAggregate> GetVerifier(LedgerStoreContext context)
        => context.ServiceProvider.GetRequiredService<DerivedDataVerifier<LedgerAggregate>>();

    private async Task<LedgerStoreContext> CreateContextAsync(Boolean integrityProtection = true, String? databaseName = null, Boolean personalData = false)
    {
        var context = await LedgerStoreContext.CreateAsync(_container,
                                                           integrityProtection,
                                                           databaseName: databaseName,
                                                           configureOptions: options => options.CheckpointInterval = 2,
                                                           personalData: personalData);
        _contexts.Add(context);
        return context;
    }
}
