// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo;

using Microsoft.Extensions.Options;
using MongoDB.Driver;

/// <summary>
/// Default implementation of <see cref="IMongoClientSettingsFactory"/> that creates MongoDB client settings from URLs.
/// </summary>
internal class MongoClientSettingsFactory : IMongoClientSettingsFactory
{
    private readonly MongoOptions? _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="MongoClientSettingsFactory"/> class.
    /// </summary>
    /// <param name="options">Optional MongoDB configuration options that can be used to configure the client settings.</param>
    public MongoClientSettingsFactory(IOptions<MongoOptions>? options)
    {
        _options = options?.Value;
    }

    /// <inheritdoc/>
    public MongoClientSettings CreateMongoClientSettings(MongoUrl url)
    {
        ArgumentNullException.ThrowIfNull(url);
        var settings = MongoClientSettings.FromUrl(url);

        _options?.ConfigureClientSettings?.Invoke(settings);
        return settings;
    }
}
