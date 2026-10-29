// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo;

using MongoDB.Bson.Serialization.Attributes;

/// <summary>
/// Represents a lock document stored in MongoDB for distributed locking.
/// </summary>
[BsonIgnoreExtraElements]
internal class MongoLockDocument
{
    /// <summary>
    /// Gets or sets the holder ID that currently owns the lock.
    /// </summary>
    public String? Holder { get; internal set; }

    /// <summary>
    /// Gets or sets the unique identifier of the lock.
    /// </summary>
    [BsonId]
    public required String Id { get; internal set; }

    /// <summary>
    /// Gets or sets the UTC date and time when the lock lease will expire.
    /// </summary>
    public DateTime LeaseUntilUtc { get; internal set; }
}
