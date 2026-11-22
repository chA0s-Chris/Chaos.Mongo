// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Queues;

/// <summary>
/// Represents a queue subscription that monitors and processes queue items.
/// </summary>
/// <typeparam name="TPayload">The type of payload stored in queue items.</typeparam>
internal interface IMongoQueueSubscription<TPayload> : IAsyncDisposable
    where TPayload : class
{
    /// <summary>
    /// Gets a value indicating whether the subscription is actively running and not disposed.
    /// </summary>
    Boolean IsActive { get; }

    /// <summary>
    /// Starts the queue subscription by initializing indexes and launching background tasks for monitoring and processing.
    /// </summary>
    /// <remarks>
    /// This method is idempotent - calling it multiple times has no effect if already started. It also has no effect while a
    /// stop is in progress, because the stopping token of a subscription cannot be reset. Once the subscription is disposed,
    /// it throws an <see cref="InvalidOperationException"/>.
    /// Two background tasks are launched:
    /// <list type="bullet">
    ///     <item>Change stream monitor for real-time notifications</item>
    ///     <item>Queue processor for handling items</item>
    /// </list>
    /// </remarks>
    /// <param name="cancellationToken">The cancellation token for the startup operation.</param>
    /// <returns>A task that completes when the subscription has started.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the subscription is already disposed.</exception>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the queue subscription by cancelling background tasks and waiting for them to complete.
    /// </summary>
    /// <remarks>
    /// If the cancellation token is triggered before background tasks complete, a warning is logged
    /// and the method returns, but background tasks may still be running.
    /// This method is idempotent and safe to call multiple times. Concurrent calls, and a concurrent
    /// <see cref="IAsyncDisposable.DisposeAsync"/>, share one stop; each caller's token bounds only its own wait.
    /// </remarks>
    /// <param name="cancellationToken">The cancellation token representing the caller's shutdown timeout.</param>
    /// <returns>A task that completes when the subscription has stopped or the timeout expires.</returns>
    Task StopAsync(CancellationToken cancellationToken = default);
}
