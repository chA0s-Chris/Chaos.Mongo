// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.TestSupport;

/// <summary>
/// Holds a <see cref="CancellationTokenSource.CancelAsync"/> in the middle of its callbacks, so tests can run a second
/// stop while the first is still cancelling.
/// </summary>
/// <remarks>
/// Callbacks of one token run sequentially, in reverse registration order, and that order is only guaranteed for
/// callbacks registered by the same thread. <see cref="RegisterOn"/> therefore registers a marker first and a blocker
/// second, back to back: the blocker runs first and holds the cancellation while the marker is still pending. The
/// registrations are deliberately not disposed, because disposing a registration waits for a running callback.
/// </remarks>
public sealed class CancellationCallbackBarrier
{
    private readonly TaskCompletionSource _blockerEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _markerRan = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Gets a task that completes once the blocker callback holds the cancellation.
    /// </summary>
    public Task BlockerEntered => _blockerEntered.Task;

    /// <summary>
    /// Gets a task that completes once the marker callback, which runs after the blocker, has run.
    /// </summary>
    public Task MarkerRan => _markerRan.Task;

    /// <summary>
    /// Registers the marker and then the blocker on <paramref name="token"/>, in the same synchronous call.
    /// </summary>
    /// <param name="token">The token a component under test hands to its test double.</param>
    public void RegisterOn(CancellationToken token)
    {
        token.Register(() => _markerRan.TrySetResult());
        token.Register(() =>
        {
            _blockerEntered.TrySetResult();
            _release.Task.Wait();
        });
    }

    /// <summary>
    /// Lets the blocker return, so the cancellation continues with the marker. The blocker never blocks again afterwards,
    /// and tests call this in a <c>finally</c> block so a failing assertion cannot hang the run.
    /// </summary>
    public void Release() => _release.TrySetResult();
}
