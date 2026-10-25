// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Chaos.Mongo.EventStore.Integrity;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Driver;
using NUnit.Framework;
using System.Security.Cryptography;
using Testcontainers.MongoDb;

public class AnchorEvidenceStoreTests
{
    private readonly List<LedgerStoreContext> _contexts = [];
    private MongoDbContainer _container;

    [OneTimeSetUp]
    public async Task GetMongoDbContainer() => _container = await MongoDbTestContainer.StartContainerAsync();

    [Test]
    [TestCase(1_000_000)]
    [TestCase(1)]
    public async Task ReadAsync_ChunkCountNotMatchingTheLength_ThrowsAnchorEvidenceException(Int32 chunkCount)
    {
        var store = await CreateStoreAsync();
        var hash = await store.StoreAsync(RandomNumberGenerator.GetBytes(AnchorEvidenceStore.ChunkSize + 1), CancellationToken.None);
        await store.Evidence.UpdateOneAsync(new BsonDocument("_id", new BsonBinaryData(hash)),
                                            new BsonDocument("$set", new BsonDocument(AnchorEvidenceStore.ChunkCountField, chunkCount)));

        var act = () => store.ReadAsync(hash, CancellationToken.None);

        (await act.Should().ThrowAsync<AnchorEvidenceException>()).Which.Message.Should().Contain("malformed");
    }

    [Test]
    public async Task ReadAsync_ChunkCountNotPositive_ThrowsAnchorEvidenceException()
    {
        var store = await CreateStoreAsync();
        var hash = await store.StoreAsync(RandomNumberGenerator.GetBytes(AnchorEvidenceStore.ChunkSize + 1), CancellationToken.None);
        await store.Evidence.UpdateOneAsync(new BsonDocument("_id", new BsonBinaryData(hash)),
                                            new BsonDocument("$set", new BsonDocument(AnchorEvidenceStore.ChunkCountField, 0)));

        var act = () => store.ReadAsync(hash, CancellationToken.None);

        (await act.Should().ThrowAsync<AnchorEvidenceException>()).Which.Message.Should().Contain("malformed");
    }

    [Test]
    public async Task ReadAsync_ChunkMissing_ThrowsAnchorEvidenceException()
    {
        var store = await CreateStoreAsync();
        var hash = await store.StoreAsync(RandomNumberGenerator.GetBytes((2 * AnchorEvidenceStore.ChunkSize) + 1), CancellationToken.None);
        await store.Chunks.DeleteOneAsync(new BsonDocument("_id", AnchorEvidenceStore.ChunkId(hash, 1)));

        var act = () => store.ReadAsync(hash, CancellationToken.None);

        (await act.Should().ThrowAsync<AnchorEvidenceException>()).Which.Message.Should().Contain("1 of its 3 chunks are missing");
    }

    [Test]
    public async Task ReadAsync_ChunkNotMatchingTheHash_ThrowsAnchorEvidenceException()
    {
        var store = await CreateStoreAsync();
        var data = RandomNumberGenerator.GetBytes(AnchorEvidenceStore.ChunkSize + 1);
        var hash = await store.StoreAsync(data, CancellationToken.None);

        // The replacement must differ from the stored byte, or the item would still match its hash.
        await store.Chunks.UpdateOneAsync(new BsonDocument("_id", AnchorEvidenceStore.ChunkId(hash, 1)),
                                          new BsonDocument("$set", new BsonDocument(AnchorEvidenceStore.DataField, new BsonBinaryData([(Byte)(data[^1] ^ 0xFF)]))));

        var act = () => store.ReadAsync(hash, CancellationToken.None);

        (await act.Should().ThrowAsync<AnchorEvidenceException>()).Which.Hash.Should().Equal(hash);
    }

    [Test]
    public async Task ReadAsync_ChunkSizesNotMatchingTheLength_ThrowsAnchorEvidenceException()
    {
        var store = await CreateStoreAsync();
        var hash = await store.StoreAsync(RandomNumberGenerator.GetBytes(AnchorEvidenceStore.ChunkSize + 1), CancellationToken.None);
        await store.Chunks.UpdateOneAsync(new BsonDocument("_id", AnchorEvidenceStore.ChunkId(hash, 1)),
                                          new BsonDocument("$set", new BsonDocument(AnchorEvidenceStore.DataField, new BsonBinaryData([0x01, 0x02]))));

        var act = () => store.ReadAsync(hash, CancellationToken.None);

        (await act.Should().ThrowAsync<AnchorEvidenceException>()).Which.Message.Should().Contain("does not match its hash");
    }

    [Test]
    public async Task ReadAsync_InlineDataNotMatchingTheHash_ThrowsAnchorEvidenceException()
    {
        var store = await CreateStoreAsync();
        var hash = await store.StoreAsync([0x01, 0x02, 0x03], CancellationToken.None);
        await store.Evidence.UpdateOneAsync(new BsonDocument("_id", new BsonBinaryData(hash)),
                                            new BsonDocument("$set", new BsonDocument(AnchorEvidenceStore.DataField, new BsonBinaryData([0x01, 0x02, 0x04]))));

        var act = () => store.ReadAsync(hash, CancellationToken.None);

        (await act.Should().ThrowAsync<AnchorEvidenceException>()).Which.Message.Should().Contain("does not match its hash");
    }

