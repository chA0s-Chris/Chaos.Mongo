// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

/// <summary>
/// Anchors the stream heads changed since the previous anchor in a Merkle tree, chains the anchor to its predecessor
/// and has it timestamped.
/// </summary>
/// <remarks>
///     <para>
///     A run executes on one instance at a time under a distributed lock and is crash-safe at every phase:
///     </para>
///     <list type="number">
///         <item>
///             <description>Complete or retry the anchor awaiting its timestamps; while it cannot complete, the run stops.</description>
///         </item>
///         <item>
///             <description>Read candidates up to the leaf maximum and reduce them to the highest version per stream.</description>
///         </item>
///         <item>
///             <description>
///             Insert the anchor as prepared, write its leaves, then commit it with its sequence number and acknowledge
///             the candidates in one transaction. That transaction is the commit point.
///             </description>
///         </item>
///         <item>
///             <description>Request tokens from the authorities and complete the anchor at the configured minimum.</description>
///         </item>
///     </list>
///     <para>
///     Before that, every run discards prepared attempts, deletes the leaves of discarded attempts and removes attempts
///     discarded long ago; under the lock, a prepared attempt belongs to a crashed or superseded run. A run does not
///     loop, so a backlog drains at one anchor per run.
///     </para>
///     <para>
///     After the phases, whatever their outcome and even when they throw, a run confirms the revocation status of
///     stored tokens whose grace period has passed (see <see cref="TimestampRevocationConfirmer"/>); only a lost lock
///     skips it. Confirmation therefore does not block anchoring, and a failing anchoring phase does not starve it.
///     </para>
/// </remarks>
internal sealed class AnchoringRun
{
    /// <summary>
    /// The name of the distributed lock that ensures a single anchoring run.
    /// </summary>
    public const String LockName = "Chaos.Mongo.EventStore.IntegrityAnchoring";

    private static readonly Comparer<Byte[]> LeafComparer = Comparer<Byte[]>.Create((x, y) => x.AsSpan().SequenceCompareTo(y));

    private const Int32 LeafInsertBatchSize = 1_000;
    private readonly IReadOnlyList<ITimestampAuthority> _authorities;
    private readonly TimestampRevocationConfirmer _confirmer;
    private readonly AnchorEvidenceStore _evidence;
    private readonly ILogger _logger;
    private readonly IMongoHelper _mongoHelper;
    private readonly AnchorCompletionNotifier? _notifier;
    private readonly IntegrityAnchoringOptions _options;
    private readonly IAnchorCandidateSource _source;
    private readonly AnchorStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly IntegrityVerificationOptions? _verification;

    public AnchoringRun(IMongoHelper mongoHelper,
                        IAnchorCandidateSource source,
                        IReadOnlyList<ITimestampAuthority> authorities,
                        IntegrityAnchoringOptions options,
                        TimeProvider timeProvider,
                        ILogger<AnchoringRun> logger,
                        AnchorCompletionNotifier? notifier = null,
                        IntegrityVerificationOptions? verification = null)
    {
        ArgumentNullException.ThrowIfNull(mongoHelper);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(authorities);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        ValidateConfiguration(authorities, options);
        _mongoHelper = mongoHelper;
        _source = source;
        _authorities = authorities;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _notifier = notifier;
        _verification = verification;
        _store = new AnchorStore(mongoHelper, options);
        _evidence = new AnchorEvidenceStore(mongoHelper, options);
        _confirmer = new TimestampRevocationConfirmer(_store, _evidence, authorities, options, timeProvider, logger);
    }

    /// <summary>
    /// Gets the oldest commit time among the candidates the last run read but has not committed into an anchor, or
    /// <c>null</c> when the last read found none or its candidates were committed. A run that fails before its commit
    /// point leaves it set, so the anchor delay can still be reported.
    /// </summary>
    internal DateTime? LastReadOldestCandidateUtc { get; private set; }

    /// <summary>
    /// Gets or sets the number of anchors whose due entries the revocation confirmation of one run reads at most. Tests
    /// lower it.
    /// </summary>
    internal Int32 MaximumConfirmationAnchorsPerRun
    {
        get => _confirmer.MaximumAnchorsPerRun;
        set => _confirmer.MaximumAnchorsPerRun = value;
    }

