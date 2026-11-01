// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo;

using Chaos.Mongo.Configuration;
using Chaos.Mongo.Migrations;
using Chaos.Mongo.Queues;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Immutable;

/// <summary>
/// Hosted service used for automatic lifecycle management.
/// </summary>
internal class MongoHostedService : IHostedLifecycleService
{
    private readonly ILogger<MongoHostedService> _logger;
    private readonly MongoOptions _options;
    private readonly ImmutableArray<IMongoQueue> _queues;
    private readonly IServiceScopeFactory _serviceScopeFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="MongoHostedService"/> class.
    /// </summary>
    /// <param name="queues">The collection of queues to manage.</param>
    /// <param name="serviceScopeFactory">The service scope factory for resolving scoped user components.</param>
    /// <param name="options">The MongoDB options.</param>
    /// <param name="logger">The logger for diagnostic messages.</param>
    public MongoHostedService(IEnumerable<IMongoQueue> queues,
                              IServiceScopeFactory serviceScopeFactory,
                              IOptions<MongoOptions> options,
                              ILogger<MongoHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(queues);
        ArgumentNullException.ThrowIfNull(serviceScopeFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _queues = [.. queues];
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
        _options = options.Value;
    }

    /// <inheritdoc/>
    public async Task StartedAsync(CancellationToken cancellationToken)
    {
        foreach (var queue in _queues.Where(x => x.QueueDefinition.AutoStartSubscription))
        {
            _logger.LogInformation("Starting subscription for MongoDB queue with payload {Payload}", queue.QueueDefinition.PayloadType.Name);
            await queue.StartSubscriptionAsync(cancellationToken);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Migrations run before configurators, so a configurator cannot create an index before a migration that
    /// cleans up the data it depends on. When migrations are enabled and the migration lock wait times out (see
    /// <see cref="MongoOptions.MigrationLockWaitTimeout"/>), startup fails instead of continuing to configurators
    /// against a potentially stale schema, unless <see cref="MongoOptions.MigrationLockWaitTimeout"/> is
    /// <see cref="TimeSpan.Zero"/> — an explicit, intentionally-unsafe compatibility opt-out.
    /// </remarks>
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        if (_options.ApplyMigrationsOnStartup)
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var migrationRunner = scope.ServiceProvider.GetRequiredService<IMongoMigrationRunner>();

            _logger.LogInformation("Running MongoDB migrations on application startup");
            var result = await migrationRunner.RunMigrationsAsync(cancellationToken);

            if (result.Skipped && _options.MigrationLockWaitTimeout != TimeSpan.Zero)
            {
                throw new InvalidOperationException(
                    $"MongoDB migrations were skipped because the migration lock '{_options.MigrationsLockName}' could " +
                    $"not be acquired within the configured {nameof(MongoOptions.MigrationLockWaitTimeout)} " +
                    $"({_options.MigrationLockWaitTimeout}). Hosted startup cannot continue against a potentially " +
                    "stale schema. Set MigrationLockWaitTimeout to TimeSpan.Zero to opt out of this check.");
            }
        }

        if (_options.RunConfiguratorsOnStartup)
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var configuratorRunner = scope.ServiceProvider.GetRequiredService<IMongoConfiguratorRunner>();

            _logger.LogInformation("Running MongoDB configurators on application startup");
            await configuratorRunner.RunConfiguratorsAsync(cancellationToken);
        }
    }

    /// <inheritdoc/>
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public async Task StoppingAsync(CancellationToken cancellationToken)
    {
        foreach (var queue in _queues.Where(x => x.QueueDefinition.AutoStartSubscription))
        {
            _logger.LogInformation("Stopping subscription for MongoDB queue with payload {Payload}", queue.QueueDefinition.PayloadType.Name);
            await queue.StopSubscriptionAsync(cancellationToken);
        }
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