    [Test]
    public async Task ReadAsync_InvalidReference_ThrowsArgumentException()
    {
        var store = await CreateStoreAsync();

        await FluentActions.Invoking(() => store.ReadAsync(new Byte[31], CancellationToken.None)).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Invoking(() => store.ReadAsync(null!, CancellationToken.None)).Should().ThrowAsync<ArgumentNullException>();
    }

    [Test]
    public async Task ReadAsync_MalformedEvidenceDocument_ThrowsAnchorEvidenceException()
    {
        var store = await CreateStoreAsync();
        var hash = await store.StoreAsync([0x01, 0x02, 0x03], CancellationToken.None);
        await store.Evidence.UpdateOneAsync(new BsonDocument("_id", new BsonBinaryData(hash)),
                                            new BsonDocument("$set", new BsonDocument(AnchorEvidenceStore.DataField, "not binary")));

        var act = () => store.ReadAsync(hash, CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<AnchorEvidenceException>()).Which;
        thrown.Message.Should().Contain("malformed");
        thrown.InnerException.Should().BeOfType<InvalidCastException>();
    }

    [Test]
    public async Task ReadAsync_MissingEvidence_ThrowsAnchorEvidenceException()
    {
        var store = await CreateStoreAsync();
        var hash = SHA256.HashData([0x01]);

        var act = () => store.ReadAsync(hash, CancellationToken.None);

        (await act.Should().ThrowAsync<AnchorEvidenceException>()).Which.Message.Should().Contain(Convert.ToHexString(hash)).And.Contain("missing");
    }

    [Test]
    public async Task StoreAsync_ItemBeyondTheDocumentLimit_IsChunkedAndReadByteIdentical()
    {
        var store = await CreateStoreAsync();
        var data = RandomNumberGenerator.GetBytes(17 * 1024 * 1024);

        var hash = await store.StoreAsync(data, CancellationToken.None);

        hash.Should().Equal(SHA256.HashData(data));
        (await store.Chunks.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty)).Should().Be(5);
        var document = await store.Evidence.Find(new BsonDocument("_id", new BsonBinaryData(hash))).SingleAsync();
        document.Contains(AnchorEvidenceStore.DataField).Should().BeFalse();
        document[AnchorEvidenceStore.ChunkCountField].ToInt32().Should().Be(5);
        (await store.ReadAsync(hash, CancellationToken.None)).Should().Equal(data);
    }

    [Test]
    public async Task StoreAsync_NullData_ThrowsArgumentNullException()
    {
        var store = await CreateStoreAsync();

        await FluentActions.Invoking(() => store.StoreAsync(null!, CancellationToken.None)).Should().ThrowAsync<ArgumentNullException>();
    }

    [Test]
    public async Task StoreAsync_PartialChunksIncludingTheFirst_CompletesTheItem()
    {
        var store = await CreateStoreAsync();
        var data = RandomNumberGenerator.GetBytes((2 * AnchorEvidenceStore.ChunkSize) + 1);
        var hash = SHA256.HashData(data);

        // An attempt that failed after its first chunk, and a superseded one that wrote the last.
        await InsertChunkAsync(store, data, hash, 0);
        await InsertChunkAsync(store, data, hash, 2);
        var stored = await store.StoreAsync(data, CancellationToken.None);

        stored.Should().Equal(hash);
        (await store.Chunks.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty)).Should().Be(3);
        (await store.ReadAsync(hash, CancellationToken.None)).Should().Equal(data);
    }

    [Test]
    public async Task StoreAsync_SameContentTwice_StoresOneInlineDocument()
    {
        var store = await CreateStoreAsync();
        var data = RandomNumberGenerator.GetBytes(AnchorEvidenceStore.ChunkSize);

        var first = await store.StoreAsync(data, CancellationToken.None);
        var second = await store.StoreAsync(data.ToArray(), CancellationToken.None);

        second.Should().Equal(first);
        (await store.Evidence.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty)).Should().Be(1);
        (await store.Chunks.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty)).Should().Be(0);
        (await store.ReadAsync(first, CancellationToken.None)).Should().Equal(data);
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

    private static Task InsertChunkAsync(AnchorEvidenceStore store, Byte[] data, Byte[] hash, Int32 index)
    {
        var offset = index * AnchorEvidenceStore.ChunkSize;
        return store.Chunks.InsertOneAsync(new BsonDocument
        {
            { "_id", AnchorEvidenceStore.ChunkId(hash, index) },
            { AnchorEvidenceStore.DataField, data.AsSpan(offset, Math.Min(AnchorEvidenceStore.ChunkSize, data.Length - offset)).ToArray() }
        });
    }

    private async Task<AnchorEvidenceStore> CreateStoreAsync()
    {
        var context = await LedgerStoreContext.CreateAsync(_container);
        _contexts.Add(context);
        return new AnchorEvidenceStore(context.MongoHelper, new IntegrityAnchoringOptions());
    }
}
