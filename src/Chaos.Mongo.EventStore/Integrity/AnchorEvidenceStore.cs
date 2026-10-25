// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using MongoDB.Bson;
using MongoDB.Driver;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Stores the tokens, certificates and CRLs of anchor timestamps once per distinct content, addressed by their
/// SHA-256, so anchors reference them instead of embedding them.
/// </summary>
/// <remarks>
///     <para>
///     An item up to <see cref="ChunkSize"/> bytes is stored inline in its evidence document; a larger item is split
///     into chunk documents keyed by its hash and chunk index, and its evidence document holds the chunk count.
///     </para>
///     <para>
///     The hash fixes an item's bytes and the chunk size fixes every chunk, so all writes are idempotent: a duplicate
///     key means that document's content is already stored. Chunks are written before the evidence document, so an
///     item is complete once its evidence document exists, and an existing evidence document is never rewritten.
///     Evidence is never deleted; items left by an attempt that failed before referencing them are reused by its retry.
///     </para>
/// </remarks>
internal sealed class AnchorEvidenceStore
{
    internal const String ChunkCountField = "ChunkCount";

    /// <summary>
    /// The largest item stored inline and the size of every chunk but the last; part of the storage format.
    /// </summary>
    internal const Int32 ChunkSize = 4 * 1024 * 1024;

    internal const String DataField = "Data";
    internal const String HashField = "Hash";
    internal const Int32 HashLength = 32;
    internal const String IndexField = "Index";
    internal const String LengthField = "Length";
    private readonly IMongoHelper _mongoHelper;
    private readonly IntegrityAnchoringOptions _options;

    public AnchorEvidenceStore(IMongoHelper mongoHelper, IntegrityAnchoringOptions options)
    {
        ArgumentNullException.ThrowIfNull(mongoHelper);
        ArgumentNullException.ThrowIfNull(options);
        _mongoHelper = mongoHelper;
        _options = options;
    }

    /// <summary>
    /// Gets the chunks collection.
    /// </summary>
    public IMongoCollection<BsonDocument> Chunks => _mongoHelper.Database.GetCollection<BsonDocument>(_options.AnchorEvidenceChunksCollectionName);

    /// <summary>
    /// Gets the evidence collection.
    /// </summary>
    public IMongoCollection<BsonDocument> Evidence => _mongoHelper.Database.GetCollection<BsonDocument>(_options.AnchorEvidenceCollectionName);

    /// <summary>
    /// Reads an item and checks its length and hash.
    /// </summary>
    /// <param name="hash">The SHA-256 of the item.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The item's bytes.</returns>
    /// <exception cref="AnchorEvidenceException">
    /// Thrown when the item is missing, its stored documents are malformed, or it does not match its hash.
    /// </exception>
    public async Task<Byte[]> ReadAsync(Byte[] hash, CancellationToken cancellationToken)
    {
        ValidateHash(hash);
        // Read raw, so a document that repeats an element name is reported as malformed instead of failing the read.
        var document = await Evidence.Find(new BsonDocument("_id", new BsonBinaryData(hash))).As<RawBsonDocument>().FirstOrDefaultAsync(cancellationToken) ??
                       throw new AnchorEvidenceException(hash, "it is missing.");
        var (length, inline, chunkCount) = Decode(hash, () =>
        {
            var stored = StoredDocuments.Prepare(document);
            var storedLength = StoredDocuments.ReadInt32(stored[LengthField]);
            (Int32 Length, Byte[]? Inline, Int32 ChunkCount) decoded = stored.TryGetValue(DataField, out var value)
                ? (storedLength, ReadData(value), 0)
                : (storedLength, null, StoredDocuments.ReadInt32(stored[ChunkCountField]));
            return decoded;
        });
        // A chunked item is longer than one chunk and has exactly as many chunks as its length needs, so a tampered count
        // cannot make the read unbounded.
        if (inline is null && (length <= ChunkSize || chunkCount != ((length - 1) / ChunkSize) + 1))
        {
            throw new AnchorEvidenceException(hash, "its stored document is malformed.");
        }

        var data = inline ?? await ReadChunksAsync(hash, chunkCount, length, cancellationToken);

        if ((data.Length != length) || !SHA256.HashData(data).AsSpan().SequenceEqual(hash))
        {
            throw new AnchorEvidenceException(hash, "its stored content does not match its hash.");
        }

        return data;
    }

