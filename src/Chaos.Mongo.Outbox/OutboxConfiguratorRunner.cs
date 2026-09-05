// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox;

using Microsoft.Extensions.Logging;

/// <summary>
/// Runs outbox-specific configurators. Used by <see cref="OutboxHostedService"/>
/// to ensure indexes exist before any processor starts.
/// </summary>
public interface IOutboxConfiguratorRunner
{
    /// <summary>
    /// Runs all outbox configurators.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes when all configurators have run.</returns>
    Task RunAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Default implementation of <see cref="IOutboxConfiguratorRunner"/>.
/// </summary>
public sealed class OutboxConfiguratorRunner : IOutboxConfiguratorRunner
{
    private readonly IEnumerable<OutboxConfigurator> _configurators;
    private readonly ILogger<OutboxConfiguratorRunner> _logger;
    private readonly IMongoHelper _mongoHelper;

    /// <summary>
    /// Initializes a new instance of the <see cref="OutboxConfiguratorRunner"/> class.
    /// </summary>
    /// <param name="mongoHelper">The MongoDB helper.</param>
    /// <param name="configurator">The outbox configurator.</param>
    /// <param name="logger">The logger.</param>
    public OutboxConfiguratorRunner(IMongoHelper mongoHelper,
                                    OutboxConfigurator configurator,
                                    ILogger<OutboxConfiguratorRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(mongoHelper);
        ArgumentNullException.ThrowIfNull(configurator);
        ArgumentNullException.ThrowIfNull(logger);
        _mongoHelper = mongoHelper;
        _configurators = [configurator];
        _logger = logger;
    }

    internal OutboxConfiguratorRunner(IMongoHelper mongoHelper, IEnumerable<OutboxConfigurator> configurators,
                                      ILogger<OutboxConfiguratorRunner> logger)
    {
        _mongoHelper = mongoHelper;
        _configurators = configurators;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        foreach (var configurator in _configurators)
        {
            using var logScope = _logger.BeginScope(new Dictionary<String, Object>
            {
                ["OutboxIdentity"] = configurator.Options.Identity,
                ["CollectionName"] = configurator.Options.CollectionName
            });
            _logger.LogInformation("Running outbox configurator to ensure indexes exist");
            await configurator.ConfigureAsync(_mongoHelper, cancellationToken);
            _logger.LogInformation("Outbox configurator completed");
        }
    }
}
