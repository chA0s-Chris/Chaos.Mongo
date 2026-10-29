// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo;

using MongoDB.Driver;

/// <summary>
/// Default implementation of <see cref="IMongoConnection"/> that holds references to a MongoDB client and database.
/// </summary>
public sealed class MongoConnection : IMongoConnection
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MongoConnection"/> class.
    /// </summary>
    /// <param name="client">The MongoDB client instance.</param>
    /// <param name="database">The MongoDB database instance.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="client"/> or <paramref name="database"/> is null.</exception>
    public MongoConnection(IMongoClient client, IMongoDatabase database)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(database);
        Client = client;
        Database = database;
    }

    /// <inheritdoc/>
    public IMongoClient Client { get; }

    /// <inheritdoc/>
    public IMongoDatabase Database { get; }
}
