// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo;

using MongoDB.Driver;

/// <summary>
/// Factory for creating <see cref="IMongoConnection"/> instances.
/// </summary>
public interface IMongoConnectionFactory
{
    /// <summary>
    /// Creates a MongoDB connection from the specified URL.
    /// </summary>
    /// <param name="url">The MongoDB connection URL.</param>
    /// <param name="databaseName">Optional database name. If not specified, the database name from the URL is used.</param>
    /// <returns>A configured MongoDB connection instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="url"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the URL doesn't contain a database name and
    /// <paramref name="databaseName"/> is not provided.
    /// </exception>
    IMongoConnection CreateConnection(MongoUrl url, String? databaseName = null);
}
