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
    private readonly IOutboxProcessor? _outboxProcessor;
    private readonly OutboxRegistration[] _registrations = [];
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IServiceProvider? _services;

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

    internal OutboxHostedService(IServiceScopeFactory serviceScopeFactory, ILogger<OutboxHostedService> logger,
                                 OutboxRegistration[] registrations, IServiceProvider services)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
        _registrations = registrations;
        _services = services;
    }

    private async Task ObserveStopCompletionAsync(Task stopping)
    {
        try
        {
            await stopping;
        }
        catch (OperationCanceledException) when (stopping.IsCanceled)
        {
            // Cancellation after the shutdown deadline is expected.
        }
        catch (Exception exception)
        {
            _logger.LogError(stopping.Exception ?? exception,
                             "Outbox processor shutdown failed after the host stopped waiting");
        }
    }

    private async Task RunProcessorAsync(OutboxRegistration registration, Boolean start, CancellationToken cancellationToken)
    {
        using var logScope = _logger.BeginScope(new Dictionary<String, Object>
        {
            ["OutboxIdentity"] = registration.Options.Identity,
            ["CollectionName"] = registration.Options.CollectionName
        });
        _logger.LogDebug("{LifecycleAction} outbox processor", start ? "Starting" : "Stopping");
        if (_services is { } services)
        {
            var processor = registration.GetProcessor(services);
            if (start)
                await processor.StartAsync(cancellationToken);
            else
                await processor.StopAsync(cancellationToken);
        }
    }

    /// <inheritdoc/>
    public async Task StartedAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Outbox hosted service started — starting outbox processor");
        if (_outboxProcessor is not null)
            await _outboxProcessor.StartAsync(cancellationToken);
        else
            await Task.WhenAll(_registrations.Select(r => RunProcessorAsync(r, true, cancellationToken)));
    }

    /// <inheritdoc/>
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Outbox hosted service starting — running outbox configurators");

        using var scope = _serviceScopeFactory.CreateScope();
        if (_outboxProcessor is not null)
        {
            var configuratorRunner = scope.ServiceProvider.GetRequiredService<IOutboxConfiguratorRunner>();
            await configuratorRunner.RunAsync(cancellationToken);
        }
        else
        {
            var helper = scope.ServiceProvider.GetRequiredService<IMongoHelper>();
            foreach (var registration in _registrations)
            {
                _logger.LogInformation("Initializing outbox {OutboxIdentity} for collection {CollectionName}",
                                       registration.Options.Identity, registration.Options.CollectionName);
                await registration.GetConfigurator(scope.ServiceProvider).ConfigureAsync(helper, cancellationToken);
            }
        }
    }

    /// <inheritdoc/>
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public async Task StoppingAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Outbox hosted service stopping — stopping outbox processor");
        var stopping = _outboxProcessor is not null
            ? _outboxProcessor.StopAsync(cancellationToken)
            : Task.WhenAll(_registrations.Select(r => RunProcessorAsync(r, false, cancellationToken)));
        try
        {
            await stopping.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Every processor has been signaled; let cleanup finish without extending the host deadline.
            _ = ObserveStopCompletionAsync(stopping);
        }
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
