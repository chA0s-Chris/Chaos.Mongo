// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Configuration;

using Microsoft.Extensions.Logging;

/// <inheritdoc/>
public class MongoConfiguratorRunner : IMongoConfiguratorRunner
{
    private readonly List<IMongoConfigurator> _configurators;
    private readonly ILogger<MongoConfiguratorRunner> _logger;
    private readonly IMongoHelper _mongoHelper;

    /// <summary>
    /// Initializes a new instance of the <see cref="MongoConfiguratorRunner"/> class.
    /// </summary>
    /// <param name="mongoHelper">The MongoDB helper instance.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="configurators">The collection of configurators to run.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="mongoHelper"/>, <paramref name="logger"/>, or
    /// <paramref name="configurators"/> is null.
    /// </exception>
    public MongoConfiguratorRunner(IMongoHelper mongoHelper,
                                   ILogger<MongoConfiguratorRunner> logger,
                                   IEnumerable<IMongoConfigurator> configurators)
    {
        ArgumentNullException.ThrowIfNull(mongoHelper);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(configurators);
        _mongoHelper = mongoHelper;
        _logger = logger;
        _configurators = configurators.ToList();
    }

    /// <inheritdoc/>
    public async Task RunConfiguratorsAsync(CancellationToken cancellationToken = default)
    {
        if (_configurators.Count == 0)
        {
            _logger.LogInformation("No MongoDB configurators registered");
            return;
        }

        _logger.LogInformation("Found MongoDB configurators: {Count}", _configurators.Count);

        foreach (var configurator in _configurators)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogInformation("Running MongoDB configurator: {Type}", configurator.GetType().FullName);
            await configurator.ConfigureAsync(_mongoHelper, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("Finished running MongoDB configurators");
    }
}
