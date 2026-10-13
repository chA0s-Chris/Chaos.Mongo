// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox;

/// <summary>
/// Controls the lifecycle of the outbox background processor.
/// </summary>
/// <remarks>
/// When auto-start is enabled, the processor is started automatically by <see cref="OutboxHostedService"/>.
/// When auto-start is off, the user can inject this interface and start/stop the processor manually.
/// </remarks>
public interface IOutboxProcessor
{
    /// <summary>
    /// Starts the outbox processor, which begins polling for pending messages.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes when the processor has started.</returns>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the outbox processor gracefully.
    /// </summary>
    /// <remarks>
    /// Stopping the processor prevents it from polling for new messages. If a message is already being
    /// processed, shutdown cancels only the in-flight publish, and only if the publisher observes the
    /// processor cancellation token. A canceled publish leaves the message pending so it can be retried
    /// on the next startup or by another processor instance. A message whose publish completed
    /// successfully is still marked as processed; once a stop has been requested, that update gets a
    /// short grace period, after which its lock is released and it remains pending for redelivery.
    /// Any remaining unclaimed messages from the current batch also remain pending for later processing.
    /// </remarks>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes when the processor has stopped.</returns>
    Task StopAsync(CancellationToken cancellationToken = default);
}
