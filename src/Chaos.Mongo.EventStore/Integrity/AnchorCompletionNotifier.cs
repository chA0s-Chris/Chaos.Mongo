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
/// dependency-injection scope, and the run's lock is checked before each anchor, so a slow hook cannot outlive the
/// lease while another instance notifies the same anchors.
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
    /// Notifies the completed anchors that were not yet notified, stopping at the first failing hook call or when the
    /// lock is lost.
    /// </summary>
    /// <param name="ensureLockAsync">Checks, and extends if needed, the lock of the run before each anchor.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes when the notifications are done, a hook call failed, or the lock was lost.</returns>
    public async Task NotifyAsync(Func<CancellationToken, Task<Boolean>> ensureLockAsync, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ensureLockAsync);
        while (await ensureLockAsync(cancellationToken) && await _store.FindFirstUnnotifiedAsync(cancellationToken) is { } anchor)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                await _hook(scope.ServiceProvider, CompletedAnchor.From(anchor), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "The completion hook failed for anchor {AnchorSequence}; it and later anchors are notified on a later run",
                                 anchor.Sequence);
                return;
            }

            await _store.MarkNotifiedAsync(anchor.Id, _timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        }
    }
}
