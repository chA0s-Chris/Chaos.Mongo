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
    /// The currently in-flight message is allowed to finish processing. Any remaining unclaimed messages
    /// from the current batch remain pending for the next startup or another processor instance.
    /// </remarks>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes when the processor has stopped.</returns>
    Task StopAsync(CancellationToken cancellationToken = default);
}
