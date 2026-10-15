// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Chaos.Mongo.EventStore.Integrity;
using Chaos.Mongo.EventStore.Tests.Integrity;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using NUnit.Framework;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Testcontainers.MongoDb;

/// <summary>
/// Regenerates the committed test vectors of the tamper-evidence specification from real library output. The vectors
/// are committed once; <see cref="TamperEvidenceVectorTests"/> checks that the library still agrees with them, and the
/// reference verifier checks them independently.
/// </summary>
[Explicit("Regenerates the committed test vectors under spec/tamper-evidence/v1.")]
public class TamperEvidenceVectorGenerator
{
    public static readonly Guid AppendedStream = Guid.Parse("0f5a9c2e-6b1d-4c3a-9e2f-1a2b3c4d5e01");
    public static readonly Guid PersonalDataStream = Guid.Parse("0f5a9c2e-6b1d-4c3a-9e2f-1a2b3c4d5e02");
    public static readonly Guid RenamedStream = Guid.Parse("0f5a9c2e-6b1d-4c3a-9e2f-1a2b3c4d5e05");
    public static readonly Guid RetroactiveStream = Guid.Parse("0f5a9c2e-6b1d-4c3a-9e2f-1a2b3c4d5e03");
    public static readonly Guid TamperedStream = Guid.Parse("0f5a9c2e-6b1d-4c3a-9e2f-1a2b3c4d5e04");
    private static readonly DateTime CreatedUtc = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
    private MongoDbContainer _container;
    private TestTimestampAuthority _testAuthority;

    [OneTimeTearDown]
    public void DisposeTestAuthority() => _testAuthority.Dispose();

    [Test]
    public async Task Generate()
    {
        var directory = TamperEvidenceVectorTests.VectorDirectory;
        Directory.CreateDirectory(directory);
        var databaseName = $"VectorDb_{Guid.NewGuid():N}";
        var testAuthority = _testAuthority;
        await using var unprotected = await LedgerStoreContext.CreateAsync(_container, false, databaseName: databaseName);
        await using var personalData = await LedgerStoreContext.CreateAsync(_container, databaseName: databaseName, personalData: true);
        await using var context = await LedgerStoreContext.CreateAsync(
            _container,
            databaseName: databaseName,
            configureMongo: mongo => mongo.WithIntegrityAnchoring(anchoring => anchoring.WithTimestampAuthority(_ => testAuthority.CreateAuthority()))
                                          .WithIntegrityVerification(verification => verification.WithTimestampAuthorityTrust(
                                                                         "test-tsa",
                                                                         trust => trust.TrustedRoots.Add(testAuthority.Root))));

        await AppendAsync(context, AppendedStream, 1, 3);

        await AppendAsync(personalData, PersonalDataStream, 1, 3);
        await personalData.Events.UpdateOneAsync(EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(PersonalDataStream, 2),
                                                 Builders<BsonDocument>.Update.Set("Memo", BsonNull.Value));
        await personalData.UnsetSaltAsync(PersonalDataStream, 2, "Memo");
        await personalData.Eraser.ErasePersonalDataAsync(PersonalDataStream, new ErasureRequest("TICKET-1", [1], ["Memo"]));

        await AppendAsync(context, RetroactiveStream, 1, 2);
        await AppendAsync(unprotected, RetroactiveStream, 3, 2);

        await AppendAsync(context, TamperedStream, 1, 3);

        await AppendAsync(unprotected, RenamedStream, 1, 2);
        await unprotected.Events.UpdateManyAsync(EventDocumentFields<LedgerAggregate>.ForAggregate<BsonDocument>(RenamedStream),
                                                 Builders<BsonDocument>.Update.Set("AggregateType", "RenamedLedger"));
        await context.Sweep.RunPassAsync();

        await RunUntilAnchoredAsync(context, (AppendedStream, 3), (PersonalDataStream, 4), (RetroactiveStream, 4), (TamperedStream, 3), (RenamedStream, 2));
        await AppendAsync(context, AppendedStream, 4, 1);
        await RunUntilAnchoredAsync(context, (AppendedStream, 4));

        await context.Events.UpdateOneAsync(EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(TamperedStream, 2),
                                            Builders<BsonDocument>.Update.Set("Memo", "tampered"));
        await ConfirmFirstAnchorAsync(context, testAuthority);

        var streams = await WriteStreamsAsync(context, directory);
        var anchors = await WriteAnchorsAsync(context, directory, testAuthority);
        await File.WriteAllTextAsync(Path.Combine(directory, "streams.json"), Serialize(streams));
        await File.WriteAllTextAsync(Path.Combine(directory, "anchors.json"), Serialize(anchors));
        await File.WriteAllTextAsync(Path.Combine(directory, "merkle.json"), Serialize(TamperEvidenceVectorTests.BuildMerkleVectors()));
        await File.WriteAllTextAsync(Path.Combine(directory, "evidence-chunked.json"), Serialize(TamperEvidenceVectorTests.BuildChunkedEvidenceVector()));
    }

