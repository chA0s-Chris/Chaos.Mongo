// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using Microsoft.Extensions.Logging;

/// <summary>
/// Runs the anchoring run in the background, once at start and then once per
/// <see cref="IntegrityAnchoringOptions.Interval"/>, and reports the anchor delay after every run.
/// </summary>
/// <remarks>
/// After a run that failed or lost its lock, the delay of the anchor awaiting its timestamps, or else of the last known
/// oldest unanchored candidate, is reported, so the alert does not go silent while anchoring fails.
/// </remarks>
internal sealed class IntegrityAnchoringService
{
    private readonly ILogger _logger;
    private readonly IntegrityAnchoringOptions _options;
    private readonly AnchoringRun _run;
    private readonly AnchorStore _store;
    private readonly TimeProvider _timeProvider;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task _loop = Task.CompletedTask;
    private DateTime? _unanchoredSinceUtc;

    public IntegrityAnchoringService(AnchoringRun run,
                                     IMongoHelper mongoHelper,
                                     IntegrityAnchoringOptions options,
                                     TimeProvider timeProvider,
                                     ILogger<IntegrityAnchoringService> logger)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _run = run;
        _store = new AnchorStore(mongoHelper, options);
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Runs the anchoring run once and reports the anchor delay. A failed run is logged and retried by the next one.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The result of the run, or <c>null</c> when it failed or was cancelled.</returns>
    public async Task<AnchoringRunResult?> RunOnceAsync(CancellationToken cancellationToken)
    {
        AnchoringRunResult result;
        try
        {
            result = await _run.RunAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The anchoring run failed and is retried with the next run");
            await ReportDelayAfterFailureAsync(cancellationToken);
            return null;
        }

        _unanchoredSinceUtc = result.Outcome is AnchoringRunOutcome.AwaitingTimestamps or AnchoringRunOutcome.LockLost
            ? result.OldestCandidateUtc ?? _unanchoredSinceUtc
            : null;
        if (result.OldestCandidateUtc is { } oldest)
        {
            ReportDelay(oldest);
        }
        else if (result.Outcome == AnchoringRunOutcome.LockLost)
        {
            await ReportDelayAfterFailureAsync(cancellationToken);
        }

        return result;
    }

    /// <summary>
    /// Starts the background loop.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token linked to the loop.</param>
    /// <returns>A completed task; the loop runs in the background.</returns>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_cancellationTokenSource is not null)
        {
            return Task.CompletedTask;
        }

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = RunLoopAsync(_cancellationTokenSource.Token);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the background loop and waits for the current run to observe cancellation.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token that limits the wait.</param>
    /// <returns>A task that completes when the loop has stopped.</returns>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_cancellationTokenSource is null)
        {
            return;
        }

        await _cancellationTokenSource.CancelAsync();
        try
        {
            await _loop.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        finally
        {
            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = null;
        }
    }

    private void ReportDelay(DateTime oldest)
    {
        var delay = _timeProvider.GetUtcNow().UtcDateTime - oldest;
        var exceeded = delay > _options.DelayThreshold;
        IntegrityAnchoringDiagnostics.RecordAnchorDelay(delay, exceeded);
        if (exceeded)
        {
            _logger.LogWarning("The anchor delay of {AnchorDelay} is above the anchor delay threshold of {DelayThreshold}; " +
                               "the oldest candidate this run saw was committed that long ago",
                               delay, _options.DelayThreshold);
        }
    }

    /// <summary>
    /// Reports the delay of the anchor awaiting its timestamps, or else of the last known oldest unanchored candidate,
    /// after a run that could not report one itself.
    /// </summary>
    private async Task ReportDelayAfterFailureAsync(CancellationToken cancellationToken)
    {
        var oldest = _unanchoredSinceUtc;
        try
        {
            oldest = (await _store.FindAwaitingTimestampAsync(cancellationToken))?.OldestCandidateUtc ?? oldest;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "The anchor awaiting its timestamps could not be read to report the anchor delay");
        }

        if (oldest is { } value)
        {
            ReportDelay(value);
        }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await RunOnceAsync(cancellationToken);
            try
            {
                await Task.Delay(_options.Interval, _timeProvider, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