    /// <summary>
    /// Gets or sets a callback invoked after each phase. Tests use it to abort a run after any phase and resume
    /// from the stored state.
    /// </summary>
    internal Func<AnchoringPhase, CancellationToken, Task>? PhaseCompleted { get; set; }

    /// <summary>
    /// Runs the anchoring phases once, unless another instance holds the anchoring lock.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>How the run ended.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the run's anchor cannot be committed because a later run discarded the attempt or took its sequence
    /// number; its candidates stay unacknowledged.
    /// </exception>
    public async Task<AnchoringRunResult> RunAsync(CancellationToken cancellationToken = default)
    {
        await using var runLock = await _mongoHelper.TryAcquireLockAsync(LockName, MongoDefaults.LockLeaseTime, cancellationToken);
        if (runLock is null)
        {
            _logger.LogDebug("Anchoring run skipped; another instance holds the lock");
            return new AnchoringRunResult(AnchoringRunOutcome.LockUnavailable);
        }

        await _store.DiscardPreparingAsync(_timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        await _store.CleanUpDiscardedAsync(_timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        var notified = await NotifyAsync(runLock, cancellationToken);

        AnchoringRunResult result;
        try
        {
            result = await RunPhasesAsync(runLock, cancellationToken);
        }
        catch (Exception ex) when (!IsCancellation(ex, cancellationToken))
        {
            // An anchoring failure can repeat on every run, for example for an anchor that cannot be stored, and must
            // not keep stored tokens from being confirmed; the run still fails afterwards.
            await ConfirmRevocationAsync(runLock, cancellationToken);
            throw;
        }

        if (result.Outcome != AnchoringRunOutcome.LockLost)
        {
            await ConfirmRevocationAsync(runLock, cancellationToken);
        }

        if (notified)
        {
            // A hook that failed before the run is retried on the next run, not again in this one.
            await NotifyAsync(runLock, cancellationToken);
        }

        return result;
    }

    private static Boolean IsCancellation(Exception exception, CancellationToken cancellationToken)
        => exception is OperationCanceledException && cancellationToken.IsCancellationRequested;

    private static void ValidateConfiguration(IReadOnlyList<ITimestampAuthority> authorities, IntegrityAnchoringOptions options)
    {
        if (authorities.Count == 0)
        {
            throw new ArgumentException("Anchoring needs at least one timestamp authority.", nameof(authorities));
        }

        var duplicate = authorities.GroupBy(authority => authority.Name).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"The timestamp authority name '{duplicate.Key}' is used more than once.", nameof(authorities));
        }

        if (options.MinimumTimestampTokens < 1 || options.MinimumTimestampTokens > authorities.Count)
        {
            throw new ArgumentException(
                $"The minimum number of timestamp tokens must be between 1 and the number of authorities ({authorities.Count}).", nameof(options));
        }

        if (options.MaxLeavesPerAnchor < 1 || options.CandidateReadBatchSize < 1)
        {
            throw new ArgumentException("The maximum number of leaves and the candidate batch size must be positive.", nameof(options));
        }

        if (options.RevocationGracePeriod <= TimeSpan.Zero || options.RevocationConfirmationDelayThreshold <= options.RevocationGracePeriod)
        {
            throw new ArgumentException("The revocation grace period must be positive and less than the revocation confirmation delay threshold.",
                                        nameof(options));
        }
    }

    private async Task CommitAsync(AnchorRecord anchor, AnchorCandidateBatch lastBatch, CancellationToken cancellationToken)
        => await _mongoHelper.ExecuteInTransactionAsync(async (_, session, ct) =>
                                                        {
                                                            if (!await _store.CommitAsync(session, anchor.Id, anchor.Sequence, ct))
                                                            {
                                                                throw new InvalidOperationException(
                                                                    $"Anchor {anchor.Sequence} could not be committed: a later run discarded the attempt " +
                                                                    "or another anchor took its sequence number.");
                                                            }

                                                            await _source.AcknowledgeAsync(session, lastBatch, ct);
                                                        },
                                                        cancellationToken: cancellationToken);

    /// <summary>
    /// Confirms the revocation status of due tokens unless the lock was lost. A failure is logged and retried by the
    /// next run, so it never fails the run.
    /// </summary>
    private async Task ConfirmRevocationAsync(IMongoLock runLock, CancellationToken cancellationToken)
    {
        if (!runLock.IsValid)
        {
            return;
        }

        try
        {
            await _confirmer.ConfirmAsync(ct => EnsureLockAsync(runLock, ct), cancellationToken);
        }
        catch (Exception ex) when (!IsCancellation(ex, cancellationToken))
        {
            _logger.LogError(ex, "Confirming the revocation status of timestamps failed; it is retried with the next run");
        }
    }