    [OneTimeSetUp]
    public async Task GetMongoDbContainer()
    {
        _testAuthority = new TestTimestampAuthority();
        _container = await MongoDbTestContainer.StartContainerAsync();
    }

    private static Task AppendAsync(LedgerStoreContext context, Guid aggregateId, Int64 firstVersion, Int32 count)
        => context.Store.AppendEventsAsync(Enumerable.Range(0, count).Select(offset =>
        {
            var version = firstVersion + offset;
            return LedgerStoreContext.CreateEntry(aggregateId,
                                                  version,
                                                  new Guid(SHA256.HashData([.. aggregateId.ToByteArray(), (Byte)version]).AsSpan(0, 16)),
                                                  CreatedUtc.AddMinutes(version));
        }));

    /// <summary>
    /// Completes the revocation evidence of the first anchor's token with a CRL issued after the grace period, as the
    /// revocation confirmation of the anchoring run would.
    /// </summary>
    private static async Task ConfirmFirstAnchorAsync(LedgerStoreContext context, TestTimestampAuthority testAuthority)
    {
        var store = context.ServiceProvider.GetRequiredService<AnchorVerifier>().Store;
        var first = AnchorStore.ReadAnchor(await store.Anchors.Find(new BsonDocument(AnchorStore.SequenceField, 1L)).SingleAsync());
        var evidence = new AnchorEvidenceStore(context.MongoHelper, context.ServiceProvider.GetRequiredService<IntegrityAnchoringOptions>());
        var crl = testAuthority.BuildCrl(first.Timestamps[0].TimestampUtc.AddHours(25));
        var hash = await evidence.StoreAsync(crl, CancellationToken.None);
        await store.Anchors.UpdateOneAsync(new BsonDocument("_id", new BsonBinaryData(first.Id, GuidRepresentation.Standard)),
                                           Builders<BsonDocument>.Update
                                                                 .Set("Timestamps.0.RevocationStatus", nameof(TimestampRevocationStatus.Confirmed))
                                                                 .Set("Timestamps.0.RevocationStatusUtc", first.Timestamps[0].TimestampUtc.AddHours(25))
                                                                 .Set("Timestamps.0.ConfirmingRevocationListHashes", new BsonArray
                                                                 {
                                                                     new BsonBinaryData(hash)
                                                                 }));
    }

    private static async Task DumpAsync(IMongoCollection<RawBsonDocument> collection, FilterDefinition<RawBsonDocument> filter, SortDefinition<RawBsonDocument> sort, String path)
    {
        await using var output = File.Create(path);
        foreach (var document in await collection.Find(filter).Sort(sort).ToListAsync())
        {
            using (document)
            {
                await output.WriteAsync(document.ToBson());
            }
        }
    }

