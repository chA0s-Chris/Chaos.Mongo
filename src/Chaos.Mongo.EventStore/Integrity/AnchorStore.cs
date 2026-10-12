// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using MongoDB.Bson;
using MongoDB.Driver;

/// <summary>
/// Reads and writes anchor and leaf documents.
/// </summary>
/// <remarks>
/// Every status transition is filtered by the expected status, and the sequence number is unique among the anchors
/// that carry one, so a run whose lock lease expired can neither commit a discarded attempt nor fork the chain.
/// </remarks>
internal sealed class AnchorStore
{
    internal const String AnchorDigestField = "AnchorDigest";
    internal const String AnchorIdField = "AnchorId";
    internal const String AnchorSequenceField = "AnchorSequence";

    /// <summary>
    /// How long after discarding an attempt its leaves are still cleaned up; far beyond any lock lease.
    /// </summary>
    internal static readonly TimeSpan DiscardedLeafCleanupWindow = TimeSpan.FromHours(24);

    internal const String SequenceField = "Sequence";
    internal const String StatusField = "Status";
    private const String AggregateIdField = "AggregateId";
    private const String AggregateTypeField = "AggregateType";
    private const String AuthorityFailuresField = "AuthorityFailures";
    private const String AuthorityField = "Authority";
    private const String CompletedField = "CompletedUtc";
    private const String DiscardedField = "DiscardedUtc";
    private const String HashField = "Hash";
    private const String IndexField = "Index";
    private const String LeavesByAnchorIndexName = "AnchorId_with_Index_Unique";
    private const String SequenceIndexName = "Sequence_Unique";
    private const String StatusIndexName = "Status";
    private const String TimestampsField = "Timestamps";

    private readonly IMongoHelper _mongoHelper;
    private readonly IntegrityAnchoringOptions _options;

    public AnchorStore(IMongoHelper mongoHelper, IntegrityAnchoringOptions options)
    {
        ArgumentNullException.ThrowIfNull(mongoHelper);
        ArgumentNullException.ThrowIfNull(options);
        _mongoHelper = mongoHelper;
        _options = options;
    }

    /// <summary>
    /// Gets the anchors collection.
    /// </summary>
    public IMongoCollection<BsonDocument> Anchors => _mongoHelper.Database.GetCollection<BsonDocument>(_options.AnchorsCollectionName);

    /// <summary>
    /// Gets the leaves collection.
    /// </summary>
    public IMongoCollection<BsonDocument> Leaves => _mongoHelper.Database.GetCollection<BsonDocument>(_options.AnchorLeavesCollectionName);

