// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using Microsoft.Extensions.Hosting;

/// <summary>
/// Hosts the anchoring loop. It starts once the application has started, so configurators have created the
/// collections and indexes.
/// </summary>
internal sealed class IntegrityAnchoringHostedService : IHostedLifecycleService
{
    private readonly IntegrityAnchoringService _service;

    public IntegrityAnchoringHostedService(IntegrityAnchoringService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    /// <inheritdoc/>
    public Task StartedAsync(CancellationToken cancellationToken) => _service.StartAsync(cancellationToken);

    /// <inheritdoc/>
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task StoppingAsync(CancellationToken cancellationToken) => _service.StopAsync(cancellationToken);

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
