// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Hosted service that manages the outbox processor lifecycle when auto-start is enabled.
/// During startup, it runs outbox configurators first and then starts the processor.
/// </summary>
public sealed class OutboxHostedService : IHostedLifecycleService
{
    private readonly ILogger<OutboxHostedService> _logger;
    private readonly IOutboxProcessor _outboxProcessor;
    private readonly IServiceScopeFactory _serviceScopeFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="OutboxHostedService"/> class.
    /// </summary>
    /// <param name="outboxProcessor">The outbox processor to manage.</param>
    /// <param name="serviceScopeFactory">The service scope factory for resolving scoped services.</param>
    /// <param name="logger">The logger for diagnostic messages.</param>
    public OutboxHostedService(IOutboxProcessor outboxProcessor,
                               IServiceScopeFactory serviceScopeFactory,
                               ILogger<OutboxHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(outboxProcessor);
        ArgumentNullException.ThrowIfNull(serviceScopeFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _outboxProcessor = outboxProcessor;
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task StartedAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Outbox hosted service started — starting outbox processor");
        await _outboxProcessor.StartAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Outbox hosted service starting — running outbox configurators");

        using var scope = _serviceScopeFactory.CreateScope();
        var configuratorRunner = scope.ServiceProvider.GetRequiredService<IOutboxConfiguratorRunner>();
        await configuratorRunner.RunAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public async Task StoppingAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Outbox hosted service stopping — stopping outbox processor");
        await _outboxProcessor.StopAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
