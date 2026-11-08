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
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using Testcontainers.MongoDb;

/// <summary>
/// Regenerates the committed test vectors of the tamper-evidence specification from real library output. The vectors
/// are committed once; <see cref="TamperEvidenceVectorTests"/> checks that the library still agrees with them, and the
/// reference verifier checks them independently.
/// </summary>
[Explicit("Regenerates the committed test vectors under spec/tamper-evidence/v1.")]
[Category("Integration")]
public class TamperEvidenceVectorGenerator
{
    public static readonly Guid AppendedStream = Guid.Parse("0f5a9c2e-6b1d-4c3a-9e2f-1a2b3c4d5e01");
    public static readonly Guid DegradedEvidenceStream = Guid.Parse("0f5a9c2e-6b1d-4c3a-9e2f-1a2b3c4d5e08");
    public static readonly Guid EmptyStream = Guid.Parse("0f5a9c2e-6b1d-4c3a-9e2f-1a2b3c4d5e06");
    public static readonly Guid PersonalDataStream = Guid.Parse("0f5a9c2e-6b1d-4c3a-9e2f-1a2b3c4d5e02");
    public static readonly Guid RenamedStream = Guid.Parse("0f5a9c2e-6b1d-4c3a-9e2f-1a2b3c4d5e05");
    public static readonly Guid RetroactiveStream = Guid.Parse("0f5a9c2e-6b1d-4c3a-9e2f-1a2b3c4d5e03");
    public static readonly Guid SharedIdentifierStream = Guid.Parse("0f5a9c2e-6b1d-4c3a-9e2f-1a2b3c4d5e07");
    public static readonly Guid TamperedStream = Guid.Parse("0f5a9c2e-6b1d-4c3a-9e2f-1a2b3c4d5e04");
    private static readonly DateTime CreatedUtc = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
    private MongoDbContainer _container;
    private TestTimestampAuthority _testAuthority;

    [OneTimeTearDown]
    public void DisposeTestAuthority() => _testAuthority.Dispose();