    private async Task<Boolean> EnsureLockAsync(IMongoLock runLock, CancellationToken cancellationToken)
    {
        if (!runLock.IsValid)
        {
            return false;
        }

        var remaining = runLock.ValidUntilUtc - _timeProvider.GetUtcNow().UtcDateTime;
        return remaining >= MongoDefaults.LockLeaseTime / 2 ||
               await runLock.TryExtendAsync(MongoDefaults.LockLeaseTime, cancellationToken);
    }

    /// <returns><c>true</c> when every completed anchor is notified, or no hook is configured.</returns>
    private async Task<Boolean> NotifyAsync(IMongoLock runLock, CancellationToken cancellationToken)
    {
        if (_notifier is null)
        {
            return true;
        }

        try
        {
            return await _notifier.NotifyAsync(ct => EnsureLockAsync(runLock, ct), cancellationToken);
        }
        catch (Exception ex) when (!IsCancellation(ex, cancellationToken))
        {
            // Notification is retried by the next run and must not keep this run from anchoring.
            _logger.LogError(ex, "Notifying completed anchors failed; it is retried with the next run");
            return false;
        }
    }

    private Task OnPhaseCompletedAsync(AnchoringPhase phase, CancellationToken cancellationToken)
        => PhaseCompleted?.Invoke(phase, cancellationToken) ?? Task.CompletedTask;

    /// <summary>
    /// Inserts the prepared anchor and its leaves. The sequence number is fixed here, before the leaves carry it.
    /// </summary>
    /// <returns>The prepared anchor, or <c>null</c> when the lock was lost.</returns>
    private async Task<AnchorRecord?> PrepareAsync(CandidateRead read, IMongoLock runLock, CancellationToken cancellationToken)
    {
        var latest = await _store.FindLatestCommittedAsync(cancellationToken);
        var sequence = (latest?.Sequence ?? 0) + 1;
        var previousDigest = latest?.AnchorDigest ?? new Byte[AnchorEncoding.HashLength];

        var leaves = read.Heads.Select(candidate => (Candidate: candidate, Data: AnchorEncoding.EncodeLeaf(candidate)))
                         .OrderBy(leaf => leaf.Data, LeafComparer)
                         .ToList();
        var tree = MerkleTree.Build(leaves.Select(leaf => MerkleTree.HashLeaf(leaf.Data)).ToList());
        var periodStart = latest?.PeriodEndUtc ?? read.OldestUtc;
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var anchor = new AnchorRecord(Guid.NewGuid(),
                                      AnchorStatus.Preparing,
                                      sequence,
                                      AnchorEncoding.FormatVersion,
                                      AnchorEncoding.HashAlgorithmName,
                                      previousDigest,
                                      tree.Root,
                                      AnchorEncoding.ComputeAnchorDigest(AnchorEncoding.FormatVersion, sequence, previousDigest, tree.Root),
                                      leaves.Count,
                                      periodStart,
                                      read.NewestUtc > periodStart ? read.NewestUtc : periodStart,
                                      read.OldestUtc,
                                      [],
                                      [],
                                      now,
                                      null);
        await _store.InsertPreparingAsync(anchor, cancellationToken);

        for (var offset = 0; offset < leaves.Count; offset += LeafInsertBatchSize)
        {
            if (!await EnsureLockAsync(runLock, cancellationToken))
            {
                return null;
            }

            var chunk = Enumerable.Range(offset, Math.Min(LeafInsertBatchSize, leaves.Count - offset))
                                  .Select(index => new AnchorLeaf(anchor.Id,
                                                                  sequence,
                                                                  index,
                                                                  leaves[index].Candidate.AggregateType,
                                                                  leaves[index].Candidate.AggregateId,
                                                                  leaves[index].Candidate.Version,
                                                                  leaves[index].Candidate.Hash,
                                                                  tree.GetInclusionProof(index)));
            await _store.InsertLeavesAsync(chunk, cancellationToken);
        }

        return anchor;
    }

