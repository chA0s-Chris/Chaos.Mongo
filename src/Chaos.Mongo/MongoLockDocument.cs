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
    /// Gets or sets the token identifying the acquisition that currently holds the lock. Generated once per
    /// acquisition and never changed by extension, so it distinguishes acquisitions that happen to share the same
    /// <see cref="Holder"/> and <see cref="LeaseUntilUtc"/>. <see langword="null"/> for documents written before
    /// this field existed; such documents are never extended or released, only taken over once their lease expires.
    /// </summary>
    public String? AcquisitionToken { get; internal set; }

    /// <summary>
    /// Gets or sets a counter the holder increments inside its migration transaction to check, on the server and
    /// atomically with the commit, that it still owns the lock. The write has to change the document, because a
    /// write that changes nothing does not conflict with a concurrent change. Only the holder's check writes it;
    /// extension, release and takeover leave it alone. <c>0</c> when the document was written by a version
    /// without this field or the holder has not run the check yet.
    /// </summary>
    public Int64 FenceCounter { get; internal set; }

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