    /// <summary>
    /// Stores an item unless it is already stored.
    /// </summary>
    /// <param name="data">The item's bytes.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The SHA-256 of the item, which references it.</returns>
    public async Task<Byte[]> StoreAsync(Byte[] data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        var hash = SHA256.HashData(data);
        var id = new BsonBinaryData(hash);
        if (await Evidence.Find(new BsonDocument("_id", id)).Project(new BsonDocument("_id", 1)).AnyAsync(cancellationToken))
        {
            return hash;
        }

        var document = new BsonDocument
        {
            { "_id", id },
            { LengthField, data.Length }
        };
        if (data.Length <= ChunkSize)
        {
            document.Add(DataField, data);
        }
        else
        {
            var chunkCount = (data.Length + ChunkSize - 1) / ChunkSize;
            for (var index = 0; index < chunkCount; index++)
            {
                // One insert per chunk, so a chunk left by a failed attempt does not stop the remaining ones.
                var offset = index * ChunkSize;
                var chunk = data.AsSpan(offset, Math.Min(ChunkSize, data.Length - offset)).ToArray();
                await InsertIfAbsentAsync(Chunks, new BsonDocument
                {
                    { "_id", ChunkId(hash, index) },
                    { DataField, chunk }
                }, cancellationToken);
            }

            document.Add(ChunkCountField, chunkCount);
        }

        await InsertIfAbsentAsync(Evidence, document, cancellationToken);
        return hash;
    }

    /// <summary>
    /// Creates the identifier of a chunk document.
    /// </summary>
    internal static BsonDocument ChunkId(Byte[] hash, Int32 index)
        => new()
        {
            { HashField, new BsonBinaryData(hash) },
            { IndexField, index }
        };

    private static T Decode<T>(Byte[] hash, Func<T> decode)
    {
        try
        {
            return decode();
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidCastException or InvalidOperationException or FormatException or OverflowException or
                                         NotSupportedException or DecoderFallbackException)
        {
            throw new AnchorEvidenceException(hash, "its stored document is malformed.", ex);
        }
    }

    private static async Task InsertIfAbsentAsync(IMongoCollection<BsonDocument> collection, BsonDocument document, CancellationToken cancellationToken)
    {
        try
        {
            await collection.InsertOneAsync(document, cancellationToken: cancellationToken);
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // The hash fixes the content, so the document is already stored as it would be written.
        }
    }

    /// <summary>
    /// Reads fields of a stored document, reporting a missing field or one of another type as malformed evidence.
    /// </summary>
    /// <summary>
    /// Reads stored item data, which is generic binary data (subtype 0x00).
    /// </summary>
    private static Byte[] ReadData(BsonValue value)
        => value is BsonBinaryData { SubType: BsonBinarySubType.Binary } data
            ? data.Bytes
            : throw new FormatException($"Stored evidence data is a {value.BsonType}, not generic binary data.");

    private static void ValidateHash(Byte[] hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        if (hash.Length != HashLength)
        {
            throw new ArgumentException($"An evidence reference is a SHA-256 hash of {HashLength} bytes.", nameof(hash));
        }
    }

    private async Task<Byte[]> ReadChunksAsync(Byte[] hash, Int32 chunkCount, Int32 length, CancellationToken cancellationToken)
    {
        if (chunkCount < 1)
        {
            throw new AnchorEvidenceException(hash, "its stored document is malformed.");
        }

        var ids = new BsonArray(Enumerable.Range(0, chunkCount).Select(index => ChunkId(hash, index)));
        var chunks = await Chunks.Find(new BsonDocument("_id", new BsonDocument("$in", ids))).As<RawBsonDocument>().ToListAsync(cancellationToken);
        if (chunks.Count != chunkCount)
        {
            throw new AnchorEvidenceException(hash, $"{chunkCount - chunks.Count} of its {chunkCount} chunks are missing.");
        }

        var ordered = Decode(hash, () => chunks.Select(StoredDocuments.Prepare)
                                               .OrderBy(chunk => StoredDocuments.ReadInt32(chunk["_id"][IndexField]))
                                               .Select(chunk => ReadData(chunk[DataField]))
                                               .ToList());
        if (ordered.Sum(chunk => (Int64)chunk.Length) != length)
        {
            throw new AnchorEvidenceException(hash, "its stored content does not match its hash.");
        }

        var data = new Byte[length];
        var offset = 0;
        foreach (var chunk in ordered)
        {
            chunk.CopyTo(data, offset);
            offset += chunk.Length;
        }

        return data;
    }
}