    /// <summary>
    /// Reads candidates until the source reports no more or the leaf maximum is reached, reducing them to the highest
    /// version per stream.
    /// </summary>
    /// <returns>The read candidates, or <c>null</c> when the lock was lost.</returns>
    private async Task<CandidateRead?> ReadCandidatesAsync(IMongoLock runLock, CancellationToken cancellationToken)
    {
        // An earlier attempt of this process may have read without acknowledging; those candidates must be read again.
        _source.DiscardUnacknowledgedReads();
        var heads = new Dictionary<(String AggregateType, Guid AggregateId), AnchorCandidate>();
        var oldest = DateTime.MaxValue;
        var newest = DateTime.MinValue;
        // The source reports more as long as changes keep arriving, whether or not they yield candidates, so the
        // batches read bound the run as well as the distinct streams; continuous changes must not keep a run from
        // committing or from acknowledging its progress.
        var maxBatches = Math.Max(1, (_options.MaxLeavesPerAnchor + _options.CandidateReadBatchSize - 1) / _options.CandidateReadBatchSize);
        var batches = 0;
        AnchorCandidateBatch batch;
        do
        {
            if (!await EnsureLockAsync(runLock, cancellationToken))
            {
                return null;
            }

            batch = await _source.ReadBatchAsync(_options.CandidateReadBatchSize, cancellationToken);
            batches++;
            foreach (var candidate in batch.Candidates)
            {
                var key = (candidate.AggregateType, candidate.AggregateId);
                if (!heads.TryGetValue(key, out var head) || candidate.Version > head.Version)
                {
                    heads[key] = candidate;
                }

                oldest = candidate.CommittedUtc < oldest ? candidate.CommittedUtc : oldest;
                newest = candidate.CommittedUtc > newest ? candidate.CommittedUtc : newest;
            }
        } while (batch.HasMore && heads.Count < _options.MaxLeavesPerAnchor && batches < maxBatches);

        LastReadOldestCandidateUtc = heads.Count > 0 ? oldest : null;
        return new CandidateRead(heads.Values.ToList(), batch, oldest, newest);
    }

    private async Task<AnchoringRunResult> RunPhasesAsync(IMongoLock runLock, CancellationToken cancellationToken)
    {
        if (await _store.FindAwaitingTimestampAsync(cancellationToken) is { } pending)
        {
            var completed = await StampAsync(pending, runLock, cancellationToken);
            await OnPhaseCompletedAsync(AnchoringPhase.PendingAnchorStamped, cancellationToken);
            if (!completed)
            {
                var pendingOutcome = runLock.IsValid ? AnchoringRunOutcome.AwaitingTimestamps : AnchoringRunOutcome.LockLost;
                return new AnchoringRunResult(pendingOutcome, await _store.FindAsync(pending.Id, cancellationToken), pending.OldestCandidateUtc);
            }
        }

        var read = await ReadCandidatesAsync(runLock, cancellationToken);
        if (read is null)
        {
            return new AnchoringRunResult(AnchoringRunOutcome.LockLost);
        }

        if (read.Heads.Count == 0)
        {
            // Persists the progress of the source, such as a scan over unsealed streams or a fresh resume token.
            await _mongoHelper.ExecuteInTransactionAsync((_, session, ct) => _source.AcknowledgeAsync(session, read.LastBatch, ct),
                                                         cancellationToken: cancellationToken);
            return new AnchoringRunResult(AnchoringRunOutcome.NoCandidates);
        }

        await OnPhaseCompletedAsync(AnchoringPhase.CandidatesRead, cancellationToken);

        var anchor = await PrepareAsync(read, runLock, cancellationToken);
        if (anchor is null)
        {
            return new AnchoringRunResult(AnchoringRunOutcome.LockLost, OldestCandidateUtc: read.OldestUtc);
        }

        await OnPhaseCompletedAsync(AnchoringPhase.LeavesWritten, cancellationToken);
        if (!await EnsureLockAsync(runLock, cancellationToken))
        {
            return new AnchoringRunResult(AnchoringRunOutcome.LockLost, OldestCandidateUtc: read.OldestUtc);
        }

        await CommitAsync(anchor, read.LastBatch, cancellationToken);
        LastReadOldestCandidateUtc = null;
        anchor = anchor with
        {
            Status = AnchorStatus.AwaitingTimestamp
        };
        await OnPhaseCompletedAsync(AnchoringPhase.Committed, cancellationToken);

        var stamped = await StampAsync(anchor, runLock, cancellationToken);
        await OnPhaseCompletedAsync(AnchoringPhase.Stamped, cancellationToken);
        var outcome = stamped ? AnchoringRunOutcome.Completed :
            runLock.IsValid ? AnchoringRunOutcome.AwaitingTimestamps : AnchoringRunOutcome.LockLost;
        return new AnchoringRunResult(outcome, await _store.FindAsync(anchor.Id, cancellationToken), read.OldestUtc);
    }

