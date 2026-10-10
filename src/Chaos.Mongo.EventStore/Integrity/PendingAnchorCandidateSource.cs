// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using MongoDB.Bson;
using MongoDB.Driver;

/// <summary>
/// Reads anchor candidates from the pending-anchor collection that appends and retroactive sealing write in
/// their transactions (option E of #154).
/// </summary>
/// <remarks>
/// Uncommitted entries are invisible and acknowledging deletes exactly the entries that were read, so an entry
/// whose transaction commits late is returned by a later read. Unacknowledged entries stay in the collection,
/// which makes reading again, also after a restart, return them again.
/// </remarks>
internal sealed class PendingAnchorCandidateSource : IAnchorCandidateSource
{
    private readonly IMongoHelper _mongoHelper;
    private readonly IntegrityAnchoringOptions _options;

    public PendingAnchorCandidateSource(IMongoHelper mongoHelper, IntegrityAnchoringOptions options)
    {
        ArgumentNullException.ThrowIfNull(mongoHelper);
        ArgumentNullException.ThrowIfNull(options);
        _mongoHelper = mongoHelper;
        _options = options;
    }

    private IMongoCollection<BsonDocument> GetCollection()
        => _mongoHelper.Database.GetCollection<BsonDocument>(_options.PendingAnchorsCollectionName);

    /// <inheritdoc/>
    public Task AcknowledgeAsync(IClientSessionHandle session, AnchorCandidateBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(batch);

        if (batch.Position is not IReadOnlyList<BsonValue> entryIds)
        {
            throw new ArgumentException("The batch was not read from the pending-anchor collection.", nameof(batch));
        }

        return entryIds.Count == 0
            ? Task.CompletedTask
            : GetCollection().DeleteManyAsync(session, Builders<BsonDocument>.Filter.In("_id", entryIds), cancellationToken: cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<AnchorCandidateBatch> ReadBatchAsync(Int32 maxCandidates, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCandidates);

        var entries = await GetCollection()
                            .Find(FilterDefinition<BsonDocument>.Empty)
                            .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
                            .Limit(maxCandidates)
                            .ToListAsync(cancellationToken);

        return new AnchorCandidateBatch(entries.Select(PendingAnchorEntry.ToCandidate).ToList(),
                                        entries.Select(entry => entry["_id"]).ToList());
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // Reads hold no server-side state between batches.
    }
}
