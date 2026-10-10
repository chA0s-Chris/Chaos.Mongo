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
    /// Reads one batch, acknowledges it in a transaction and returns its reduced candidates.
    /// </summary>
    /// <param name="maxCandidates">The maximum number of candidates to read.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The candidates of the batch, one per stream.</returns>
    public async Task<IReadOnlyList<AnchorCandidate>> ConsumeBatchAsync(Int32 maxCandidates, CancellationToken cancellationToken = default)
    {
        var batch = await _source.ReadBatchAsync(maxCandidates, cancellationToken);
        await _mongoHelper.ExecuteInTransaction((_, session, ct) => _source.AcknowledgeAsync(session, batch, ct),
                                                cancellationToken: cancellationToken);
        return Reduce(batch.Candidates);
    }

    /// <summary>
    /// Consumes batches until cancelled, pausing whenever a batch comes back empty.
    /// </summary>
    /// <param name="maxCandidates">The maximum number of candidates per batch.</param>
    /// <param name="idleDelay">The pause after an empty batch.</param>
    /// <param name="cancellationToken">A cancellation token that stops the loop.</param>
    /// <returns>A task that completes when the loop is cancelled.</returns>
    public async Task RunAsync(Int32 maxCandidates, TimeSpan idleDelay, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var candidates = await ConsumeBatchAsync(maxCandidates, cancellationToken);
                if (candidates.Count == 0)
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
