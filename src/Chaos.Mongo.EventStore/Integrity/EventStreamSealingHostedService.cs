// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using Microsoft.Extensions.Hosting;

/// <summary>
/// Hosted service that runs the sealing sweep of a protected aggregate type while the host runs.
/// The sweep starts after startup, so configurators that create the event store indexes have already run.
/// </summary>
/// <typeparam name="TAggregate">The aggregate type.</typeparam>
internal sealed class EventStreamSealingHostedService<TAggregate> : IHostedLifecycleService
    where TAggregate : class, IAggregate, new()
{
    private readonly EventStreamSealingSweep<TAggregate> _sweep;

    public EventStreamSealingHostedService(EventStreamSealingSweep<TAggregate> sweep)
    {
        ArgumentNullException.ThrowIfNull(sweep);
        _sweep = sweep;
    }

    /// <inheritdoc/>
    public Task StartedAsync(CancellationToken cancellationToken) => _sweep.StartAsync(cancellationToken);

    /// <inheritdoc/>
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task StoppingAsync(CancellationToken cancellationToken) => _sweep.StopAsync(cancellationToken);

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
