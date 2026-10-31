// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Errors;

/// <summary>
/// Thrown when an append cannot verify or create the unique concurrency index on <c>(AggregateId, Version)</c>,
/// either because the connection lacks permission to create indexes, or because an index already exists under
/// that name with different keys or options. Appends are unsafe without this exact index.
/// </summary>
public sealed class MongoConcurrencyIndexMissingException : MongoEventStoreException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MongoConcurrencyIndexMissingException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The optional inner exception.</param>
    public MongoConcurrencyIndexMissingException(String message, Exception? innerException = null)
        : base(message, innerException) { }
}
