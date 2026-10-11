// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Chaos.Mongo.EventStore.Integrity;
using MongoDB.Bson;
using MongoDB.Driver;

/// <summary>
/// A fake candidate source over a fixed list of candidates, for anchors larger than appending real events allows in a
/// test. It follows the read contract and writes its position in the acknowledging transaction.
/// </summary>
internal sealed class InMemoryAnchorCandidateSource : IAnchorCandidateSource
{
    private readonly IReadOnlyList<AnchorCandidate> _candidates;
    private readonly IMongoCollection<BsonDocument> _state;
    private Int32 _acknowledged;
    private AnchorCandidateBatch? _lastBatch;
    private Int32 _read;

    public InMemoryAnchorCandidateSource(IReadOnlyList<AnchorCandidate> candidates, IMongoDatabase database)
    {
        _candidates = candidates;
        _state = database.GetCollection<BsonDocument>("InMemoryCandidateState");
    }

    public static IReadOnlyList<AnchorCandidate> CreateCandidates(Int32 count)
    {
        var start = DateTime.UtcNow.AddMinutes(-10);
        return Enumerable.Range(0, count)
                         .Select(index => new AnchorCandidate(nameof(LedgerAggregate),
                                                              Guid.NewGuid(),
                                                              1,
                                                              Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray(),
                                                              start.AddMilliseconds(index)))
                         .ToList();
    }

    public async Task AcknowledgeAsync(IClientSessionHandle session, AnchorCandidateBatch batch, CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(batch, _lastBatch))
        {
            throw new ArgumentException("Only the last batch can be acknowledged.", nameof(batch));
        }

        var position = (Int32)batch.Position;
        await _state.ReplaceOneAsync(session,
                                     new BsonDocument("_id", "position"),
                                     new BsonDocument
                                     {
                                         { "_id", "position" },
                                         { "Position", position }
                                     },
                                     new ReplaceOptions
                                     {
                                         IsUpsert = true
                                     },
                                     cancellationToken);
        _acknowledged = position;
    }

    public void DiscardUnacknowledgedReads()
    {
        _lastBatch = null;
        _read = _acknowledged;
    }

    public Task<AnchorCandidateBatch> ReadBatchAsync(Int32 maxCandidates, CancellationToken cancellationToken)
    {
        var candidates = _candidates.Skip(_read).Take(maxCandidates).ToList();
        _read += candidates.Count;
        _lastBatch = new AnchorCandidateBatch(candidates, _read, _read < _candidates.Count);
        return Task.FromResult(_lastBatch);
    }

    public void Dispose() { }
}