    private static async Task RunUntilAnchoredAsync(LedgerStoreContext context, params (Guid AggregateId, Int64 Version)[] heads)
    {
        var run = context.ServiceProvider.GetRequiredService<AnchoringRun>();
        var store = context.ServiceProvider.GetRequiredService<AnchorVerifier>().Store;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        foreach (var (aggregateId, version) in heads)
        {
            while ((await store.FindCompletedStreamLeavesAsync(nameof(LedgerAggregate), aggregateId, version, CancellationToken.None)).Count == 0)
            {
                Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "The streams were not anchored within the timeout.");
                await run.RunAsync();
            }
        }
    }

    private static String Serialize(JsonNode node) => node.ToJsonString(new JsonSerializerOptions
    {
        WriteIndented = true
    }) + Environment.NewLine;

    private static async Task<JsonObject> WriteAnchorsAsync(LedgerStoreContext context, String directory, TestTimestampAuthority testAuthority)
    {
        var verifier = context.ServiceProvider.GetRequiredService<AnchorVerifier>();
        var options = context.ServiceProvider.GetRequiredService<IntegrityAnchoringOptions>();
        var committed = Builders<RawBsonDocument>.Filter.Exists(AnchorStore.SequenceField);
        var database = context.MongoHelper.Database;
        await DumpAsync(database.GetCollection<RawBsonDocument>(options.AnchorsCollectionName), committed,
                        Builders<RawBsonDocument>.Sort.Ascending(AnchorStore.SequenceField), Path.Combine(directory, "anchors.bson"));
        await DumpAsync(database.GetCollection<RawBsonDocument>(options.AnchorLeavesCollectionName), Builders<RawBsonDocument>.Filter.Empty,
                        Builders<RawBsonDocument>.Sort.Ascending("AnchorSequence").Ascending("Index"), Path.Combine(directory, "leaves.bson"));
        await DumpAsync(database.GetCollection<RawBsonDocument>(options.AnchorEvidenceCollectionName), Builders<RawBsonDocument>.Filter.Empty,
                        Builders<RawBsonDocument>.Sort.Ascending("_id"), Path.Combine(directory, "evidence.bson"));
        await File.WriteAllTextAsync(Path.Combine(directory, "tsa-root.pem"), testAuthority.Root.ExportCertificatePem() + Environment.NewLine);

        var anchors = new JsonArray();
        var documents = await verifier.Store.Anchors.Find(new BsonDocument(AnchorStore.SequenceField, new BsonDocument("$exists", true)))
                                      .Sort(new BsonDocument(AnchorStore.SequenceField, 1))
                                      .ToListAsync();
        foreach (var anchor in documents.Select(AnchorStore.ReadAnchor))
        {
            var result = await verifier.VerifyAnchorAsync(anchor);
            anchors.Add(new JsonObject
            {
                ["sequence"] = anchor.Sequence,
                ["digest"] = TamperEvidenceVectorTests.Hex(anchor.AnchorDigest),
                ["timestamps"] = new JsonArray(result.Timestamps.Select(t => (JsonNode)new JsonArray(t.IsValid, t.RevocationStatus.ToString())).ToArray())
            });
        }

        var chain = await verifier.VerifyAnchorChainAsync();
        return new JsonObject
        {
            ["trustedRoots"] = new JsonArray("tsa-root.pem"),
            ["graceHours"] = 24,
            ["chain"] = new JsonObject
            {
                ["intact"] = chain.IsIntact,
                ["firstBrokenSequence"] = chain.FirstBrokenSequence,
                ["failure"] = chain.Failure?.ToString(),
                ["anchorCount"] = chain.AnchorCount
            },
            ["anchors"] = anchors
        };
    }

    private static async Task<JsonObject> WriteStreamsAsync(LedgerStoreContext context, String directory)
    {
        var streams = new (String Name, Guid Id)[]
        {
            ("appended", AppendedStream),
            ("personal-data", PersonalDataStream),
            ("retroactive", RetroactiveStream),
            ("tampered", TamperedStream),
            ("renamed", RenamedStream)
        };
        var ids = new BsonArray(streams.Select(stream => new BsonBinaryData(stream.Id, GuidRepresentation.Standard)));
        await DumpAsync(context.MongoHelper.Database.GetCollection<RawBsonDocument>(context.Options.EventsCollectionName),
                        Builders<RawBsonDocument>.Filter.In("AggregateId", ids),
                        Builders<RawBsonDocument>.Sort.Ascending("AggregateId").Ascending("Version"),
                        Path.Combine(directory, "streams.bson"));

        var expected = new JsonArray();
        var verifier = new EventStreamVerifier<LedgerAggregate>(context.MongoHelper, context.Options);
        foreach (var (name, id) in streams)
        {
            var aggregateType = (await context.FindEventAsync(id, 1))["AggregateType"].AsString;
            expected.Add(new JsonObject
            {
                ["name"] = name,
                ["aggregateId"] = id.ToString(),
                ["genesis"] = TamperEvidenceVectorTests.Hex(EventIntegrityChain.ComputeGenesis(aggregateType, id)),
                ["result"] = TamperEvidenceVectorTests.ToJson(await verifier.VerifyStreamAsync(id))
            });
        }

        return new JsonObject
        {
            ["streams"] = expected
        };
    }
}
