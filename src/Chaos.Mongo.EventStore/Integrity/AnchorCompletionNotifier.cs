// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using Microsoft.Extensions.Logging;

/// <summary>
/// Passes completed anchors to the application's completion hook, in sequence order and at least once.
/// </summary>
/// <remarks>
/// An anchor is marked notified only after the hook returns. A failing hook is logged and retried on the next run,
/// and later anchors are not notified before it succeeds; anchoring itself continues.
/// </remarks>
internal sealed class AnchorCompletionNotifier
{
    private readonly Func<IServiceProvider, CompletedAnchor, CancellationToken, Task> _hook;
    private readonly ILogger _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly AnchorStore _store;
    private readonly TimeProvider _timeProvider;

    public AnchorCompletionNotifier(IMongoHelper mongoHelper,
                                    IntegrityAnchoringOptions options,
                                    IServiceProvider serviceProvider,
                                    Func<IServiceProvider, CompletedAnchor, CancellationToken, Task> hook,
                                    TimeProvider timeProvider,
                                    ILogger<AnchorCompletionNotifier> logger)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(hook);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _store = new AnchorStore(mongoHelper, options);
        _serviceProvider = serviceProvider;
        _hook = hook;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Notifies every completed anchor that was not yet notified, stopping at the first failing hook call.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes when the notifications are done or a hook call failed.</returns>
    public async Task NotifyAsync(CancellationToken cancellationToken)
    {
        await foreach (var anchor in _store.FindUnnotifiedAsync(cancellationToken))
        {
            try
            {
                await _hook(_serviceProvider, CompletedAnchor.From(anchor), cancellationToken);
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
