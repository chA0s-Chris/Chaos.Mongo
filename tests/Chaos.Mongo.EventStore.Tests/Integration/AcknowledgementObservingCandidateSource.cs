// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Chaos.Mongo.EventStore.Integrity;
using MongoDB.Driver;

/// <summary>
/// Wraps an anchor candidate source and reports when a batch with the candidate of one aggregate has been consumed:
/// a consumer reads, acknowledges in a transaction, and only then reads again, so the first read after the batch
/// that carried the candidate proves that its acknowledgement was committed.
/// </summary>
internal sealed class AcknowledgementObservingCandidateSource : IAnchorCandidateSource
{
    private readonly TaskCompletionSource _acknowledged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Guid _aggregateId;
    private readonly IAnchorCandidateSource _inner;
    private Boolean _candidateRead;

    public AcknowledgementObservingCandidateSource(IAnchorCandidateSource inner, Guid aggregateId)
    {
        ArgumentNullException.ThrowIfNull(inner);

        _inner = inner;
        _aggregateId = aggregateId;
    }

    /// <summary>
    /// Completes once the batch with the aggregate's candidate has been acknowledged.
    /// </summary>
    public Task Acknowledged => _acknowledged.Task;

    public Task AcknowledgeAsync(IClientSessionHandle session, AnchorCandidateBatch batch, CancellationToken cancellationToken)
        => _inner.AcknowledgeAsync(session, batch, cancellationToken);

    public void DiscardUnacknowledgedReads() => _inner.DiscardUnacknowledgedReads();

    public async Task<AnchorCandidateBatch> ReadBatchAsync(Int32 maxCandidates, CancellationToken cancellationToken)
    {
        if (_candidateRead)
            _acknowledged.TrySetResult();

        var batch = await _inner.ReadBatchAsync(maxCandidates, cancellationToken);
        _candidateRead |= batch.Candidates.Any(candidate => candidate.AggregateId == _aggregateId);
        return batch;
    }

    /// <summary>
    /// Does not dispose the wrapped source, which belongs to the service provider of the test's context.
    /// </summary>
    public void Dispose() { }
}
