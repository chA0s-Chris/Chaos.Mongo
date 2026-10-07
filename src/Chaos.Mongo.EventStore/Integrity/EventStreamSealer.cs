// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using Chaos.Mongo.EventStore.Errors;
using MongoDB.Bson;
using MongoDB.Driver;

/// <summary>
/// Seals events that were stored without integrity data into their stream's hash chain.
/// </summary>
/// <remarks>
/// Sealing appends only the <c>_integrity</c> element, so the stored bytes of the event are preserved.
/// Sealed events always form a prefix of their stream, so sealing continues after the last sealed
/// version in version order. All operations run in the caller's transaction.
/// </remarks>
/// <typeparam name="TAggregate">The aggregate type.</typeparam>
internal sealed class EventStreamSealer<TAggregate> where TAggregate : class, IAggregate, new()
{
    private readonly String _aggregateTypeName = typeof(TAggregate).Name;
    private readonly IMongoHelper _mongoHelper;
    private readonly MongoEventStoreOptions<TAggregate> _options;

    public EventStreamSealer(IMongoHelper mongoHelper, MongoEventStoreOptions<TAggregate> options)
    {
        ArgumentNullException.ThrowIfNull(mongoHelper);
        ArgumentNullException.ThrowIfNull(options);
        _mongoHelper = mongoHelper;
        _options = options;
    }

    /// <summary>
    /// Seals all unsealed events of a stream before an append, as long as they fit into one chunk.
    /// </summary>
    /// <param name="session">The session of the append transaction.</param>
    /// <param name="aggregateId">The aggregate identifier.</param>
    /// <param name="predecessorVersion">The version the appended events follow.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The hash of the predecessor version.</returns>
    /// <exception cref="MongoEventStoreException">
    /// Thrown when more events than one chunk are unsealed, or when an event version is missing.
    /// </exception>
    /// <exception cref="MongoConcurrencyException">Thrown when the stream changed concurrently.</exception>
    public async Task<Byte[]> SealForAppendAsync(
        IClientSessionHandle session,
        Guid aggregateId,
        Int64 predecessorVersion,
        CancellationToken cancellationToken)
    {
        // Read one bounded window below the append and look for the sealed boundary inside it, so a long
        // unsealed stream costs at most one chunk of reads before the append fails.
        var window = await ReadAppendWindowAsync(session, aggregateId, predecessorVersion, cancellationToken);
        try
        {
            var boundary = window.FindIndex(document => document.Contains(EventIntegrityChain.ElementName));
            if (boundary < 0 && window.Count > _options.SealingChunkSize)
            {
                throw new MongoEventStoreException(
                    $"Aggregate '{aggregateId}' has more than {_options.SealingChunkSize} events that are not yet sealed. " +
                    "The stream is not yet sealed; the sealing sweep catches it up.");
            }

            var prefix = boundary < 0
                ? new SealedPrefix(0, EventIntegrityChain.ComputeGenesis(_aggregateTypeName, aggregateId))
                : ReadSealedPrefix(window[boundary], aggregateId);
            var unsealed = window.Take(boundary < 0 ? window.Count : boundary).Reverse().ToList();

            var lastHash = await SealAsync(session, aggregateId, unsealed, prefix, cancellationToken);
            if (prefix.Version + unsealed.Count != predecessorVersion)
            {
                throw new MongoConcurrencyException(
                    $"The stream of aggregate '{aggregateId}' changed concurrently while sealing it before an append.");
            }

            return lastHash;
        }
        finally
        {
            DisposeAll(window);
        }
    }

    /// <summary>
    /// Seals the next chunk of unsealed events of a stream.
    /// </summary>
    /// <param name="session">The session of the surrounding transaction.</param>
    /// <param name="aggregateId">The aggregate identifier.</param>
    /// <param name="knownSealedVersion">
    /// The last sealed version reported by the previous chunk, or <c>null</c> to determine it from the stream.
    /// </param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The outcome of the chunk.</returns>
    /// <exception cref="MongoConcurrencyException">Thrown when another writer sealed the stream concurrently.</exception>
    /// <exception cref="MongoEventStoreException">Thrown when an event version is missing.</exception>
    public async Task<SealingChunkResult> SealNextChunkAsync(
        IClientSessionHandle session,
        Guid aggregateId,
        Int64? knownSealedVersion,
        CancellationToken cancellationToken)
    {
        var prefix = await FindSealedPrefixAsync(session, aggregateId, knownSealedVersion, cancellationToken);
        var events = await ReadEventsAfterAsync(session, aggregateId, prefix.Version, _options.SealingChunkSize + 1, cancellationToken);
        try
        {
            var chunk = events.Take(_options.SealingChunkSize).ToList();
            await SealAsync(session, aggregateId, chunk, prefix, cancellationToken);
            return new SealingChunkResult(chunk.Count, prefix.Version + chunk.Count, events.Count > chunk.Count);
        }
        finally
        {
            DisposeAll(events);
        }
    }

    private static void DisposeAll(List<RawBsonDocument> documents)
    {
        foreach (var document in documents)
        {
            document.Dispose();
        }
    }

    private static SealedPrefix ReadSealedPrefix(BsonDocument document, Guid aggregateId)
    {
        var version = document[EventDocumentFields<TAggregate>.Version].ToInt64();
        if (!EventIntegrityChain.TryRead(document[EventIntegrityChain.ElementName], out var integrity))
        {
            throw new MongoEventStoreException(
                $"The integrity data of version {version} of aggregate '{aggregateId}' is malformed, so the stream cannot be sealed.");
        }

        return new SealedPrefix(version, integrity.Hash);
    }