    /// <summary>
    /// Requests tokens from every authority that has not yet delivered one and completes the anchor at the
    /// configured minimum.
    /// </summary>
    /// <returns><c>true</c> when the anchor is completed.</returns>
    private async Task<Boolean> StampAsync(AnchorRecord anchor, IMongoLock runLock, CancellationToken cancellationToken)
    {
        var tokens = anchor.Timestamps.Count;
        var failures = anchor.AuthorityFailures.ToDictionary(failure => failure.Authority);
        foreach (var authority in _authorities.Where(authority => anchor.Timestamps.All(timestamp => timestamp.Authority != authority.Name)))
        {
            // An anchor that already holds the minimum is still completed below; the transition is status-filtered.
            if (!await EnsureLockAsync(runLock, cancellationToken))
            {
                break;
            }

            TimestampToken token;
            try
            {
                token = await authority.TimestampAsync(anchor.AnchorDigest, HashAlgorithmName.SHA256, cancellationToken);

                // Every token is checked before it is stored, so a custom authority cannot complete an anchor with
                // a malformed token or chain, nor with an untrusted one when verification knows the authority's trust.
                TimestampTokenValidation.ValidateReceived(token, anchor.AnchorDigest, _verification?.FindTrust(authority.Name));
            }
            catch (Exception ex) when (!IsCancellation(ex, cancellationToken))
            {
                _logger.LogWarning(ex, "Timestamp authority {AuthorityName} failed to stamp anchor {AnchorSequence}", authority.Name, anchor.Sequence);
                failures.TryGetValue(authority.Name, out var previous);
                failures[authority.Name] = new AnchorAuthorityFailure(authority.Name, (previous?.FailureCount ?? 0) + 1, ex.Message, _timeProvider.GetUtcNow().UtcDateTime);
                await _store.SetAuthorityFailuresAsync(anchor.Id, failures.Values, cancellationToken);
                continue;
            }

            var timestamp = await StoreEvidenceAsync(authority.Name, token, _timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
            if (await _store.AddTimestampAsync(anchor.Id, timestamp, cancellationToken))
            {
                tokens++;
            }
        }

        if (tokens < _options.MinimumTimestampTokens || !await _store.CompleteAsync(anchor.Id, _timeProvider.GetUtcNow().UtcDateTime, cancellationToken))
        {
            return false;
        }

        _logger.LogInformation("Anchor {AnchorSequence} with {LeafCount} leaves completed with {TokenCount} timestamp tokens",
                               anchor.Sequence, anchor.LeafCount, tokens);
        return true;
    }

    /// <summary>
    /// Stores the token and its evidence before the anchor references them, so an anchor never references evidence
    /// that is not complete.
    /// </summary>
    private async Task<AnchorTimestampReference> StoreEvidenceAsync(String authority, TimestampToken token, DateTime obtainedUtc, CancellationToken cancellationToken)
    {
        var tokenHash = await _evidence.StoreAsync(token.Token, cancellationToken);
        var certificateHashes = new List<Byte[]>(token.CertificateChain.Count);
        foreach (var certificate in token.CertificateChain)
        {
            certificateHashes.Add(await _evidence.StoreAsync(certificate, cancellationToken));
        }

        var crlHashes = new List<Byte[]>(token.CertificateRevocationLists.Count);
        foreach (var crl in token.CertificateRevocationLists)
        {
            crlHashes.Add(await _evidence.StoreAsync(crl, cancellationToken));
        }

        return new AnchorTimestampReference(authority, tokenHash, certificateHashes, crlHashes, token.Timestamp.UtcDateTime, obtainedUtc);
    }

    private sealed record CandidateRead(IReadOnlyList<AnchorCandidate> Heads, AnchorCandidateBatch LastBatch, DateTime OldestUtc, DateTime NewestUtc);
}
