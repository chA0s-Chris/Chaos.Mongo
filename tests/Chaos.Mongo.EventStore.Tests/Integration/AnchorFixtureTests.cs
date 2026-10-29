// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Chaos.Mongo.EventStore.Integrity;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using NUnit.Framework;
using System.Security.Cryptography;
using Testcontainers.MongoDb;

/// <summary>
/// Reads fixed BSON fixtures captured from the real <see cref="AnchorStore"/> and <see cref="AnchorEvidenceStore"/>
/// write paths at commit <c>7cacf8726842a01ab1d409d7bc8427c8f10a9b54</c> (the merge-base of this branch), before
/// <see cref="AnchorAuthorityFailure"/>, <see cref="AnchorLeaf"/> and <see cref="TimestampConfirmationFailure"/> were
/// converted from positional records to <c>init</c>-property records, so the fixtures are genuine pre-conversion
/// writer output, not an inference from the converted code's unchanged field mapping.
/// </summary>
/// <remarks>
/// Capture recipe: a disposable <c>git worktree</c> checked out at that commit (positional-record constructors
/// still in effect there), running a throwaway test that calls the same <see cref="AnchorStore"/>/
/// <see cref="AnchorEvidenceStore"/> write methods exercised below — <c>InsertPreparingAsync</c>, <c>CommitAsync</c>,
/// <c>AddTimestampAsync</c>, <c>RecordTimestampConfirmationFailureAsync</c>, <c>SetAuthorityFailuresAsync</c>,
/// <c>CompleteAsync</c>, <c>InsertLeavesAsync</c>, <c>AnchorEvidenceStore.StoreAsync</c> — with the exact field values
/// used by <see cref="BuildExpectedAnchor"/>/<see cref="BuildExpectedLeaf"/>/<see cref="BuildExpectedTimestamp"/>/
/// <see cref="BuildExpectedAuthorityFailure"/> below, then freezing the bytes MongoDB stored. Re-running that capture
/// against the current (converted) code reproduces byte-identical <c>anchor-with-failures.bson</c> and
/// <c>evidence-blob.bson</c> fixtures (both documents pin their own deterministic <c>_id</c>), confirming the
/// unchanged field mapping; <c>leaf.bson</c> differs only in its MongoDB-assigned <c>_id</c> (an
/// <see cref="MongoDB.Bson.ObjectId"/>, nondeterministic per insert and never read by <see cref="AnchorStore.ReadLeaf"/>)
/// —
/// every field <see cref="AnchorStore.ReadLeaf"/> actually decodes is identical.
/// </remarks>
public class AnchorFixtureTests
{
    private const String AnchorFixtureFile = "anchor-with-failures.bson";

    /// <summary>
    /// The frozen content of the committed evidence fixture, stored inline since it is below the chunk size.
    /// </summary>
    private static readonly Byte[] EvidenceContent = Enumerable.Range(0, 64).Select(i => (Byte)i).ToArray();

    private const String EvidenceFixtureFile = "evidence-blob.bson";