    [OneTimeSetUp]
    public async Task GetMongoDbContainer()
    {
        _testAuthority = new TestTimestampAuthority();
        _container = await MongoDbTestContainer.StartContainerAsync();
    }

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
            configureMongo: mongo =>
            {
                LedgerStoreContext.AddShipmentStore(mongo);
                mongo.WithIntegrityAnchoring(anchoring => anchoring.WithTimestampAuthority(_ => testAuthority.CreateAuthority()))
                     .WithIntegrityVerification(verification => verification.WithTimestampAuthorityTrust(
                                                    "test-tsa",
                                                    trust => trust.TrustedRoots.Add(testAuthority.Root)));
            });

        await AppendAsync(context, AppendedStream, 1, 3);

        await AppendAsync(personalData, PersonalDataStream, 1, 3);
        await personalData.Events.UpdateOneAsync(EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(PersonalDataStream, 2),
                                                 Builders<BsonDocument>.Update.Set("Memo", BsonNull.Value));
        await personalData.UnsetSaltAsync(PersonalDataStream, 2, "Memo");
        await personalData.Eraser.ErasePersonalDataAsync(PersonalDataStream, new ErasureRequest("TICKET-1", [1], ["Memo"]));

        await AppendAsync(context, RetroactiveStream, 1, 2);
        await AppendAsync(unprotected, RetroactiveStream, 3, 2);

        await AppendAsync(context, TamperedStream, 1, 3);

        // Anchored only as ShipmentAggregate, so a leaf exists for the AggregateId; its LedgerAggregate-typed events
        // are appended, renamed and retroactively sealed only after every anchoring run in this method (see the
        // comment near the end), so they stay genuinely intact but never become an anchor candidate under that type.
        await context.ServiceProvider.GetRequiredService<IEventStore<ShipmentAggregate>>()
                     .AppendEventsAsync([ShipmentDispatchedEvent.Create(RenamedStream, 1)]);
        await context.Sweep.RunPassAsync();
        await RunUntilAnchoredAsync(context, nameof(ShipmentAggregate), (RenamedStream, 1));

        await RunUntilAnchoredAsync(context, (AppendedStream, 3), (PersonalDataStream, 4), (RetroactiveStream, 4), (TamperedStream, 3));
        await AppendAsync(context, AppendedStream, 4, 1);
        await RunUntilAnchoredAsync(context, (AppendedStream, 4));

        await context.Events.UpdateOneAsync(EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(TamperedStream, 2),
                                            Builders<BsonDocument>.Update.Set("Memo", "tampered"));
        await ConfirmFirstAnchorAsync(context, testAuthority);

        // Two independently anchored types sharing one AggregateId: the ledger side goes through the retroactive,
        // unprotected-then-swept path so SealingAnchorLookup resolves it, the shipment side through the ordinary
        // protected path; each must resolve only its own leaves.
        await AppendAsync(unprotected, SharedIdentifierStream, 1, 1);
        await context.Sweep.RunPassAsync();
        await context.ServiceProvider.GetRequiredService<IEventStore<ShipmentAggregate>>()
                     .AppendEventsAsync([ShipmentDispatchedEvent.Create(SharedIdentifierStream, 1)]);
        await RunUntilAnchoredAsync(context, (SharedIdentifierStream, 1));
        await RunUntilAnchoredAsync(context, nameof(ShipmentAggregate), (SharedIdentifierStream, 1));

        // The first anchor's only token is later genuinely revoked, while a second anchor still covers the stream
        // with a valid token, so the evidence degrades to that later anchor: the production entry points, not only
        // the fixture replayer, must tolerate it (plan §4).
        await AppendAndAnchorDegradedEvidenceStreamAsync(context, testAuthority);

        // Renamed and resealed only now, after every anchoring run above has already happened: AnchoringRun.RunAsync
        // sweeps up every currently sealed, unanchored candidate it finds, not only the stream a given
        // RunUntilAnchoredAsync call is waiting for, so renaming any earlier would risk a later, unrelated
        // anchoring run anchoring these renamed events too and defeating the whole point of this vector (a type
        // genuinely never anchored under its new name).
        await AppendAsync(unprotected, RenamedStream, 1, 2);
        await unprotected.Events.UpdateManyAsync(EventDocumentFields<LedgerAggregate>.ForAggregate<BsonDocument>(RenamedStream),
                                                 Builders<BsonDocument>.Update.Set("AggregateType", "RenamedLedger"));
        await context.Sweep.RunPassAsync();

        var streams = await WriteStreamsAsync(context, directory);
        var trustVectorRootFiles = await WriteTrustVectorAnchorsAsync(context, directory);
        var anchors = await WriteAnchorsAsync(context, directory, testAuthority, trustVectorRootFiles);
        anchors["eventAnchors"] = await BuildEventAnchorVectorsAsync(context);
        await File.WriteAllTextAsync(Path.Combine(directory, "streams.json"), Serialize(streams));
        await File.WriteAllTextAsync(Path.Combine(directory, "anchors.json"), Serialize(anchors));
        await File.WriteAllTextAsync(Path.Combine(directory, "merkle.json"), Serialize(TamperEvidenceVectorTests.BuildMerkleVectors()));
        await File.WriteAllTextAsync(Path.Combine(directory, "evidence-chunked.json"), Serialize(TamperEvidenceVectorTests.BuildChunkedEvidenceVector()));
    }

    /// <summary>
    /// Appends a stream anchored twice: the first anchor's only token is later genuinely revoked (a real CRL, as
    /// the revocation confirmation of an anchoring run would record), while a second anchor still covers the stream
    /// with a valid token, so the evidence degrades to the second anchor's time (plan §4). Returns the first
    /// anchor's sequence number.
    /// </summary>
    private static async Task<Int64> AppendAndAnchorDegradedEvidenceStreamAsync(LedgerStoreContext context, TestTimestampAuthority testAuthority)
    {
        await AppendAsync(context, DegradedEvidenceStream, 1, 2);
        await RunUntilAnchoredAsync(context, (DegradedEvidenceStream, 2));
        var store = context.ServiceProvider.GetRequiredService<AnchorVerifier>().Store;
        var firstLeaves = await store.ReadStreamLeavesAsync(nameof(LedgerAggregate), DegradedEvidenceStream, 1, null, CancellationToken.None);
        var firstSequence = firstLeaves.Completed[0].Anchor.Sequence;

        await AppendAsync(context, DegradedEvidenceStream, 3, 1);
        await RunUntilAnchoredAsync(context, (DegradedEvidenceStream, 3));

        var evidence = new AnchorEvidenceStore(context.MongoHelper, context.ServiceProvider.GetRequiredService<IntegrityAnchoringOptions>());
        var first = AnchorStore.ReadAnchor(await store.Anchors.Find(new BsonDocument(AnchorStore.SequenceField, firstSequence)).SingleAsync());
        var firstTimestampUtc = new DateTimeOffset(first.Timestamps[0].TimestampUtc, TimeSpan.Zero);
        var revokingCrl = testAuthority.BuildCrl(firstTimestampUtc.AddHours(25), revoked: (testAuthority.SigningCertificate.SerialNumberBytes.ToArray(), firstTimestampUtc));
        var hash = await evidence.StoreAsync(revokingCrl, CancellationToken.None);
        await store.Anchors.UpdateOneAsync(new BsonDocument("_id", new BsonBinaryData(first.Id, GuidRepresentation.Standard)),
                                           Builders<BsonDocument>.Update
                                                                 .Set("Timestamps.0.RevocationStatus", nameof(TimestampRevocationStatus.Revoked))
                                                                 .Set("Timestamps.0.ConfirmingRevocationListHashes", new BsonArray
                                                                 {
                                                                     new BsonBinaryData(hash)
                                                                 }));
        return firstSequence;
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
    /// Builds the committed vector of <see cref="EventStreamVerifier{TAggregate}.VerifyEventAnchorAsync"/>'s result for
    /// <see cref="DegradedEvidenceStream"/>'s first version, whose evidence degrades from the first, now-revoked
    /// anchor to the second, still-valid one.
    /// </summary>
    private static async Task<JsonObject> BuildEventAnchorVectorsAsync(LedgerStoreContext context)
    {
        var result = await context.CreateVerifier().VerifyEventAnchorAsync(DegradedEvidenceStream, 1);
        return new JsonObject
        {
            ["aggregateId"] = DegradedEvidenceStream.ToString(),
            ["version"] = 1,
            ["status"] = result.Status.ToString(),
            ["anchorSequence"] = result.Anchor?.Sequence,
            ["degradedFromSequence"] = result.DegradedFromAnchor?.Sequence
        };
    }

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

    /// <summary>
    /// Inserts a synthetic, already-completed anchor covering one dummy leaf, through the same public
    /// <see cref="AnchorStore"/> writes <see cref="AnchoringRun"/> uses, so its shape is identical to a real one.
    /// Used to commit shared vectors (plan §1, §5) whose point is the stored timestamp entries, not the leaves.
    /// </summary>
    /// <param name="context">The ledger store context whose anchor store the anchor is inserted into.</param>
    /// <param name="evidence">The evidence store the timestamp entries' tokens, certificates and CRLs are stored into.</param>
    /// <param name="leafAggregateId">The identifier of the anchor's one dummy leaf.</param>
    /// <param name="buildTimestamps">
    /// Builds the anchor's timestamp entries once its digest is known, since each entry's token must cover it.
    /// </param>
    private static async Task InsertSyntheticAnchorAsync(LedgerStoreContext context,
                                                         AnchorEvidenceStore evidence,
                                                         Guid leafAggregateId,
                                                         Func<Byte[], Task<IReadOnlyList<SyntheticTimestamp>>> buildTimestamps)
    {
        const String leafAggregateType = "TrustVector";
        var store = context.ServiceProvider.GetRequiredService<AnchorVerifier>().Store;
        var latest = await store.FindLatestCommittedAsync(CancellationToken.None);
        var sequence = (latest?.Sequence ?? 0) + 1;
        var previousDigest = latest?.AnchorDigest ?? new Byte[AnchorEncoding.HashLength];
        var candidate = new AnchorCandidate(leafAggregateType, leafAggregateId, 1, SHA256.HashData(leafAggregateId.ToByteArray()), default);
        var tree = MerkleTree.Build([MerkleTree.HashLeaf(AnchorEncoding.EncodeLeaf(candidate))]);
        var anchorId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var anchorDigest = AnchorEncoding.ComputeAnchorDigest(AnchorEncoding.FormatVersion, sequence, previousDigest, tree.Root);
        await store.InsertPreparingAsync(new AnchorRecord(anchorId,
                                                          AnchorStatus.Preparing,
                                                          sequence,
                                                          AnchorEncoding.FormatVersion,
                                                          AnchorEncoding.HashAlgorithmName,
                                                          previousDigest,
                                                          tree.Root,
                                                          anchorDigest,
                                                          1,
                                                          now,
                                                          now,
                                                          now,
                                                          [],
                                                          [],
                                                          now,
                                                          null),
                                         CancellationToken.None);
        await store.InsertLeavesAsync([
            new AnchorLeaf
            {
                AnchorId = anchorId,
                AnchorSequence = sequence,
                Index = 0,
                AggregateType = leafAggregateType,
                AggregateId = leafAggregateId,
                Version = 1,
                Hash = candidate.Hash,
                Proof = tree.GetInclusionProof(0)
            }
        ], CancellationToken.None);
        await context.MongoHelper.ExecuteInTransactionAsync(async (_, session, ct) =>
        {
            if (!await store.CommitAsync(session, anchorId, sequence, ct))
            {
                throw new InvalidOperationException("The synthetic trust-vector anchor could not be committed.");
            }
        }, cancellationToken: CancellationToken.None);

        foreach (var entry in await buildTimestamps(anchorDigest))
        {
            var tokenHash = await evidence.StoreAsync(entry.Token, CancellationToken.None);
            var certificateChainHashes = new List<Byte[]>();
            foreach (var certificate in entry.CertificateChain)
            {
                certificateChainHashes.Add(await evidence.StoreAsync(certificate, CancellationToken.None));
            }

            var confirmingHashes = new List<Byte[]>();
            if (entry.ConfirmingCrl is { } crl)
            {
                confirmingHashes.Add(await evidence.StoreAsync(crl, CancellationToken.None));
            }

            var added = await store.AddTimestampAsync(anchorId,
                                                      new AnchorTimestampReference(entry.Authority, tokenHash, certificateChainHashes, [], entry.TimestampUtc, now)
                                                      {
                                                          RevocationStatus = entry.RevocationStatus,
                                                          ConfirmingRevocationListHashes = confirmingHashes
                                                      },
                                                      CancellationToken.None);
            if (!added)
            {
                throw new InvalidOperationException($"The synthetic timestamp '{entry.Authority}' could not be added.");
            }
        }

        if (!await store.CompleteAsync(anchorId, now, CancellationToken.None))
        {
            throw new InvalidOperationException("The synthetic trust-vector anchor could not be completed.");
        }
    }

    private static Task RunUntilAnchoredAsync(LedgerStoreContext context, params (Guid AggregateId, Int64 Version)[] heads)
        => RunUntilAnchoredAsync(context, nameof(LedgerAggregate), heads);

    private static async Task RunUntilAnchoredAsync(LedgerStoreContext context, String aggregateType, params (Guid AggregateId, Int64 Version)[] heads)
    {
        var run = context.ServiceProvider.GetRequiredService<AnchoringRun>();
        var store = context.ServiceProvider.GetRequiredService<AnchorVerifier>().Store;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        foreach (var (aggregateId, version) in heads)
        {
            while ((await store.ReadStreamLeavesAsync(aggregateType, aggregateId, version, null, CancellationToken.None)).Completed.Count == 0)
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

    private static async Task<JsonObject> WriteAnchorsAsync(LedgerStoreContext context,
                                                            String directory,
                                                            TestTimestampAuthority testAuthority,
                                                            IReadOnlyDictionary<String, IReadOnlyList<String>> trustVectorRootFiles)
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
        var trustedRoots = new JsonObject
        {
            ["test-tsa"] = new JsonArray("tsa-root.pem")
        };
        foreach (var (authorityName, fileNames) in trustVectorRootFiles)
        {
            trustedRoots[authorityName] = new JsonArray(fileNames.Select(name => (JsonNode)name).ToArray());
        }

        return new JsonObject
        {
            ["trustedRoots"] = trustedRoots,
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
        var streams = new (String Name, Guid Id, String AggregateType)[]
        {
            ("appended", AppendedStream, nameof(LedgerAggregate)),
            ("personal-data", PersonalDataStream, nameof(LedgerAggregate)),
            ("retroactive", RetroactiveStream, nameof(LedgerAggregate)),
            ("tampered", TamperedStream, nameof(LedgerAggregate)),
            ("renamed", RenamedStream, nameof(LedgerAggregate)),
            ("renamed-other-type", RenamedStream, nameof(ShipmentAggregate)),
            ("empty", EmptyStream, nameof(LedgerAggregate)),
            ("shared-ledger", SharedIdentifierStream, nameof(LedgerAggregate)),
            ("shared-shipment", SharedIdentifierStream, nameof(ShipmentAggregate)),
            ("degraded-evidence", DegradedEvidenceStream, nameof(LedgerAggregate))
        };
        var ledgerIds = new BsonArray(streams.Where(stream => stream.AggregateType == nameof(LedgerAggregate))
                                             .Select(stream => new BsonBinaryData(stream.Id, GuidRepresentation.Standard)));
        await DumpAsync(context.MongoHelper.Database.GetCollection<RawBsonDocument>(context.Options.EventsCollectionName),
                        Builders<RawBsonDocument>.Filter.In("AggregateId", ledgerIds),
                        Builders<RawBsonDocument>.Sort.Ascending("AggregateId").Ascending("Version"),
                        Path.Combine(directory, "streams.bson"));

        var shipmentOptions = context.ServiceProvider.GetRequiredService<MongoEventStoreOptions<ShipmentAggregate>>();
        var shipmentIds = new BsonArray(streams.Where(stream => stream.AggregateType == nameof(ShipmentAggregate))
                                               .Select(stream => new BsonBinaryData(stream.Id, GuidRepresentation.Standard))
                                               .Distinct());
        await DumpAsync(context.MongoHelper.Database.GetCollection<RawBsonDocument>(shipmentOptions.EventsCollectionName),
                        Builders<RawBsonDocument>.Filter.In("AggregateId", shipmentIds),
                        Builders<RawBsonDocument>.Sort.Ascending("AggregateId").Ascending("Version"),
                        Path.Combine(directory, "shipment-events.bson"));

        var expected = new JsonArray();
        var ledgerVerifier = context.CreateVerifier();
        var shipmentVerifier = context.ServiceProvider.GetRequiredService<EventStreamVerifier<ShipmentAggregate>>();
        foreach (var (name, id, aggregateType) in streams)
        {
            var isShipment = aggregateType == nameof(ShipmentAggregate);
            // The empty stream never had an event appended, so there is no version-1 document to read a genesis
            // value from; it exercises EmptyStream instead.
            var first = id == EmptyStream
                ? null
                : isShipment
                    ? await context.FindShipmentEventAsync(id, 1)
                    : await context.FindEventAsync(id, 1);
            var result = isShipment ? await shipmentVerifier.VerifyStreamAsync(id) : await ledgerVerifier.VerifyStreamAsync(id);
            expected.Add(new JsonObject
            {
                ["name"] = name,
                ["aggregateType"] = aggregateType,
                ["aggregateId"] = id.ToString(),
                ["genesis"] = first is null ? null : TamperEvidenceVectorTests.Hex(EventIntegrityChain.ComputeGenesis(first["AggregateType"].AsString, id)),
                ["result"] = TamperEvidenceVectorTests.ToJson(result)
            });
        }

        return new JsonObject
        {
            ["streams"] = expected
        };
    }

    /// <summary>
    /// Commits the §1 revocation-status scenarios and the §5 trust-path matrix as synthetic anchors, each verified
    /// through the same production <see cref="StoredTimestampVerifier"/> path as every other anchor, not only
    /// through the lower-level helpers <see cref="TrustedChainValidationTests"/> covers directly. Each scenario uses
    /// its own named authority and trust, never merged with another scenario's: <see cref="X509Chain"/> resolves an
    /// ambiguous candidate (for example two certificates sharing a subject name or key) by what else is in its
    /// <see cref="X509ChainPolicy.CustomTrustStore"/>, so pooling every scenario's roots into one shared trust could
    /// change which genuine path a scenario's own chain resolves to. Returns, per authority name, the committed PEM
    /// file names of the trusted roots it needs.
    /// </summary>
    private static async Task<IReadOnlyDictionary<String, IReadOnlyList<String>>> WriteTrustVectorAnchorsAsync(LedgerStoreContext context, String directory)
    {
        var evidence = new AnchorEvidenceStore(context.MongoHelper, context.ServiceProvider.GetRequiredService<IntegrityAnchoringOptions>());
        var verification = context.ServiceProvider.GetRequiredService<IntegrityVerificationOptions>();
        var rootFiles = new Dictionary<String, IReadOnlyList<String>>(StringComparer.Ordinal);
        var nextLeafAggregateId = 0;

        async Task AddScenarioAsync(String authorityName,
                                    IReadOnlyList<X509Certificate2> trustedRoots,
                                    Func<Byte[], Task<SyntheticTimestamp>> buildTimestamp)
        {
            var trust = new TimestampAuthorityTrust();
            trust.TrustedRoots.AddRange(new X509Certificate2Collection(trustedRoots.ToArray()));
            verification.Authorities[authorityName] = trust;

            var fileName = $"{authorityName}.pem";
            await File.WriteAllTextAsync(Path.Combine(directory, fileName),
                                         String.Concat(trustedRoots.Select(root => root.ExportCertificatePem() + Environment.NewLine)));
            rootFiles[authorityName] = [fileName];

            // Every scenario gets its own small, dedicated leaf stream (never verified as an actual stream): the
            // point of these anchors is their stored timestamp entries, not their leaves.
            var leafAggregateId = new Guid(SHA256.HashData([.. "TrustVector"u8, (Byte)(++nextLeafAggregateId)]).AsSpan(0, 16));
            await InsertSyntheticAnchorAsync(context, evidence, leafAggregateId, async digest => [await buildTimestamp(digest)]);
        }

        async Task AddTrustPathScenarioAsync(Func<TrustPathVectorFixtures.Scenario> build)
        {
            // The scenario's certificates are built exactly once: each uses a freshly generated key, so building it
            // twice (for example once to read its name/roots ahead of the real digest and once for the token
            // itself) would record a chain and trusted roots belonging to an unrelated set of certificates than the
            // one the token actually resolves to.
            var scenario = build();
            await AddScenarioAsync(scenario.Name,
                                   scenario.TrustedRoots,
                                   digest =>
                                   {
                                       var token = TrustedChainValidationTests.CreateToken(scenario.Signer, digest, scenario.TokenTime, scenario.ExtraEmbeddedCertificates);
                                       var tokenBytes = token.AsSignedCms().Encode();
                                       var certificateChainBytes = scenario.StoredChain.Select(certificate => certificate.RawData).ToList();
                                       var tokenTime = new DateTimeOffset(token.TokenInfo.Timestamp.UtcDateTime, TimeSpan.Zero);
                                       return Task.FromResult(new SyntheticTimestamp(scenario.Name, tokenBytes, certificateChainBytes, null,
                                                                                     TimestampRevocationStatus.NoRevocationSource, tokenTime.UtcDateTime));
                                   });
        }

        // §1: a chain with no (or only partial) CRL sources must not be reported as a check that covered it, and
        // demonstrated revocation still wins over another certificate's missing coverage.
        const String noSourceAuthority = "no-revocation-source-tsa";
        var noSource = new ChainedTimestampAuthority(noSourceAuthority, subjectPrefix: "Chaos.Mongo No-Source Vector");
        await AddScenarioAsync(noSourceAuthority,
                               [noSource.Root],
                               async digest =>
                               {
                                   var token = await noSource.TimestampAsync(digest, HashAlgorithmName.SHA256, CancellationToken.None);
                                   return new SyntheticTimestamp(noSourceAuthority, token.Token, token.CertificateChain, null, TimestampRevocationStatus.NoRevocationSource,
                                                                 token.TimestampUtc);
                               });

        // The signer's own distribution point is backed by a genuine, confirming CRL that covers it, while the
        // intermediate's remains structurally absent: a CRL that actually covers the one existing source, rather
        // than none at all, is what distinguishes this partial-coverage rule from the intermediate's source simply
        // never being checked — a regression that mis-computed "every certificate has a source" would recompute
        // Confirmed here instead of Unconfirmed, which an always-absent CRL could not detect. The source defaults
        // to naming the certificate's own issuer (TimestampTokenValidation.GetCrlSources), so the signer's source
        // names the intermediate — exactly who BuildIntermediateCrl signs with below, unlike naming the
        // intermediate's own distribution point, whose source would name the root instead.
        const String partialSourceAuthority = "partial-source-tsa";
        var partialSource = new ChainedTimestampAuthority(partialSourceAuthority,
                                                          signerCrlLocations: [new Uri("http://chained-tsa.test/signer.crl")],
                                                          subjectPrefix: "Chaos.Mongo Partial-Source Vector");
        await AddScenarioAsync(partialSourceAuthority,
                               [partialSource.Root],
                               async digest =>
                               {
                                   var token = await partialSource.TimestampAsync(digest, HashAlgorithmName.SHA256, CancellationToken.None);
                                   var tokenTime = new DateTimeOffset(token.TimestampUtc, TimeSpan.Zero);
                                   var crl = partialSource.BuildIntermediateCrl(tokenTime.AddHours(25));
                                   return new SyntheticTimestamp(partialSourceAuthority, token.Token, token.CertificateChain, crl, TimestampRevocationStatus.Unconfirmed,
                                                                 token.TimestampUtc);
                               });

        const String revokedMissingAuthority = "revoked-with-missing-coverage-tsa";
        var revokedMissing = new ChainedTimestampAuthority(revokedMissingAuthority,
                                                           signerCrlLocations: [new Uri("http://chained-tsa.test/signer.crl")],
                                                           subjectPrefix: "Chaos.Mongo Revoked-Missing Vector");
        await AddScenarioAsync(revokedMissingAuthority,
                               [revokedMissing.Root],
                               async digest =>
                               {
                                   var token = await revokedMissing.TimestampAsync(digest, HashAlgorithmName.SHA256, CancellationToken.None);
                                   var tokenTime = new DateTimeOffset(token.TimestampUtc, TimeSpan.Zero);
                                   var crl = revokedMissing.BuildIntermediateCrl(tokenTime.AddHours(25), revoked: (revokedMissing.Signer.SerialNumberBytes.ToArray(), tokenTime));
                                   return new SyntheticTimestamp(revokedMissingAuthority, token.Token, token.CertificateChain, crl, TimestampRevocationStatus.Revoked,
                                                                 token.TimestampUtc);
                               });

        // §5: the trust-path matrix, each scenario its own anchor and its own authority so every timestamp verifies
        // independently through the production StoredTimestampVerifier.Verify path, not only through the
        // lower-level helpers TrustedChainValidationTests covers directly.
        await AddTrustPathScenarioAsync(TrustPathVectorFixtures.BuildBridgingPathAccepted);
        await AddTrustPathScenarioAsync(TrustPathVectorFixtures.BuildSubstitutedRootRejected);
        await AddTrustPathScenarioAsync(TrustPathVectorFixtures.BuildInsertedCertificateRejected);
        await AddTrustPathScenarioAsync(TrustPathVectorFixtures.BuildValidOnlyAtTokenTimeAccepted);
        await AddTrustPathScenarioAsync(TrustPathVectorFixtures.BuildAlreadyExpiredAtTokenTimeRejected);
        await AddTrustPathScenarioAsync(TrustPathVectorFixtures.BuildPathLengthConstraintRejected);
        await AddTrustPathScenarioAsync(TrustPathVectorFixtures.BuildNameConstraintRejected);
        await AddTrustPathScenarioAsync(TrustPathVectorFixtures.BuildExtendedKeyUsageConstraintRejected);
        await AddTrustPathScenarioAsync(TrustPathVectorFixtures.BuildReorderedMultisetRejected);
        await AddTrustPathScenarioAsync(TrustPathVectorFixtures.BuildSharedKeySiblingNonUniqueRejected);
        await AddTrustPathScenarioAsync(TrustPathVectorFixtures.BuildSharedKeySiblingIneligibleAlternateAccepted);
        await AddTrustPathScenarioAsync(TrustPathVectorFixtures.BuildNonFinalSelfIssuedIntermediateAccepted);

        // §5's generalizations (items 5, 7, 8 and 9): a cross-certificate variant of the path-length case; the
        // shared-key-sibling non-uniqueness generalized to two recorded intermediates, to the token embedding both
        // siblings, and to three siblings each constraint-ineligible for a different reason; and the same-root
        // masking case. The three-sibling generalization (item 8) gives each sibling its own explicit Subject Key
        // Identifier so the signer's Authority Key Identifier names the recorded intermediate specifically; see
        // TrustPathVectorFixtures.BuildThreeSharedKeySiblingsEachConstraintIneligibleAccepted for why this is needed
        // (unlike the other generalizations here, three same-subject, same-key candidates with no such identifier
        // make the chain-building engine's own resolution of "the" chain non-deterministic across fresh keys).
        await AddTrustPathScenarioAsync(TrustPathVectorFixtures.BuildPathLengthConstraintCrossCertificateVariantRejected);
        await AddTrustPathScenarioAsync(TrustPathVectorFixtures.BuildTwoIntermediateSiblingRejected);
        await AddTrustPathScenarioAsync(TrustPathVectorFixtures.BuildBothSiblingsEmbeddedNonUniqueRejected);
        await AddTrustPathScenarioAsync(TrustPathVectorFixtures.BuildThreeSharedKeySiblingsEachConstraintIneligibleAccepted);
        await AddTrustPathScenarioAsync(TrustPathVectorFixtures.BuildSameRootSiblingNonUniqueRejected);

        return rootFiles;
    }

    /// <summary>
    /// A timestamp entry to add to a synthetic anchor: the raw token and chain, an optional confirming CRL, the
    /// revocation status the library's own rule would recompute for it, and the token's own time.
    /// </summary>
    private sealed record SyntheticTimestamp(
        String Authority,
        Byte[] Token,
        IReadOnlyList<Byte[]> CertificateChain,
        Byte[]? ConfirmingCrl,
        TimestampRevocationStatus RevocationStatus,
        DateTime TimestampUtc);
}
