// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo;

using MongoDB.Driver;

/// <summary>
/// Default implementation of <see cref="IMongoConnectionFactory"/> that creates MongoDB connections.
/// </summary>
internal class MongoConnectionFactory : IMongoConnectionFactory
{
    private readonly IMongoClientSettingsFactory _clientSettingsFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="MongoConnectionFactory"/> class.
    /// </summary>
    /// <param name="clientSettingsFactory">Factory for creating MongoDB client settings.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="clientSettingsFactory"/> is null.</exception>
    public MongoConnectionFactory(IMongoClientSettingsFactory clientSettingsFactory)
    {
        ArgumentNullException.ThrowIfNull(clientSettingsFactory);
        _clientSettingsFactory = clientSettingsFactory;
    }

    /// <inheritdoc/>
    public IMongoConnection CreateConnection(MongoUrl url, String? databaseName = null)
    {
        ArgumentNullException.ThrowIfNull(url);

        databaseName ??= url.DatabaseName;
        if (String.IsNullOrWhiteSpace(databaseName))
            throw new ArgumentException("URL doesn't contain a database name and the databaseName parameter is also not set");

        var clientSettings = _clientSettingsFactory.CreateMongoClientSettings(url);
        var mongoClient = new MongoClient(clientSettings);
        var mongoDatabase = mongoClient.GetDatabase(databaseName);

        return new MongoConnection(mongoClient, mongoDatabase);
    }
}
