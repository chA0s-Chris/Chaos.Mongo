// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Passes completed anchors to the application's completion hook, in sequence order and at least once.
/// </summary>
/// <remarks>
/// An anchor is marked notified only after the hook returns. A failing hook is logged and retried on the next run,
/// and later anchors are not notified before it succeeds; anchoring itself continues. Each hook call runs in its own
/// dependency-injection scope. The run's lock is checked before each anchor and kept alive while a hook runs; when
/// the lease cannot be extended, the hook is cancelled and its anchor stays unnotified, so a slow hook keeps the lock
/// instead of outliving it while another instance notifies the same anchors.
/// </remarks>
internal sealed class AnchorCompletionNotifier
{
    private readonly Func<IServiceProvider, CompletedAnchor, CancellationToken, Task> _hook;
    private readonly ILogger _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AnchorStore _store;
    private readonly TimeProvider _timeProvider;

    public AnchorCompletionNotifier(IMongoHelper mongoHelper,
                                    IntegrityAnchoringOptions options,
                                    IServiceScopeFactory scopeFactory,
                                    Func<IServiceProvider, CompletedAnchor, CancellationToken, Task> hook,
                                    TimeProvider timeProvider,
                                    ILogger<AnchorCompletionNotifier> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(hook);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _store = new AnchorStore(mongoHelper, options);
        _scopeFactory = scopeFactory;
        _hook = hook;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Gets or sets how often the lock is checked, and extended if needed, while a hook runs. Defaults to a quarter
    /// of the lock lease.
    /// </summary>
    internal TimeSpan LeaseCheckInterval { get; set; } = MongoDefaults.LockLeaseTime / 4;

    /// <summary>
    /// Notifies the completed anchors that were not yet notified, stopping at the first failing hook call or when the
    /// lock is lost.
    /// </summary>
    /// <param name="ensureLockAsync">Checks, and extends if needed, the lock of the run.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>
    /// <c>true</c> when every completed anchor is notified; <c>false</c> when a hook call failed or the lock was lost,
    /// so the remaining anchors are notified on a later run.
    /// </returns>
    public async Task<Boolean> NotifyAsync(Func<CancellationToken, Task<Boolean>> ensureLockAsync, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ensureLockAsync);
        while (true)
        {
            if (!await ensureLockAsync(cancellationToken))
            {
                return false;
            }

            if (await _store.FindFirstUnnotifiedAsync(cancellationToken) is not { } anchor)
            {
                return true;
            }

            if (!await InvokeHookAsync(anchor, ensureLockAsync, cancellationToken))
            {
                return false;
            }

            await _store.MarkNotifiedAsync(anchor.Id, _timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        }
    }

    /// <summary>
    /// Calls the hook for one anchor while keeping the lock alive; the hook is cancelled when the lease cannot be
    /// extended.
    /// </summary>
    /// <returns><c>true</c> when the hook returned normally.</returns>
    private async Task<Boolean> InvokeHookAsync(AnchorRecord anchor,
                                                Func<CancellationToken, Task<Boolean>> ensureLockAsync,
                                                CancellationToken cancellationToken)
    {
        using var hookCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var hookCompleted = new CancellationTokenSource();
        var keepAlive = KeepLockAliveAsync(ensureLockAsync, hookCancellation, hookCompleted.Token);
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            await _hook(scope.ServiceProvider, CompletedAnchor.From(anchor), hookCancellation.Token);
            return true;
        }
        catch (OperationCanceledException) when (hookCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("The completion hook for anchor {AnchorSequence} was cancelled because the anchoring lock could not be extended; " +
                               "the anchor is notified on a later run",
                               anchor.Sequence);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "The completion hook failed for anchor {AnchorSequence}; it and later anchors are notified on a later run",
                             anchor.Sequence);
            return false;
        }
        finally
        {
            await hookCompleted.CancelAsync();
            await keepAlive;
        }
    }

    /// <summary>
    /// Extends the lock at <see cref="LeaseCheckInterval"/> until the hook completes, cancelling the hook when an
    /// extension fails.
    /// </summary>
    private async Task KeepLockAliveAsync(Func<CancellationToken, Task<Boolean>> ensureLockAsync,
                                          CancellationTokenSource hookCancellation,
                                          CancellationToken hookCompleted)
    {
        try
        {
            while (true)
            {
                await Task.Delay(LeaseCheckInterval, _timeProvider, hookCompleted);
                if (!await ensureLockAsync(hookCompleted))
                {
                    await hookCancellation.CancelAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (hookCompleted.IsCancellationRequested)
        {
            // The hook completed.
        }
        catch (Exception ex)
        {
            // The lease may have expired, so the hook must not continue as if the lock were held.
            _logger.LogWarning(ex, "The anchoring lock could not be extended while a completion hook was running");
            await hookCancellation.CancelAsync();
        }
    }
}
