// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using MongoDB.Bson;
using MongoDB.Driver;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

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
    /// How long after discarding an attempt its document is kept so its leaves are still cleaned up; far beyond any
    /// lock lease.
    /// </summary>
    internal static readonly TimeSpan DiscardedCleanupWindow = TimeSpan.FromHours(24);

    internal const String SequenceField = "Sequence";
    internal const String StatusField = "Status";
    private const String AggregateIdField = "AggregateId";
    private const String AggregateTypeField = "AggregateType";
    private const String AuthorityFailuresField = "AuthorityFailures";
    private const String AuthorityField = "Authority";
    private const String CertificateChainHashesField = "CertificateChainHashes";
    private const String CertificateRevocationListHashesField = "CertificateRevocationListHashes";
    private const String CompletedField = "CompletedUtc";
    private const String ConfirmationFailureCountField = "ConfirmationFailureCount";
    private const String ConfirmationNotBeforeField = "ConfirmationNotBeforeUtc";
    private const String ConfirmingRevocationListHashesField = "ConfirmingRevocationListHashes";
    private const String DiscardedField = "DiscardedUtc";
    private const String HashField = "Hash";
    private const String IndexField = "Index";
    private const String LastConfirmationFailureField = "LastConfirmationFailure";
    private const String LastConfirmationFailureUtcField = "LastConfirmationFailureUtc";
    private const String LeavesByAnchorIndexName = "AnchorId_with_Index_Unique";
    private const String LeavesByStreamIndexName = "AggregateType_with_AggregateId_with_AnchorSequence_with_Version";
    private const String NotifiedField = "Notified";
    private const String RevocationStatusField = "RevocationStatus";
    private const String RevocationStatusUtcField = "RevocationStatusUtc";
    private const String SequenceIndexName = "Sequence_Unique";
    private const String StatusIndexName = "Status";
    private const String TimestampUtcField = "TimestampUtc";
    private const String TimestampsField = "Timestamps";
    private const String TokenHashField = "TokenHash";
    private const String UnconfirmedIndexName = "Unconfirmed_Sequence";
    private const String UnnotifiedIndexName = "Unnotified_Sequence";
    private const String VersionField = "Version";
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
    /// Appends a validated token, whose evidence is already stored, unless the authority already delivered one for the
    /// anchor.
    /// </summary>
    /// <returns><c>true</c> when the token was added.</returns>
    public async Task<Boolean> AddTimestampAsync(Guid anchorId, AnchorTimestampReference timestamp, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timestamp);
        var filter = ById(anchorId, AnchorStatus.AwaitingTimestamp);
        filter.Add($"{TimestampsField}.{AuthorityField}", new BsonDocument("$ne", timestamp.Authority));
        var result = await Anchors.UpdateOneAsync(filter, new BsonDocument("$push", new BsonDocument(TimestampsField, ToBson(timestamp))),
                                                  cancellationToken: cancellationToken);
        return result.ModifiedCount == 1;
    }

    /// <summary>
    /// Deletes the leaves of discarded attempts, including leaves a superseded run wrote after it was discarded, and
    /// removes the documents of attempts discarded more than <see cref="DiscardedCleanupWindow"/> ago. A superseded
    /// run stops writing within its lock lease, so such an attempt cannot gain leaves any more, and removing it keeps
    /// the discarded attempts from accumulating.
    /// </summary>
    /// <returns>The number of deleted leaves.</returns>
    public async Task<Int64> CleanUpDiscardedAsync(DateTime utcNow, CancellationToken cancellationToken)
    {
        var discarded = await Anchors.Find(new BsonDocument(StatusField, nameof(AnchorStatus.Discarded)))
                                     .Project(new BsonDocument
                                     {
                                         { "_id", 1 },
                                         { DiscardedField, 1 }
                                     })
                                     .ToListAsync(cancellationToken);
        if (discarded.Count == 0)
        {
            return 0;
        }

        var ids = new BsonArray(discarded.Select(document => document["_id"]));
        var result = await Leaves.DeleteManyAsync(new BsonDocument(AnchorIdField, new BsonDocument("$in", ids)), cancellationToken);

        var expiredBefore = utcNow - DiscardedCleanupWindow;
        var expired = new BsonArray(discarded.Where(document => document.TryGetValue(DiscardedField, out var discardedUtc) &&
                                                                (discardedUtc.ToUniversalTime() < expiredBefore))
                                             .Select(document => document["_id"]));
        if (expired.Count > 0)
        {
            var expiredFilter = new BsonDocument
            {
                { "_id", new BsonDocument("$in", expired) },
                { StatusField, nameof(AnchorStatus.Discarded) }
            };
            await Anchors.DeleteManyAsync(expiredFilter, cancellationToken);
        }

        return result.DeletedCount;
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
            { CompletedField, utcNow },
            { NotifiedField, false }
        });
        var result = await Anchors.UpdateOneAsync(ById(anchorId, AnchorStatus.AwaitingTimestamp), update, cancellationToken: cancellationToken);
        return result.ModifiedCount == 1;
    }

    /// <summary>
    /// Records the revocation status of unconfirmed timestamp entries together with the CRLs that confirm it.
    /// </summary>
    /// <param name="statuses">
    /// The entries and their status, <see cref="TimestampRevocationStatus.Confirmed"/> or
    /// <see cref="TimestampRevocationStatus.Revoked"/>.
    /// </param>
    /// <param name="revocationListHashes">The SHA-256 of each confirming CRL, already in the evidence store.</param>
    /// <param name="utcNow">The current time.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public Task ConfirmTimestampsAsync(IReadOnlyList<(UnconfirmedTimestamp Timestamp, TimestampRevocationStatus Status)> statuses,
                                       IReadOnlyList<Byte[]> revocationListHashes,
                                       DateTime utcNow,
                                       CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        ArgumentNullException.ThrowIfNull(revocationListHashes);
        var lists = new BsonArray(revocationListHashes.Select(hash => new BsonBinaryData(hash)));
        return UpdateUnconfirmedAsync(statuses.Select(status => (status.Timestamp, new BsonDocument("$set", new BsonDocument
                                      {
                                          { $"{TimestampsField}.$[entry].{RevocationStatusField}", status.Status.ToString() },
                                          { $"{TimestampsField}.$[entry].{RevocationStatusUtcField}", utcNow },
                                          { $"{TimestampsField}.$[entry].{ConfirmingRevocationListHashesField}", lists }
                                      }))),
                                      cancellationToken);
    }

    /// <summary>
    /// Defers the confirmation of unconfirmed timestamp entries, because the CRLs available today were issued too early.
    /// </summary>
    /// <param name="timestamps">The entries.</param>
    /// <param name="notBeforeUtc">The time before which confirmation is not attempted again.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public Task DeferTimestampConfirmationAsync(IReadOnlyList<UnconfirmedTimestamp> timestamps, DateTime notBeforeUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timestamps);
        var update = new BsonDocument("$set", new BsonDocument($"{TimestampsField}.$[entry].{ConfirmationNotBeforeField}", notBeforeUtc));
        return UpdateUnconfirmedAsync(timestamps.Select(timestamp => (timestamp, update)), cancellationToken);
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
        await Anchors.Indexes.CreateOneOrUpdateAsync(
            new CreateIndexModel<BsonDocument>(new BsonDocument(SequenceField, 1),
                                               new CreateIndexOptions<BsonDocument>
                                               {
                                                   Name = UnnotifiedIndexName,
                                                   PartialFilterExpression = new BsonDocument
                                                   {
                                                       { StatusField, nameof(AnchorStatus.Completed) },
                                                       { NotifiedField, false }
                                                   }
                                               }),
            cancellationToken: cancellationToken);
        await Anchors.Indexes.CreateOneOrUpdateAsync(
            new CreateIndexModel<BsonDocument>(new BsonDocument(SequenceField, 1),
                                               new CreateIndexOptions<BsonDocument>
                                               {
                                                   Name = UnconfirmedIndexName,
                                                   PartialFilterExpression = HasUnconfirmedTimestamp()
                                               }),
            cancellationToken: cancellationToken);
        await Leaves.Indexes.CreateOneOrUpdateAsync(
            new CreateIndexModel<BsonDocument>(new BsonDocument
                                               {
                                                   { AggregateTypeField, 1 },
                                                   { AggregateIdField, 1 },
                                                   { AnchorSequenceField, 1 },
                                                   { VersionField, 1 }
                                               },
                                               new CreateIndexOptions
                                               {
                                                   Name = LeavesByStreamIndexName
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
    /// Finds the latest time an unconfirmed timestamp entry of an authority and certificate chain is deferred to, if it
    /// lies beyond the current time, so the chain's entries that became due meanwhile can wait for it as well.
    /// </summary>
    /// <param name="authority">The name of the authority.</param>
    /// <param name="certificateChainHashes">The SHA-256 of each certificate of the chain.</param>
    /// <param name="utcNow">The current time.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The time the chain waits for, or <c>null</c> when it does not wait.</returns>
    public async Task<DateTime?> FindChainDeferralAsync(String authority,
                                                        IReadOnlyList<Byte[]> certificateChainHashes,
                                                        DateTime utcNow,
                                                        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(certificateChainHashes);
        var deferredEntry = new BsonDocument
        {
            { RevocationStatusField, nameof(TimestampRevocationStatus.Unconfirmed) },
            { AuthorityField, authority },
            { CertificateChainHashesField, new BsonArray(certificateChainHashes.Select(hash => new BsonBinaryData(hash))) },
            { ConfirmationNotBeforeField, new BsonDocument("$gt", utcNow) }
        };
        var filter = HasUnconfirmedTimestamp();
        filter.Add(TimestampsField, new BsonDocument("$elemMatch", deferredEntry));
        var pipeline = new[]
        {
            new BsonDocument("$match", filter),
            new BsonDocument("$project", new BsonDocument
            {
                { $"{TimestampsField}.{RevocationStatusField}", 1 },
                { $"{TimestampsField}.{AuthorityField}", 1 },
                { $"{TimestampsField}.{CertificateChainHashesField}", 1 },
                { $"{TimestampsField}.{ConfirmationNotBeforeField}", 1 }
            }),
            new BsonDocument("$unwind", $"${TimestampsField}"),
            new BsonDocument("$match", new BsonDocument(deferredEntry.Select(element => new BsonElement($"{TimestampsField}.{element.Name}", element.Value)))),
            new BsonDocument("$group", new BsonDocument
            {
                { "_id", BsonNull.Value },
                { ConfirmationNotBeforeField, new BsonDocument("$max", $"${TimestampsField}.{ConfirmationNotBeforeField}") }
            })
        };
        var result = await Anchors.Aggregate<BsonDocument>(pipeline, cancellationToken: cancellationToken).FirstOrDefaultAsync(cancellationToken);
        return result?[ConfirmationNotBeforeField].ToUniversalTime();
    }

    /// <summary>
    /// Finds the completed anchors whose leaf for a stream has a version at least the given one, in anchor order, with
    /// that leaf. Leaves of other anchors are ignored. Leaf and anchor documents that cannot be read, for example after
    /// tampering, are skipped; the verification of the chain of anchors reports damaged anchors.
    /// </summary>
    /// <param name="aggregateType">The aggregate type name.</param>
    /// <param name="aggregateId">The aggregate identifier.</param>
    /// <param name="minimumVersion">The lowest leaf version to include.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The leaves in completed anchors, ordered by anchor sequence.</returns>
    public async Task<IReadOnlyList<CompletedStreamLeaf>> FindCompletedStreamLeavesAsync(String aggregateType,
                                                                                         Guid aggregateId,
                                                                                         Int64 minimumVersion,
                                                                                         CancellationToken cancellationToken)
    {
        var documents = await Leaves.Find(StreamLeavesFilter(aggregateType, aggregateId, minimumVersion))
                                    .Sort(StreamLeavesSort())
                                    .ToListAsync(cancellationToken);
        var streamLeaves = new List<AnchorLeaf>(documents.Count);
        foreach (var document in documents)
        {
            if (TryRead(document, ReadLeaf, out var leaf))
            {
                streamLeaves.Add(leaf);
            }
        }

        var anchors = new Dictionary<Guid, AnchorRecord>();
        if (streamLeaves.Count > 0)
        {
            var ids = new BsonArray(streamLeaves.Select(leaf => leaf.AnchorId).Distinct().Select(ToBson));
            var anchorDocuments = await Anchors.Find(new BsonDocument("_id", new BsonDocument("$in", ids))).ToListAsync(cancellationToken);
            foreach (var anchorDocument in anchorDocuments)
            {
                if (TryRead(anchorDocument, ReadAnchor, out var anchor))
                {
                    anchors[anchor.Id] = anchor;
                }
            }
        }

        var leaves = new List<CompletedStreamLeaf>();
        foreach (var leaf in streamLeaves)
        {
            if (anchors.TryGetValue(leaf.AnchorId, out var anchor) && anchor.Status == AnchorStatus.Completed && anchor.Sequence == leaf.AnchorSequence)
            {
                leaves.Add(new CompletedStreamLeaf(leaf, anchor));
            }
        }

        return leaves;
    }

    /// <summary>
    /// Finds the completed anchor with the lowest sequence number whose completion was not yet notified.
    /// </summary>
    public async Task<AnchorRecord?> FindFirstUnnotifiedAsync(CancellationToken cancellationToken)
    {
        var document = await Anchors.Find(new BsonDocument
                                    {
                                        { StatusField, nameof(AnchorStatus.Completed) },
                                        { NotifiedField, false }
                                    })
                                    .Sort(new BsonDocument(SequenceField, 1))
                                    .Limit(1)
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
    /// Finds the time of the oldest timestamp token whose revocation status is unconfirmed, whether or not its
    /// authority is still configured.
    /// </summary>
    /// <remarks>
    /// Token times only roughly follow the anchor order: authorities' clocks may differ within their allowed skew, so a
    /// later anchor can hold an earlier token. The minimum is therefore taken over every unconfirmed entry.
    /// </remarks>
    /// <returns>The token's time, or <c>null</c> when every token is confirmed.</returns>
    public async Task<DateTime?> FindOldestUnconfirmedTimestampUtcAsync(CancellationToken cancellationToken)
    {
        var pipeline = new[]
        {
            new BsonDocument("$match", HasUnconfirmedTimestamp()),
            new BsonDocument("$project", new BsonDocument
            {
                { $"{TimestampsField}.{RevocationStatusField}", 1 },
                { $"{TimestampsField}.{TimestampUtcField}", 1 }
            }),
            new BsonDocument("$unwind", $"${TimestampsField}"),
            new BsonDocument("$match", HasUnconfirmedTimestamp()),
            new BsonDocument("$group", new BsonDocument
            {
                { "_id", BsonNull.Value },
                { TimestampUtcField, new BsonDocument("$min", $"${TimestampsField}.{TimestampUtcField}") }
            })
        };
        var result = await Anchors.Aggregate<BsonDocument>(pipeline, cancellationToken: cancellationToken).FirstOrDefaultAsync(cancellationToken);
        return result?[TimestampUtcField].ToUniversalTime();
    }

    /// <summary>
    /// Finds the leaves of a stream with at least the given version, in anchor sequence order. Leaves of discarded or
    /// uncommitted attempts are included; callers check the anchor.
    /// </summary>
    public IAsyncEnumerable<AnchorLeaf> FindStreamLeavesAsync(String aggregateType, Guid aggregateId, Int64 minimumVersion, CancellationToken cancellationToken)
        => ReadAllAsync(Leaves.Find(StreamLeavesFilter(aggregateType, aggregateId, minimumVersion)).Sort(StreamLeavesSort()),
                        ReadLeaf,
                        cancellationToken);

    /// <summary>
    /// Finds the unconfirmed timestamp entries of the given authorities whose token time lies at or before a time and
    /// whose confirmation is not deferred beyond the current time, in anchor order, without reading their CRLs.
    /// Deferred entries are left out before the anchors are bounded, so chains that wait or keep failing cannot take up
    /// the bound.
    /// </summary>
    /// <param name="dueUtc">The latest token time to include.</param>
    /// <param name="authorities">The names of the authorities.</param>
    /// <param name="utcNow">The current time.</param>
    /// <param name="maximumAnchors">The maximum number of anchors to read entries from.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The entries.</returns>
    public async Task<IReadOnlyList<UnconfirmedTimestamp>> FindUnconfirmedTimestampsAsync(DateTime dueUtc,
                                                                                          IReadOnlyCollection<String> authorities,
                                                                                          DateTime utcNow,
                                                                                          Int32 maximumAnchors,
                                                                                          CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorities);
        var filter = HasUnconfirmedTimestamp();
        filter.Add(TimestampsField, new BsonDocument("$elemMatch", new BsonDocument
        {
            { RevocationStatusField, nameof(TimestampRevocationStatus.Unconfirmed) },
            { TimestampUtcField, new BsonDocument("$lte", dueUtc) },
            { AuthorityField, new BsonDocument("$in", new BsonArray(authorities)) },
            { ConfirmationNotBeforeField, new BsonDocument("$not", new BsonDocument("$gt", utcNow)) }
        }));
        var documents = await Anchors.Find(filter)
                                     .Sort(new BsonDocument(SequenceField, 1))
                                     .Limit(maximumAnchors)
                                     .ToListAsync(cancellationToken);
        return documents.SelectMany(ReadUnconfirmedTimestamps)
                        .Where(timestamp => (timestamp.TimestampUtc <= dueUtc) &&
                                            authorities.Contains(timestamp.Authority) &&
                                            !(timestamp.ConfirmationNotBeforeUtc > utcNow))
                        .ToList();
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
    /// Marks a completed anchor as notified.
    /// </summary>
    public Task MarkNotifiedAsync(Guid anchorId, DateTime utcNow, CancellationToken cancellationToken)
    {
        var update = new BsonDocument("$set", new BsonDocument
        {
            { NotifiedField, true },
            { "NotifiedUtc", utcNow }
        });
        return Anchors.UpdateOneAsync(ById(anchorId, AnchorStatus.Completed), update, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Reads every committed anchor document, that is every anchor with a sequence number, in sequence order and
    /// without parsing them, so the verification of the chain of anchors can report damaged documents.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The anchor documents.</returns>
    public IAsyncEnumerable<BsonDocument> ReadCommittedAnchorDocumentsAsync(CancellationToken cancellationToken)
        => ReadAllAsync(Anchors.Find(new BsonDocument(SequenceField, new BsonDocument("$exists", true)))
                               .Sort(new BsonDocument(SequenceField, 1)),
                        document => document,
                        cancellationToken);

    /// <summary>
    /// Reads the leaf documents of an anchor in leaf order without parsing them, so the verification of the chain of
    /// anchors can report damaged leaves.
    /// </summary>
    /// <param name="anchorId">The anchor identifier.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The leaf documents.</returns>
    public IAsyncEnumerable<BsonDocument> ReadLeafDocumentsAsync(Guid anchorId, CancellationToken cancellationToken)
        => ReadAllAsync(Leaves.Find(new BsonDocument(AnchorIdField, ToBson(anchorId))).Sort(new BsonDocument(IndexField, 1)),
                        document => document,
                        cancellationToken);

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
    /// Counts a failed attempt to confirm the revocation status of unconfirmed timestamp entries and defers their next
    /// attempt.
    /// </summary>
    /// <param name="timestamps">The entries.</param>
    /// <param name="failure">The message of the failure.</param>
    /// <param name="utcNow">The current time.</param>
    /// <param name="retryUtc">The time before which confirmation is not attempted again.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public Task RecordTimestampConfirmationFailureAsync(IReadOnlyList<UnconfirmedTimestamp> timestamps,
                                                        String failure,
                                                        DateTime utcNow,
                                                        DateTime retryUtc,
                                                        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timestamps);
        ArgumentNullException.ThrowIfNull(failure);
        var update = new BsonDocument
        {
            { "$inc", new BsonDocument($"{TimestampsField}.$[entry].{ConfirmationFailureCountField}", 1) },
            {
                "$set", new BsonDocument
                {
                    { $"{TimestampsField}.$[entry].{LastConfirmationFailureField}", failure },
                    { $"{TimestampsField}.$[entry].{LastConfirmationFailureUtcField}", utcNow },
                    { $"{TimestampsField}.$[entry].{ConfirmationNotBeforeField}", retryUtc }
                }
            }
        };
        return UpdateUnconfirmedAsync(timestamps.Select(timestamp => (timestamp, update)), cancellationToken);
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
    /// Streams the leaves of an anchor in leaf order.
    /// </summary>
    public IAsyncEnumerable<AnchorLeaf> StreamLeavesAsync(Guid anchorId, CancellationToken cancellationToken)
        => ReadAllAsync(Leaves.Find(new BsonDocument(AnchorIdField, ToBson(anchorId))).Sort(new BsonDocument(IndexField, 1)),
                        ReadLeaf,
                        cancellationToken);

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
               ReadLeafVersion(document[VersionField]),
               document[HashField].AsByteArray,
               ReadBinaries(document["Proof"]));

    /// <summary>
    /// Reads a stored document without throwing on documents whose elements are missing or have unexpected types,
    /// including identifiers stored with another GUID representation.
    /// </summary>
    internal static Boolean TryRead<T>(BsonDocument document, Func<BsonDocument, T> read, [NotNullWhen(true)] out T? value) where T : class
    {
        try
        {
            value = read(document);
            return true;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidCastException or InvalidOperationException or FormatException or ArgumentException or
                                         OverflowException)
        {
            value = null;
            return false;
        }
    }

    private static BsonDocument ById(Guid anchorId, AnchorStatus status)
        => new()
        {
            { "_id", ToBson(anchorId) },
            { StatusField, status.ToString() }
        };

    /// <summary>
    /// Matches anchors with at least one timestamp entry whose revocation status is unconfirmed.
    /// </summary>
    private static BsonDocument HasUnconfirmedTimestamp()
        => new($"{TimestampsField}.{RevocationStatusField}", nameof(TimestampRevocationStatus.Unconfirmed));

    private static async IAsyncEnumerable<T> ReadAllAsync<T>(IFindFluent<BsonDocument, BsonDocument> find,
                                                             Func<BsonDocument, T> read,
                                                             [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var cursor = await find.ToCursorAsync(cancellationToken);
        while (await cursor.MoveNextAsync(cancellationToken))
        {
            foreach (var document in cursor.Current)
            {
                yield return read(document);
            }
        }
    }

    private static IReadOnlyList<Byte[]> ReadBinaries(BsonValue value)
        => value.AsBsonArray.Select(item => item.AsByteArray).ToList();

    private static TimestampConfirmationFailure? ReadConfirmationFailure(BsonDocument document)
        => document.GetValue(ConfirmationFailureCountField, 0).ToInt32() is var count and > 0
            ? new TimestampConfirmationFailure(count,
                                               document[LastConfirmationFailureField].AsString,
                                               document[LastConfirmationFailureUtcField].ToUniversalTime())
            : null;

    private static AnchorAuthorityFailure ReadFailure(BsonDocument document)
        => new(document[AuthorityField].AsString,
               document["FailureCount"].ToInt32(),
               document["LastFailure"].AsString,
               document["LastFailureUtc"].ToUniversalTime());

    /// <summary>
    /// Reads a leaf's version; only whole numbers from 1 are versions.
    /// </summary>
    private static Int64 ReadLeafVersion(BsonValue value)
        => StoredVersion.TryRead(value, out var version) && version >= 1
            ? version
            : throw new FormatException("A leaf version must be a whole number of at least 1.");

    private static DateTime? ReadOptionalTime(BsonDocument document, String field)
        => document.GetValue(field, BsonNull.Value).IsBsonNull ? null : document[field].ToUniversalTime();

    private static AnchorTimestampReference ReadTimestamp(BsonDocument document)
        => new(document[AuthorityField].AsString,
               document[TokenHashField].AsByteArray,
               ReadBinaries(document[CertificateChainHashesField]),
               ReadBinaries(document[CertificateRevocationListHashesField]),
               document[TimestampUtcField].ToUniversalTime(),
               document["ObtainedUtc"].ToUniversalTime())
        {
            RevocationStatus = Enum.Parse<TimestampRevocationStatus>(document[RevocationStatusField].AsString),
            RevocationStatusUtc = ReadOptionalTime(document, RevocationStatusUtcField),
            ConfirmingRevocationListHashes = ReadBinaries(document.GetValue(ConfirmingRevocationListHashesField, new BsonArray())),
            ConfirmationNotBeforeUtc = ReadOptionalTime(document, ConfirmationNotBeforeField),
            ConfirmationFailure = ReadConfirmationFailure(document)
        };

    /// <summary>
    /// Reads the unconfirmed timestamp entries of an anchor document.
    /// </summary>
    private static IEnumerable<UnconfirmedTimestamp> ReadUnconfirmedTimestamps(BsonDocument anchor)
        => anchor[TimestampsField].AsBsonArray
                                  .Select(item => item.AsBsonDocument)
                                  .Where(entry => entry[RevocationStatusField].AsString == nameof(TimestampRevocationStatus.Unconfirmed))
                                  .Select(entry => new UnconfirmedTimestamp(anchor["_id"].AsGuid,
                                                                            anchor[SequenceField].ToInt64(),
                                                                            entry[AuthorityField].AsString,
                                                                            entry[TokenHashField].AsByteArray,
                                                                            ReadBinaries(entry[CertificateChainHashesField]),
                                                                            entry[TimestampUtcField].ToUniversalTime(),
                                                                            ReadOptionalTime(entry, ConfirmationNotBeforeField)));

    private static BsonDocument StreamLeavesFilter(String aggregateType, Guid aggregateId, Int64 minimumVersion)
        => new()
        {
            { AggregateTypeField, aggregateType },
            { AggregateIdField, ToBson(aggregateId) },
            { VersionField, new BsonDocument("$gte", minimumVersion) }
        };

    private static BsonDocument StreamLeavesSort()
        => new()
        {
            { AggregateTypeField, 1 },
            { AggregateIdField, 1 },
            { AnchorSequenceField, 1 }
        };

    private static BsonBinaryData ToBson(Guid value) => new(value, GuidRepresentation.Standard);

    private static BsonDocument ToBson(AnchorAuthorityFailure failure)
        => new()
        {
            { AuthorityField, failure.Authority },
            { "FailureCount", failure.FailureCount },
            { "LastFailure", failure.LastFailure },
            { "LastFailureUtc", failure.LastFailureUtc }
        };

    private static BsonDocument ToBson(AnchorTimestampReference timestamp)
        => new()
        {
            { AuthorityField, timestamp.Authority },
            { TokenHashField, timestamp.TokenHash },
            { CertificateChainHashesField, new BsonArray(timestamp.CertificateChainHashes.Select(hash => new BsonBinaryData(hash))) },
            { CertificateRevocationListHashesField, new BsonArray(timestamp.CertificateRevocationListHashes.Select(hash => new BsonBinaryData(hash))) },
            { TimestampUtcField, timestamp.TimestampUtc },
            { "ObtainedUtc", timestamp.ObtainedUtc },
            { RevocationStatusField, timestamp.RevocationStatus.ToString() },
            { ConfirmingRevocationListHashesField, new BsonArray(timestamp.ConfirmingRevocationListHashes.Select(hash => new BsonBinaryData(hash))) }
        };

    private static BsonDocument ToBson(AnchorLeaf leaf)
        => new()
        {
            { AnchorIdField, ToBson(leaf.AnchorId) },
            { AnchorSequenceField, leaf.AnchorSequence },
            { IndexField, leaf.Index },
            { AggregateTypeField, leaf.AggregateType },
            { AggregateIdField, ToBson(leaf.AggregateId) },
            { VersionField, leaf.Version },
            { HashField, leaf.Hash },
            { "Proof", new BsonArray(leaf.Proof.Select(hash => new BsonBinaryData(hash))) }
        };

    /// <summary>
    /// Updates timestamp entries that are still unconfirmed, each identified by its authority and token, so an
    /// update applies at most once.
    /// </summary>
    private async Task UpdateUnconfirmedAsync(IEnumerable<(UnconfirmedTimestamp Timestamp, BsonDocument Update)> updates, CancellationToken cancellationToken)
    {
        var models = updates.Select(update => new UpdateOneModel<BsonDocument>(new BsonDocument("_id", ToBson(update.Timestamp.AnchorId)), update.Update)
                            {
                                ArrayFilters =
                                [
                                    new BsonDocumentArrayFilterDefinition<BsonDocument>(new BsonDocument
                                    {
                                        { $"entry.{AuthorityField}", update.Timestamp.Authority },
                                        { $"entry.{TokenHashField}", update.Timestamp.TokenHash },
                                        { $"entry.{RevocationStatusField}", nameof(TimestampRevocationStatus.Unconfirmed) }
                                    })
                                ]
                            })
                            .ToList();
        if (models.Count > 0)
        {
            await Anchors.BulkWriteAsync(models,
                                         new BulkWriteOptions
                                         {
                                             IsOrdered = false
                                         },
                                         cancellationToken);
        }
    }
}