    /// <summary>
    /// Appends a validated token unless the authority already delivered one for the anchor.
    /// </summary>
    /// <returns><c>true</c> when the token was added.</returns>
    public async Task<Boolean> AddTimestampAsync(Guid anchorId, AnchorTimestamp timestamp, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timestamp);
        var filter = ById(anchorId, AnchorStatus.AwaitingTimestamp);
        filter.Add($"{TimestampsField}.{AuthorityField}", new BsonDocument("$ne", timestamp.Authority));
        var result = await Anchors.UpdateOneAsync(filter, new BsonDocument("$push", new BsonDocument(TimestampsField, ToBson(timestamp))),
                                                  cancellationToken: cancellationToken);
        return result.ModifiedCount == 1;
    }

    /// <summary>
    /// Moves a prepared anchor to <see cref="AnchorStatus.AwaitingTimestamp"/> with its sequence number, inside the
    /// transaction that acknowledges its candidates.
    /// </summary>
    /// <returns><c>false</c> when the attempt was discarded or another anchor took the sequence number.</returns>
    public async Task<Boolean> CommitAsync(IClientSessionHandle session, Guid anchorId, Int64 sequence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var update = new BsonDocument("$set", new BsonDocument
        {
            { StatusField, nameof(AnchorStatus.AwaitingTimestamp) },
            { SequenceField, sequence }
        });
        try
        {
            var result = await Anchors.UpdateOneAsync(session, ById(anchorId, AnchorStatus.Preparing), update, cancellationToken: cancellationToken);
            return result.ModifiedCount == 1;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    /// <summary>
    /// Marks an anchor awaiting its timestamps as completed.
    /// </summary>
    /// <returns><c>true</c> when the anchor was awaiting its timestamps.</returns>
    public async Task<Boolean> CompleteAsync(Guid anchorId, DateTime utcNow, CancellationToken cancellationToken)
    {
        var update = new BsonDocument("$set", new BsonDocument
        {
            { StatusField, nameof(AnchorStatus.Completed) },
            { CompletedField, utcNow }
        });
        var result = await Anchors.UpdateOneAsync(ById(anchorId, AnchorStatus.AwaitingTimestamp), update, cancellationToken: cancellationToken);
        return result.ModifiedCount == 1;
    }

    /// <summary>
    /// Deletes the leaves of recently discarded attempts, including leaves a superseded run wrote after it was
    /// discarded. A superseded run stops writing within its lock lease, so attempts discarded more than
    /// <see cref="DiscardedLeafCleanupWindow"/> ago are not examined again.
    /// </summary>
    /// <returns>The number of deleted leaves.</returns>
    public async Task<Int64> DeleteDiscardedLeavesAsync(DateTime utcNow, CancellationToken cancellationToken)
    {
        var filter = new BsonDocument
        {
            { StatusField, nameof(AnchorStatus.Discarded) },
            { DiscardedField, new BsonDocument("$gte", utcNow - DiscardedLeafCleanupWindow) }
        };
        var discarded = await Anchors.Find(filter)
                                     .Project(new BsonDocument("_id", 1))
                                     .ToListAsync(cancellationToken);
        if (discarded.Count == 0)
        {
            return 0;
        }

        var ids = new BsonArray(discarded.Select(document => document["_id"]));
        var result = await Leaves.DeleteManyAsync(new BsonDocument(AnchorIdField, new BsonDocument("$in", ids)), cancellationToken);
        return result.DeletedCount;
    }

    /// <summary>
    /// Marks every prepared attempt as discarded. Runs before a new attempt under the run lock, so a prepared anchor
    /// can only belong to a crashed or superseded attempt.
    /// </summary>
    /// <returns>The number of discarded attempts.</returns>
    public async Task<Int64> DiscardPreparingAsync(DateTime utcNow, CancellationToken cancellationToken)
    {
        var update = new BsonDocument("$set", new BsonDocument
        {
            { StatusField, nameof(AnchorStatus.Discarded) },
            { DiscardedField, utcNow }
        });
        var result = await Anchors.UpdateManyAsync(new BsonDocument(StatusField, nameof(AnchorStatus.Preparing)), update,
                                                   cancellationToken: cancellationToken);
        return result.ModifiedCount;
    }

    /// <summary>
    /// Creates the indexes of the anchors and leaves collections.
    /// </summary>
    public async Task EnsureIndexesAsync(CancellationToken cancellationToken)
    {
        await Anchors.Indexes.CreateOneOrUpdateAsync(
            new CreateIndexModel<BsonDocument>(new BsonDocument(SequenceField, 1),
                                               new CreateIndexOptions<BsonDocument>
                                               {
                                                   Name = SequenceIndexName,
                                                   Unique = true,
                                                   PartialFilterExpression = new BsonDocument(SequenceField, new BsonDocument("$exists", true))
                                               }),
            cancellationToken: cancellationToken);
        await Anchors.Indexes.CreateOneOrUpdateAsync(
            new CreateIndexModel<BsonDocument>(new BsonDocument(StatusField, 1),
                                               new CreateIndexOptions
                                               {
                                                   Name = StatusIndexName
                                               }),
            cancellationToken: cancellationToken);
        await Leaves.Indexes.CreateOneOrUpdateAsync(
            new CreateIndexModel<BsonDocument>(new BsonDocument
                                               {
                                                   { AnchorIdField, 1 },
                                                   { IndexField, 1 }
                                               },
                                               new CreateIndexOptions
                                               {
                                                   Name = LeavesByAnchorIndexName,
                                                   Unique = true
                                               }),
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Finds a committed anchor by its identifier.
    /// </summary>
    public async Task<AnchorRecord?> FindAsync(Guid anchorId, CancellationToken cancellationToken)
    {
        var filter = new BsonDocument
        {
            { "_id", ToBson(anchorId) },
            { SequenceField, new BsonDocument("$exists", true) }
        };
        var document = await Anchors.Find(filter).FirstOrDefaultAsync(cancellationToken);
        return document is null ? null : ReadAnchor(document);
    }

    /// <summary>
    /// Finds the anchor that awaits its timestamps; there is at most one, because no anchor is created while one
    /// awaits its timestamps.
    /// </summary>
    public async Task<AnchorRecord?> FindAwaitingTimestampAsync(CancellationToken cancellationToken)
    {
        var document = await Anchors.Find(new BsonDocument(StatusField, nameof(AnchorStatus.AwaitingTimestamp)))
                                    .Sort(new BsonDocument(SequenceField, 1))
                                    .FirstOrDefaultAsync(cancellationToken);
        return document is null ? null : ReadAnchor(document);
    }

    /// <summary>
    /// Finds the committed anchor with the highest sequence number.
    /// </summary>
    public async Task<AnchorRecord?> FindLatestCommittedAsync(CancellationToken cancellationToken)
    {
        var document = await Anchors.Find(new BsonDocument(SequenceField, new BsonDocument("$exists", true)))
                                    .Sort(new BsonDocument(SequenceField, -1))
                                    .Limit(1)
                                    .FirstOrDefaultAsync(cancellationToken);
        return document is null ? null : ReadAnchor(document);
    }

    /// <summary>
    /// Inserts leaves of a prepared attempt.
    /// </summary>
    public Task InsertLeavesAsync(IEnumerable<AnchorLeaf> leaves, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(leaves);
        return Leaves.InsertManyAsync(leaves.Select(ToBson), cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Inserts the anchor of a new attempt as <see cref="AnchorStatus.Preparing"/>, without a sequence number; the
    /// sequence number of the record is stored at commit.
    /// </summary>
    public Task InsertPreparingAsync(AnchorRecord anchor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        var document = new BsonDocument
        {
            { "_id", ToBson(anchor.Id) },
            { StatusField, nameof(AnchorStatus.Preparing) },
            { "FormatVersion", anchor.FormatVersion },
            { "HashAlgorithm", anchor.HashAlgorithm },
            { "PreviousDigest", anchor.PreviousDigest },
            { "MerkleRoot", anchor.MerkleRoot },
            { AnchorDigestField, anchor.AnchorDigest },
            { "LeafCount", anchor.LeafCount },
            { "PeriodStartUtc", anchor.PeriodStartUtc },
            { "PeriodEndUtc", anchor.PeriodEndUtc },
            { "OldestCandidateUtc", anchor.OldestCandidateUtc },
            { TimestampsField, new BsonArray() },
            { AuthorityFailuresField, new BsonArray() },
            { "CreatedUtc", anchor.CreatedUtc }
        };
        return Anchors.InsertOneAsync(document, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Reads the leaves of an anchor in leaf order.
    /// </summary>
    public async Task<IReadOnlyList<AnchorLeaf>> ReadLeavesAsync(Guid anchorId, CancellationToken cancellationToken)
    {
        var documents = await Leaves.Find(new BsonDocument(AnchorIdField, ToBson(anchorId)))
                                    .Sort(new BsonDocument(IndexField, 1))
                                    .ToListAsync(cancellationToken);
        return documents.Select(ReadLeaf).ToList();
    }

    /// <summary>
    /// Replaces the recorded authority failures of an anchor awaiting its timestamps.
    /// </summary>
    public Task SetAuthorityFailuresAsync(Guid anchorId, IEnumerable<AnchorAuthorityFailure> failures, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(failures);
        var update = new BsonDocument("$set", new BsonDocument(AuthorityFailuresField, new BsonArray(failures.Select(ToBson))));
        return Anchors.UpdateOneAsync(ById(anchorId, AnchorStatus.AwaitingTimestamp), update, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Reads a committed anchor document, that is one awaiting its timestamps or completed.
    /// </summary>
    internal static AnchorRecord ReadAnchor(BsonDocument document)
        => new(document["_id"].AsGuid,
               Enum.Parse<AnchorStatus>(document[StatusField].AsString),
               document[SequenceField].ToInt64(),
               document["FormatVersion"].ToInt32(),
               document["HashAlgorithm"].AsString,
               document["PreviousDigest"].AsByteArray,
               document["MerkleRoot"].AsByteArray,
               document[AnchorDigestField].AsByteArray,
               document["LeafCount"].ToInt32(),
               document["PeriodStartUtc"].ToUniversalTime(),
               document["PeriodEndUtc"].ToUniversalTime(),
               document["OldestCandidateUtc"].ToUniversalTime(),
               document.GetValue(TimestampsField, new BsonArray()).AsBsonArray.Select(item => ReadTimestamp(item.AsBsonDocument)).ToList(),
               document.GetValue(AuthorityFailuresField, new BsonArray()).AsBsonArray.Select(item => ReadFailure(item.AsBsonDocument)).ToList(),
               document["CreatedUtc"].ToUniversalTime(),
               document.GetValue(CompletedField, BsonNull.Value).IsBsonNull ? null : document[CompletedField].ToUniversalTime());

    internal static AnchorLeaf ReadLeaf(BsonDocument document)
        => new(document[AnchorIdField].AsGuid,
               document[AnchorSequenceField].ToInt64(),
               document[IndexField].ToInt32(),
               document[AggregateTypeField].AsString,
               document[AggregateIdField].AsGuid,
               document["Version"].ToInt64(),
               document[HashField].AsByteArray,
               ReadBinaries(document["Proof"]));

    private static BsonDocument ById(Guid anchorId, AnchorStatus status)
        => new()
        {
            { "_id", ToBson(anchorId) },
            { StatusField, status.ToString() }
        };

    private static IReadOnlyList<Byte[]> ReadBinaries(BsonValue value)
        => value.AsBsonArray.Select(item => item.AsByteArray).ToList();

    private static AnchorAuthorityFailure ReadFailure(BsonDocument document)
        => new(document[AuthorityField].AsString,
               document["FailureCount"].ToInt32(),
               document["LastFailure"].AsString,
               document["LastFailureUtc"].ToUniversalTime());

    private static AnchorTimestamp ReadTimestamp(BsonDocument document)
        => new(document[AuthorityField].AsString,
               document["Token"].AsByteArray,
               ReadBinaries(document["CertificateChain"]),
               ReadBinaries(document["CertificateRevocationLists"]),
               document["TimestampUtc"].ToUniversalTime(),
               document["ObtainedUtc"].ToUniversalTime());

    private static BsonBinaryData ToBson(Guid value) => new(value, GuidRepresentation.Standard);

    private static BsonDocument ToBson(AnchorAuthorityFailure failure)
        => new()
        {
            { AuthorityField, failure.Authority },
            { "FailureCount", failure.FailureCount },
            { "LastFailure", failure.LastFailure },
            { "LastFailureUtc", failure.LastFailureUtc }
        };

    private static BsonDocument ToBson(AnchorTimestamp timestamp)
        => new()
        {
            { AuthorityField, timestamp.Authority },
            { "Token", timestamp.Token },
            { "CertificateChain", new BsonArray(timestamp.CertificateChain.Select(certificate => new BsonBinaryData(certificate))) },
            { "CertificateRevocationLists", new BsonArray(timestamp.CertificateRevocationLists.Select(crl => new BsonBinaryData(crl))) },
            { "TimestampUtc", timestamp.TimestampUtc },
            { "ObtainedUtc", timestamp.ObtainedUtc }
        };

    private static BsonDocument ToBson(AnchorLeaf leaf)
        => new()
        {
            { AnchorIdField, ToBson(leaf.AnchorId) },
            { AnchorSequenceField, leaf.AnchorSequence },
            { IndexField, leaf.Index },
            { AggregateTypeField, leaf.AggregateType },
            { AggregateIdField, ToBson(leaf.AggregateId) },
            { "Version", leaf.Version },
            { HashField, leaf.Hash },
            { "Proof", new BsonArray(leaf.Proof.Select(hash => new BsonBinaryData(hash))) }
        };
}
