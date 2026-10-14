// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// Reads anchor candidates, reduces them to the highest version per stream and acknowledges them, without
/// requesting timestamps. It stands in for the anchoring pipeline of #158 in tests and benchmarks.
/// </summary>
internal sealed class AnchorCandidateConsumer
{
    private readonly IMongoHelper _mongoHelper;
    private readonly IAnchorCandidateSource _source;

    public AnchorCandidateConsumer(IMongoHelper mongoHelper, IAnchorCandidateSource source)
    {
        ArgumentNullException.ThrowIfNull(mongoHelper);
        ArgumentNullException.ThrowIfNull(source);
        _mongoHelper = mongoHelper;
        _source = source;
    }

    /// <summary>
    /// Reduces candidates to the highest version per stream, which covers its predecessors through the chain.
    /// </summary>
    /// <param name="candidates">The candidates, possibly with several versions per stream.</param>
    /// <returns>One candidate per stream.</returns>
    public static IReadOnlyList<AnchorCandidate> Reduce(IEnumerable<AnchorCandidate> candidates)
        => candidates.GroupBy(candidate => (candidate.AggregateType, candidate.AggregateId))
                     .Select(stream => stream.OrderByDescending(candidate => candidate.Version).First())
                     .ToList();

    /// <summary>
    /// Reads one batch, acknowledges it in a transaction and returns its reduced candidates. Reads of an earlier
    /// attempt that were not acknowledged are discarded first, so their candidates are read again.
    /// </summary>
    /// <param name="maxCandidates">The maximum number of candidates to read.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The candidates of the batch, one per stream, and whether more are available right away.</returns>
    public async Task<ConsumedAnchorCandidates> ConsumeBatchAsync(Int32 maxCandidates, CancellationToken cancellationToken = default)
    {
        _source.DiscardUnacknowledgedReads();
        var batch = await _source.ReadBatchAsync(maxCandidates, cancellationToken);
        await _mongoHelper.ExecuteInTransaction((_, session, ct) => _source.AcknowledgeAsync(session, batch, ct),
                                                cancellationToken: cancellationToken);
        return new ConsumedAnchorCandidates(Reduce(batch.Candidates), batch.HasMore);
    }

    /// <summary>
    /// Consumes batches until the source reports that no more candidates are available right away, for example
    /// to finish a recovery scan.
    /// </summary>
    /// <remarks>
    /// Under sustained append load the change stream keeps returning full batches, so draining may not end.
    /// Callers that run concurrently with appends bound it themselves, as an anchoring run does.
    /// </remarks>
    /// <param name="maxCandidates">The maximum number of candidates per batch.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The consumed candidates, one per stream.</returns>
    public async Task<IReadOnlyList<AnchorCandidate>> DrainAsync(Int32 maxCandidates, CancellationToken cancellationToken = default)
    {
        var candidates = new List<AnchorCandidate>();
        ConsumedAnchorCandidates consumed;
        do
        {
            consumed = await ConsumeBatchAsync(maxCandidates, cancellationToken);
            candidates.AddRange(consumed.Candidates);
        } while (consumed.HasMore);

        return Reduce(candidates);
    }

    /// <summary>
    /// Consumes batches until cancelled, pausing whenever a batch has no candidates and no more are available.
    /// </summary>
    /// <param name="maxCandidates">The maximum number of candidates per batch.</param>
    /// <param name="idleDelay">The pause after a batch without candidates and without more to come.</param>
    /// <param name="cancellationToken">A cancellation token that stops the loop.</param>
    /// <returns>A task that completes when the loop is cancelled.</returns>
    public async Task RunAsync(Int32 maxCandidates, TimeSpan idleDelay, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var consumed = await ConsumeBatchAsync(maxCandidates, cancellationToken);
                if (consumed.Candidates.Count == 0 && !consumed.HasMore)
                {
                    await Task.Delay(idleDelay, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
