// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Chaos.Mongo.EventStore.Integrity;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using NUnit.Framework;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Testcontainers.MongoDb;

/// <summary>
/// Checks that the library still agrees with the committed test vectors of the tamper-evidence specification: it
/// reproduces every deterministic vector and validates the sample tokens, so the library and the specification cannot
/// drift apart.
/// </summary>
public class TamperEvidenceVectorTests
{
    private const Int32 ChunkedIncrement = 7;
    private const Int32 ChunkedLength = (2 * AnchorEvidenceStore.ChunkSize) + 123;

    /// <summary>
    /// A prime below 256, so the pattern does not repeat at chunk boundaries and every chunk differs.
    /// </summary>
    private const Int32 ChunkedModulus = 251;

    private const Int32 ChunkedMultiplier = 31;
    private readonly List<LedgerStoreContext> _contexts = [];
    private MongoDbContainer _container;

    /// <summary>
    /// Gets the repository root, the first ancestor of the test directory that holds the solution.
    /// </summary>
    public static String RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Chaos.Mongo.slnx")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
        }
    }

    /// <summary>
    /// Gets the directory of the committed test vectors.
    /// </summary>
    public static String VectorDirectory => Path.Combine(RepositoryRoot, "spec", "tamper-evidence", "v1");

    /// <summary>
    /// Builds the deterministic chunked evidence vector: an item longer than two chunks whose byte <c>i</c> is
    /// <c>(i * 31 + 7) mod 251</c>, with its hash and the hashes of its chunks.
    /// </summary>
    public static JsonObject BuildChunkedEvidenceVector()
    {
        var item = ChunkedItem();
        var chunkHashes = new JsonArray();
        for (var offset = 0; offset < item.Length; offset += AnchorEvidenceStore.ChunkSize)
        {
            chunkHashes.Add(Hex(SHA256.HashData(item.AsSpan(offset, Math.Min(AnchorEvidenceStore.ChunkSize, item.Length - offset)))));
        }

        return new JsonObject
        {
            ["length"] = ChunkedLength,
            ["multiplier"] = ChunkedMultiplier,
            ["increment"] = ChunkedIncrement,
            ["modulus"] = ChunkedModulus,
            ["chunkSize"] = AnchorEvidenceStore.ChunkSize,
            ["hash"] = Hex(SHA256.HashData(item)),
            ["chunkHashes"] = chunkHashes
        };
    }

    /// <summary>
    /// Builds the Merkle vectors: trees of one to seven leaves over deterministic stream heads, sorted by their encoded
    /// bytes as anchors sort them, with their roots and inclusion proofs.
    /// </summary>
    public static JsonObject BuildMerkleVectors()
    {
        var trees = new JsonArray();
        for (var size = 1; size <= 7; size++)
        {
            var candidates = Enumerable.Range(0, size)
                                       .Select(index => new AnchorCandidate("LedgerAggregate",
                                                                            new Guid(SHA256.HashData([(Byte)index, (Byte)size]).AsSpan(0, 16)),
                                                                            index + 1,
                                                                            SHA256.HashData([(Byte)index]),
                                                                            default))
                                       .Select(candidate => (Candidate: candidate, Encoded: AnchorEncoding.EncodeLeaf(candidate)))
                                       .OrderBy(leaf => Convert.ToHexString(leaf.Encoded), StringComparer.Ordinal)
                                       .ToList();
            var tree = MerkleTree.Build(candidates.Select(leaf => MerkleTree.HashLeaf(leaf.Encoded)).ToList());
            trees.Add(new JsonObject
            {
                ["leaves"] = new JsonArray(candidates.Select(leaf => (JsonNode)Hex(leaf.Encoded)).ToArray()),
                ["root"] = Hex(tree.Root),
                ["proofs"] = new JsonArray(Enumerable.Range(0, size)
                                                     .Select(index => (JsonNode)new JsonArray(tree.GetInclusionProof(index)
                                                                                                  .Select(hash => (JsonNode)Hex(hash))
                                                                                                  .ToArray()))
                                                     .ToArray()),
                ["candidates"] = new JsonArray(candidates.Select(leaf => (JsonNode)new JsonObject
                                                         {
                                                             ["aggregateType"] = leaf.Candidate.AggregateType,
                                                             ["aggregateId"] = leaf.Candidate.AggregateId.ToString(),
                                                             ["version"] = leaf.Candidate.Version,
                                                             ["hash"] = Hex(leaf.Candidate.Hash),
                                                             ["encoded"] = Hex(leaf.Encoded)
                                                         })
                                                         .ToArray())
            });
        }

        return new JsonObject
        {
            ["trees"] = trees
        };
    }

    /// <summary>
    /// Encodes bytes as lower-case hexadecimal, the encoding of the vector files.
    /// </summary>
    public static String Hex(ReadOnlySpan<Byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    [Test]
    public void BuildMerkleVectors_LibraryTrees_MatchCommittedVectors()
        => JsonNode.DeepEquals(ReadJson("merkle.json"), BuildMerkleVectors()).Should().BeTrue();

    [OneTimeSetUp]
    public async Task GetMongoDbContainer() => _container = await MongoDbTestContainer.StartContainerAsync();

    [Test]
    public async Task StoreAsync_ChunkedEvidenceVector_ReproducesCommittedHashesAndChunks()
    {
        var context = await CreateContextAsync();
        var store = new AnchorEvidenceStore(context.MongoHelper, new IntegrityAnchoringOptions());
        var expected = ReadJson("evidence-chunked.json");
        var item = ChunkedItem();

        var hash = await store.StoreAsync(item, CancellationToken.None);
        var read = await store.ReadAsync(hash, CancellationToken.None);
        var chunks = await store.Chunks.Find(new BsonDocument($"_id.{AnchorEvidenceStore.HashField}", new BsonBinaryData(hash)))
                                .Sort(new BsonDocument($"_id.{AnchorEvidenceStore.IndexField}", 1))
                                .ToListAsync();

        JsonNode.DeepEquals(expected, BuildChunkedEvidenceVector()).Should().BeTrue();
        Hex(hash).Should().Be(expected["hash"]?.GetValue<String>());
        read.Should().Equal(item);
        chunks.Select(chunk => Hex(SHA256.HashData(chunk[AnchorEvidenceStore.DataField].AsByteArray)))
              .Should().Equal((expected["chunkHashes"]?.AsArray() ?? []).Select(node => node?.GetValue<String>()));
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
    public async Task VerifyAnchorAsync_AnchorVectors_MatchExpectedDigestsAndTokens()
    {
        var root = X509Certificate2.CreateFromPem(await File.ReadAllTextAsync(Path.Combine(VectorDirectory, "tsa-root.pem")));
        var context = await CreateContextAsync(mongo => mongo.WithIntegrityVerification(verification => verification.WithTimestampAuthorityTrust(
                                                                                            "test-tsa",
                                                                                            trust => trust.TrustedRoots.Add(root))));
        var options = context.ServiceProvider.GetRequiredService<IntegrityAnchoringOptions>();
        await LoadAsync(context, options.AnchorsCollectionName, "anchors.bson");
        await LoadAsync(context, options.AnchorLeavesCollectionName, "leaves.bson");
        await LoadAsync(context, options.AnchorEvidenceCollectionName, "evidence.bson");
        var verifier = context.ServiceProvider.GetRequiredService<AnchorVerifier>();
        var expected = ReadJson("anchors.json");

        var chain = await verifier.VerifyAnchorChainAsync();
        var anchors = new JsonArray();
        foreach (var document in await verifier.Store.Anchors.Find(FilterDefinition<BsonDocument>.Empty).Sort(new BsonDocument(AnchorStore.SequenceField, 1)).ToListAsync())
        {
            var anchor = AnchorStore.ReadAnchor(document);
            var result = await verifier.VerifyAnchorAsync(anchor);
            AnchorEncoding.ComputeAnchorDigest(anchor.FormatVersion, anchor.Sequence, anchor.PreviousDigest, anchor.MerkleRoot).Should().Equal(anchor.AnchorDigest);
            anchors.Add(new JsonObject
            {
                ["sequence"] = anchor.Sequence,
                ["digest"] = Hex(anchor.AnchorDigest),
                ["timestamps"] = new JsonArray(result.Timestamps.Select(t => (JsonNode)new JsonArray(t.IsValid, t.RevocationStatus.ToString())).ToArray())
            });
        }

        chain.IsIntact.Should().BeTrue();
        chain.AnchorCount.Should().Be(expected["chain"]?["anchorCount"]?.GetValue<Int64>() ?? -1);
        JsonNode.DeepEquals(anchors, expected["anchors"]).Should().BeTrue(anchors.ToJsonString());
        foreach (var leaf in await verifier.Store.Leaves.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync())
        {
            var anchoredLeaf = AnchorStore.ReadLeaf(leaf);
            var anchor = AnchorStore.ReadAnchor(await verifier.Store.Anchors.Find(new BsonDocument("_id", leaf["AnchorId"])).SingleAsync());
            var leafHash = MerkleTree.HashLeaf(AnchorEncoding.EncodeLeaf(new AnchorCandidate(anchoredLeaf.AggregateType,
                                                                                             anchoredLeaf.AggregateId,
                                                                                             anchoredLeaf.Version,
                                                                                             anchoredLeaf.Hash,
                                                                                             default)));
            MerkleTree.VerifyInclusion(leafHash, anchoredLeaf.Index, anchor.LeafCount, anchoredLeaf.Proof, anchor.MerkleRoot).Should().BeTrue();
        }
    }

    [Test]
    public async Task VerifyStreamAsync_StreamVectors_MatchExpectedResults()
    {
        var context = await CreateContextAsync();
        await LoadAsync(context, context.Options.EventsCollectionName, "streams.bson");
        var verifier = context.CreateVerifier();

        foreach (var stream in ReadJson("streams.json")["streams"]?.AsArray() ?? [])
        {
            var aggregateId = Guid.Parse(stream?["aggregateId"]?.GetValue<String>() ?? String.Empty);
            var first = await context.FindEventAsync(aggregateId, 1);
            var result = await verifier.VerifyStreamAsync(aggregateId);

            Hex(EventIntegrityChain.ComputeGenesis(first["AggregateType"].AsString, aggregateId))
                .Should().Be(stream?["genesis"]?.GetValue<String>());
            JsonNode.DeepEquals(ToJson(result), stream?["result"]).Should().BeTrue($"{stream?["name"]}: {ToJson(result).ToJsonString()}");
        }
    }

    /// <summary>
    /// Inserts the raw documents of a vector file into a collection.
    /// </summary>
    internal static async Task LoadAsync(LedgerStoreContext context, String collectionName, String file)
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(VectorDirectory, file));
        var documents = new List<RawBsonDocument>();
        for (var offset = 0; offset < bytes.Length;)
        {
            var length = BitConverter.ToInt32(bytes, offset);
            documents.Add(new RawBsonDocument(bytes.AsSpan(offset, length).ToArray()));
            offset += length;
        }

        await context.MongoHelper.Database.GetCollection<RawBsonDocument>(collectionName).InsertManyAsync(documents);
    }

    /// <summary>
    /// Reads a JSON vector file.
    /// </summary>
    internal static JsonNode ReadJson(String file)
        => JsonNode.Parse(File.ReadAllText(Path.Combine(VectorDirectory, file))) ?? throw new InvalidDataException($"The vector file {file} is empty.");

    /// <summary>
    /// Serializes a stream verification result in the vector format the reference verifier reports.
    /// </summary>
    internal static JsonObject ToJson(StreamVerificationResult result)
        => new()
        {
            ["intact"] = result.IsIntact,
            ["firstBrokenVersion"] = result.FirstBrokenVersion,
            ["failure"] = result.Failure?.ToString(),
            ["events"] = new JsonArray(result.Events.Select(e => (JsonNode)new JsonObject
                                             {
                                                 ["version"] = e.Version,
                                                 ["hash"] = Hex(e.Hash),
                                                 ["previousHash"] = Hex(e.PreviousHash),
                                                 ["sealMode"] = e.SealMode.ToString(),
                                                 ["personalData"] = new JsonArray(e.PersonalData.Select(p => (JsonNode)new JsonObject
                                                                                   {
                                                                                       ["path"] = p.Path,
                                                                                       ["status"] = p.Status.ToString()
                                                                                   })
                                                                                   .ToArray())
                                             })
                                             .ToArray()),
            ["sealModeTransitions"] = new JsonArray(result.SealModeTransitions.Select(v => (JsonNode)v).ToArray()),
            ["unrecordedErasures"] = new JsonArray(result.UnrecordedErasures.Select(u => (JsonNode)new JsonArray(u.Version, u.Path)).ToArray())
        };

    private static Byte[] ChunkedItem()
    {
        var item = new Byte[ChunkedLength];
        for (var index = 0; index < item.Length; index++)
        {
            item[index] = (Byte)(((index * ChunkedMultiplier) + ChunkedIncrement) % ChunkedModulus);
        }

        return item;
    }

    private async Task<LedgerStoreContext> CreateContextAsync(Action<MongoBuilder>? configureMongo = null)
    {
        var context = await LedgerStoreContext.CreateAsync(_container, configureMongo: configureMongo);
        _contexts.Add(context);
        return context;
    }
}