    private async Task<SealedPrefix> FindSealedPrefixAsync(
        IClientSessionHandle session,
        Guid aggregateId,
        Int64? knownSealedVersion,
        CancellationToken cancellationToken)
    {
        var genesis = new SealedPrefix(0, EventIntegrityChain.ComputeGenesis(_aggregateTypeName, aggregateId));
        if (knownSealedVersion == 0)
        {
            return genesis;
        }

        var projection = Builders<BsonDocument>.Projection
                                               .Include(EventIntegrityChain.ElementName)
                                               .Include(EventDocumentFields<TAggregate>.Version);
        if (knownSealedVersion is { } version)
        {
            var known = await GetEventsCollection()
                              .Find(session, EventDocumentFields<TAggregate>.ForVersion<BsonDocument>(aggregateId, version))
                              .Project(projection)
                              .FirstOrDefaultAsync(cancellationToken);

            if (known is not null && known.Contains(EventIntegrityChain.ElementName))
            {
                return ReadSealedPrefix(known, aggregateId);
            }
        }

        var filter = new BsonDocument
        {
            { EventDocumentFields<TAggregate>.AggregateId, new BsonBinaryData(aggregateId, GuidRepresentation.Standard) },
            { EventIntegrityChain.ElementName, new BsonDocument("$exists", true) }
        };
        var lastSealed = await GetEventsCollection()
                               .Find(session, filter)
                               .Sort(new BsonDocument(EventDocumentFields<TAggregate>.Version, -1))
                               .Project(projection)
                               .FirstOrDefaultAsync(cancellationToken);

        return lastSealed is null
            ? genesis
            : ReadSealedPrefix(lastSealed, aggregateId);
    }

    private IMongoCollection<BsonDocument> GetEventsCollection()
        => _mongoHelper.Database.GetCollection<BsonDocument>(_options.EventsCollectionName);

    private Task<List<RawBsonDocument>> ReadAppendWindowAsync(
        IClientSessionHandle session,
        Guid aggregateId,
        Int64 predecessorVersion,
        CancellationToken cancellationToken)
    {
        var query = SealingSweepQueries<TAggregate>.AppendSealingWindow(aggregateId, predecessorVersion);
        return _mongoHelper.Database
                           .GetCollection<RawBsonDocument>(_options.EventsCollectionName)
                           .Find(session, query.Filter)
                           .Sort(query.Sort)
                           .Limit(_options.SealingChunkSize + 1)
                           .ToListAsync(cancellationToken);
    }

    private Task<List<RawBsonDocument>> ReadEventsAfterAsync(
        IClientSessionHandle session,
        Guid aggregateId,
        Int64 version,
        Int32 limit,
        CancellationToken cancellationToken)
    {
        var versionElement = EventDocumentFields<TAggregate>.Version;
        var filter = new BsonDocument
        {
            { EventDocumentFields<TAggregate>.AggregateId, new BsonBinaryData(aggregateId, GuidRepresentation.Standard) },
            { versionElement, new BsonDocument("$gt", version) }
        };

        return _mongoHelper.Database
                           .GetCollection<RawBsonDocument>(_options.EventsCollectionName)
                           .Find(session, filter)
                           .Sort(new BsonDocument(versionElement, 1))
                           .Limit(limit)
                           .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Seals the given events, which must directly follow the sealed prefix, and returns the last hash.
    /// </summary>
    private async Task<Byte[]> SealAsync(
        IClientSessionHandle session,
        Guid aggregateId,
        IReadOnlyList<RawBsonDocument> events,
        SealedPrefix prefix,
        CancellationToken cancellationToken)
    {
        var previousHash = prefix.Hash;
        var updates = new List<WriteModel<BsonDocument>>(events.Count);

        for (var index = 0; index < events.Count; index++)
        {
            var document = events[index];
            var expectedVersion = prefix.Version + index + 1;
            if (document[EventDocumentFields<TAggregate>.Version].ToInt64() != expectedVersion)
            {
                throw new MongoEventStoreException(
                    $"Version {expectedVersion} of aggregate '{aggregateId}' is missing, so the stream cannot be sealed.");
            }

            if (document.Contains(EventIntegrityChain.ElementName))
            {
                throw new MongoConcurrencyException(
                    $"Version {expectedVersion} of aggregate '{aggregateId}' was sealed concurrently.");
            }

            var integrity = EventIntegrityChain.Seal(document.ToBson(), previousHash, IntegritySealMode.Retroactive);
            var filter = new BsonDocument
            {
                { "_id", document["_id"] },
                { EventIntegrityChain.ElementName, new BsonDocument("$exists", false) }
            };
            updates.Add(new UpdateOneModel<BsonDocument>(
                            filter,
                            Builders<BsonDocument>.Update.Set(EventIntegrityChain.ElementName, integrity.ToBsonDocument())));
            previousHash = integrity.Hash;
        }

        if (updates.Count == 0)
        {
            return previousHash;
        }

        var result = await GetEventsCollection().BulkWriteAsync(session, updates, cancellationToken: cancellationToken);

        // A concurrent seal normally surfaces as a write conflict, which the driver retries. A zero
        // match is not retried by the driver, so it is reported as a concurrent modification.
        if (result.MatchedCount != updates.Count)
        {
            throw new MongoConcurrencyException(
                $"Events of aggregate '{aggregateId}' were sealed concurrently.");
        }

        return previousHash;
    }

    private sealed record SealedPrefix(Int64 Version, Byte[] Hash);
}
