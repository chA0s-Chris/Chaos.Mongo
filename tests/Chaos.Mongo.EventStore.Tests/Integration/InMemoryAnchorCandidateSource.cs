// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Chaos.Mongo.EventStore.Integrity;
using MongoDB.Bson;
using MongoDB.Driver;

/// <summary>
/// A fake candidate source over a fixed list of candidates, for anchors larger than appending real events allows in a
/// test. It follows the read contract and writes its position in the acknowledging transaction. It can first serve a
/// number of batches that report more but carry no candidate, like changes that yield none.
/// </summary>
internal sealed class InMemoryAnchorCandidateSource : IAnchorCandidateSource
{
    private readonly IReadOnlyList<AnchorCandidate> _candidates;
    private readonly Int32 _emptyBatches;
    private readonly IMongoCollection<BsonDocument> _state;
    private Position _acknowledged = new(0, 0);
    private Int32 _emptyRead;
    private AnchorCandidateBatch? _lastBatch;
    private Int32 _read;

    public InMemoryAnchorCandidateSource(IReadOnlyList<AnchorCandidate> candidates, IMongoDatabase database, Int32 emptyBatches = 0)
    {
        _candidates = candidates;
        _emptyBatches = emptyBatches;
        _state = database.GetCollection<BsonDocument>("InMemoryCandidateState");
    }

    /// <summary>
    /// Gets the number of batches read so far.
    /// </summary>
    public Int32 BatchesRead { get; private set; }

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

        var position = (Position)batch.Position;
        await _state.ReplaceOneAsync(session,
                                     new BsonDocument("_id", "position"),
                                     new BsonDocument
                                     {
                                         { "_id", "position" },
                                         { "EmptyBatchesRead", position.EmptyBatchesRead },
                                         { "CandidatesRead", position.CandidatesRead }
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
        _emptyRead = _acknowledged.EmptyBatchesRead;
        _read = _acknowledged.CandidatesRead;
    }

    public Task<AnchorCandidateBatch> ReadBatchAsync(Int32 maxCandidates, CancellationToken cancellationToken)
    {
        BatchesRead++;
        if (_emptyRead < _emptyBatches)
        {
            _emptyRead++;
            _lastBatch = new AnchorCandidateBatch([], new Position(_emptyRead, _read), true);
            return Task.FromResult(_lastBatch);
        }

        var candidates = _candidates.Skip(_read).Take(maxCandidates).ToList();
        _read += candidates.Count;
        _lastBatch = new AnchorCandidateBatch(candidates, new Position(_emptyRead, _read), _read < _candidates.Count);
        return Task.FromResult(_lastBatch);
    }

    public void Dispose() { }

    private sealed record Position(Int32 EmptyBatchesRead, Int32 CandidatesRead);
}
