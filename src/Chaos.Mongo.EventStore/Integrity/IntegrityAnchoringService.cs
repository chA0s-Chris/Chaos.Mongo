// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using Microsoft.Extensions.Logging;

/// <summary>
/// Runs the anchoring run in the background, once at start and then once per
/// <see cref="IntegrityAnchoringOptions.Interval"/>, and reports the anchor delay after every run.
/// </summary>
internal sealed class IntegrityAnchoringService
{
    private readonly ILogger _logger;
    private readonly IntegrityAnchoringOptions _options;
    private readonly AnchoringRun _run;
    private readonly TimeProvider _timeProvider;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task _loop = Task.CompletedTask;

    public IntegrityAnchoringService(AnchoringRun run,
                                     IntegrityAnchoringOptions options,
                                     TimeProvider timeProvider,
                                     ILogger<IntegrityAnchoringService> logger)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _run = run;
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
            return null;
        }

        ReportDelay(result);
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

    private void ReportDelay(AnchoringRunResult result)
    {
        if (result.OldestCandidateUtc is not { } oldest)
        {
            return;
        }

        var delay = _timeProvider.GetUtcNow().UtcDateTime - oldest;
        var exceeded = delay > _options.DelayThreshold;
        IntegrityAnchoringDiagnostics.RecordAnchorDelay(delay, exceeded);
        if (exceeded)
        {
            _logger.LogWarning("The oldest unanchored candidate is {AnchorDelay} old, above the anchor delay threshold of {DelayThreshold}",
                               delay, _options.DelayThreshold);
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