    private static readonly Guid FixtureAnchorId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTime FixtureAuthorityFailureUtc = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FixtureCompletedUtc = new(2025, 1, 4, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FixtureConfirmationFailureUtc = new(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FixtureConfirmationRetryUtc = new(2025, 1, 3, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FixtureCreatedUtc = new(2024, 12, 31, 23, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FixturePeriodEndUtc = new(2025, 1, 1, 1, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FixturePeriodStartUtc = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FixtureTokenObtainedUtc = new(2025, 1, 1, 0, 0, 1, DateTimeKind.Utc);
    private const String LeafFixtureFile = "leaf.bson";

    private readonly List<LedgerStoreContext> _contexts = [];
    private MongoDbContainer _container;

    private static String FixtureDirectory
        => Path.Combine(TamperEvidenceVectorTests.RepositoryRoot, "tests", "Chaos.Mongo.EventStore.Tests", "Integration", "Fixtures");

    /// <summary>
    /// Not the source of the committed fixtures, which are frozen pre-conversion writer output (see the class
    /// remarks). Run with <c>UPDATE_FIXTURES=1</c> only after a deliberate future change to the stored anchor, leaf
    /// or evidence format, to capture a new baseline from the writers' then-current output, and review the diff.
    /// </summary>
    [Test]
    public async Task BuildFixtures_WhenRequested_WritesCurrentOutput()
    {
        if (Environment.GetEnvironmentVariable("UPDATE_FIXTURES") != "1")
        {
            Assert.Ignore("Set UPDATE_FIXTURES=1 to capture a new baseline after a deliberate format change; it does not reproduce the current committed fixtures.");
        }

        var context = await LedgerStoreContext.CreateAsync(_container);
        _contexts.Add(context);

        var anchorDocument = await WriteAnchorDocumentAsync(context.MongoHelper);
        File.WriteAllBytes(Path.Combine(FixtureDirectory, AnchorFixtureFile), anchorDocument.ToBson());

        var leafDocument = await WriteLeafDocumentAsync(context.MongoHelper);
        File.WriteAllBytes(Path.Combine(FixtureDirectory, LeafFixtureFile), leafDocument.ToBson());

        var evidenceDocument = await WriteEvidenceDocumentAsync(context.MongoHelper);
        File.WriteAllBytes(Path.Combine(FixtureDirectory, EvidenceFixtureFile), evidenceDocument.ToBson());
    }

    [OneTimeSetUp]
    public async Task GetMongoDbContainer() => _container = await MongoDbTestContainer.StartContainerAsync();

    [Test]
    public void ReadAnchor_FixtureWithAuthorityFailureAndConfirmationFailure_DecodesStoredValues()
    {
        var bytes = File.ReadAllBytes(Path.Combine(FixtureDirectory, AnchorFixtureFile));
        var document = BsonSerializer.Deserialize<BsonDocument>(bytes);

        var anchor = AnchorStore.ReadAnchor(document);

        anchor.Should().BeEquivalentTo(BuildExpectedAnchor());
    }

    [Test]
    public async Task ReadAsync_EvidenceBlobFixture_DecodesStoredBytes()
    {
        var context = await LedgerStoreContext.CreateAsync(_container);
        _contexts.Add(context);
        var store = new AnchorEvidenceStore(context.MongoHelper, new IntegrityAnchoringOptions());
        var bytes = File.ReadAllBytes(Path.Combine(FixtureDirectory, EvidenceFixtureFile));
        var document = BsonSerializer.Deserialize<BsonDocument>(bytes);
        await store.Evidence.InsertOneAsync(document);

        var read = await store.ReadAsync(SHA256.HashData(EvidenceContent), CancellationToken.None);

        read.Should().Equal(EvidenceContent);
    }

    [Test]
    public void ReadLeaf_Fixture_DecodesStoredValues()
    {
        var bytes = File.ReadAllBytes(Path.Combine(FixtureDirectory, LeafFixtureFile));
        var document = BsonSerializer.Deserialize<BsonDocument>(bytes);

        var leaf = AnchorStore.ReadLeaf(document);

        leaf.Should().BeEquivalentTo(BuildExpectedLeaf());
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

    private static AnchorRecord BuildExpectedAnchor()
        => new(FixtureAnchorId,
               AnchorStatus.Completed,
               3,
               AnchorEncoding.FormatVersion,
               AnchorEncoding.HashAlgorithmName,
               new Byte[32],
               Enumerable.Repeat((Byte)7, 32).ToArray(),
               Enumerable.Repeat((Byte)9, 32).ToArray(),
               1,
               FixturePeriodStartUtc,
               FixturePeriodEndUtc,
               FixturePeriodStartUtc,
               [BuildExpectedTimestamp()],
               [BuildExpectedAuthorityFailure()],
               FixtureCreatedUtc,
               FixtureCompletedUtc);

    private static AnchorAuthorityFailure BuildExpectedAuthorityFailure()
        => new()
        {
            Authority = "test-tsa",
            FailureCount = 2,
            LastFailure = "the authority is unreachable.",
            LastFailureUtc = FixtureAuthorityFailureUtc
        };

    private static AnchorLeaf BuildExpectedLeaf()
        => new()
        {
            AnchorId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            AnchorSequence = 3,
            Index = 0,
            AggregateType = "LedgerAggregate",
            AggregateId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Version = 1,
            Hash = Enumerable.Repeat((Byte)5, 32).ToArray(),
            Proof = [Enumerable.Repeat((Byte)6, 32).ToArray()]
        };

    private static AnchorTimestampReference BuildExpectedTimestamp()
        => new("test-tsa",
               new Byte[32],
               [new Byte[32]],
               [],
               FixturePeriodStartUtc,
               FixtureTokenObtainedUtc)
        {
            ConfirmationFailure = new TimestampConfirmationFailure
            {
                FailureCount = 1,
                LastFailure = "the CRL could not be downloaded.",
                LastFailureUtc = FixtureConfirmationFailureUtc
            },
            ConfirmationNotBeforeUtc = FixtureConfirmationRetryUtc
        };

    /// <summary>
    /// Drives the anchor through its real lifecycle (prepare, commit, add a timestamp, record a confirmation
    /// failure, record an authority failure, complete) using <see cref="AnchorStore"/>'s actual write methods, then
    /// reads back the exact bytes MongoDB stored.
    /// </summary>
    private static async Task<BsonDocument> WriteAnchorDocumentAsync(IMongoHelper mongoHelper)
    {
        var store = new AnchorStore(mongoHelper, new IntegrityAnchoringOptions());
        var anchor = new AnchorRecord(FixtureAnchorId,
                                      AnchorStatus.Preparing,
                                      0,
                                      AnchorEncoding.FormatVersion,
                                      AnchorEncoding.HashAlgorithmName,
                                      new Byte[32],
                                      Enumerable.Repeat((Byte)7, 32).ToArray(),
                                      Enumerable.Repeat((Byte)9, 32).ToArray(),
                                      1,
                                      FixturePeriodStartUtc,
                                      FixturePeriodEndUtc,
                                      FixturePeriodStartUtc,
                                      [],
                                      [],
                                      FixtureCreatedUtc,
                                      null);
        await store.InsertPreparingAsync(anchor, CancellationToken.None);

        await mongoHelper.ExecuteInTransaction(async (_, session, ct) =>
        {
            var committed = await store.CommitAsync(session, FixtureAnchorId, 3, ct);
            if (!committed)
            {
                throw new InvalidOperationException("The fixture anchor could not be committed.");
            }
        });

        var expectedTimestamp = BuildExpectedTimestamp();
        var timestamp = new AnchorTimestampReference(expectedTimestamp.Authority,
                                                     expectedTimestamp.TokenHash,
                                                     expectedTimestamp.CertificateChainHashes,
                                                     expectedTimestamp.CertificateRevocationListHashes,
                                                     expectedTimestamp.TimestampUtc,
                                                     expectedTimestamp.ObtainedUtc);
        await store.AddTimestampAsync(FixtureAnchorId, timestamp, CancellationToken.None);

        var unconfirmed = new UnconfirmedTimestamp(FixtureAnchorId,
                                                   3,
                                                   timestamp.Authority,
                                                   timestamp.TokenHash,
                                                   timestamp.CertificateChainHashes,
                                                   timestamp.TimestampUtc,
                                                   null);
        await store.RecordTimestampConfirmationFailureAsync([unconfirmed],
                                                            expectedTimestamp.ConfirmationFailure!.LastFailure,
                                                            FixtureConfirmationFailureUtc,
                                                            FixtureConfirmationRetryUtc,
                                                            CancellationToken.None);

        await store.SetAuthorityFailuresAsync(FixtureAnchorId, [BuildExpectedAuthorityFailure()], CancellationToken.None);

        await store.CompleteAsync(FixtureAnchorId, FixtureCompletedUtc, CancellationToken.None);

        return await store.Anchors.Find(new BsonDocument("_id", new BsonBinaryData(FixtureAnchorId, GuidRepresentation.Standard))).SingleAsync();
    }

    /// <summary>
    /// Stores the evidence blob through <see cref="AnchorEvidenceStore.StoreAsync"/> and reads back the exact bytes
    /// MongoDB stored.
    /// </summary>
    private static async Task<BsonDocument> WriteEvidenceDocumentAsync(IMongoHelper mongoHelper)
    {
        var store = new AnchorEvidenceStore(mongoHelper, new IntegrityAnchoringOptions());
        var hash = await store.StoreAsync(EvidenceContent, CancellationToken.None);
        return await store.Evidence.Find(new BsonDocument("_id", new BsonBinaryData(hash))).SingleAsync();
    }

    /// <summary>
    /// Inserts the leaf through <see cref="AnchorStore.InsertLeavesAsync"/> and reads back the exact bytes MongoDB
    /// stored.
    /// </summary>
    private static async Task<BsonDocument> WriteLeafDocumentAsync(IMongoHelper mongoHelper)
    {
        var store = new AnchorStore(mongoHelper, new IntegrityAnchoringOptions());
        var leaf = BuildExpectedLeaf();
        await store.InsertLeavesAsync([leaf], CancellationToken.None);
        return await store.Leaves.Find(new BsonDocument(AnchorStore.AnchorIdField, new BsonBinaryData(leaf.AnchorId, GuidRepresentation.Standard)))
                          .SingleAsync();
    }
}
