// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Errors;

/// <summary>
/// Thrown when a duplicate key error occurs on the event <c>_id</c> field,
/// indicating that an event with the same identifier already exists.
/// This is typically an idempotency issue — the caller usually does nothing.
/// </summary>
public sealed class MongoDuplicateEventException : MongoEventStoreException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MongoDuplicateEventException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The optional inner exception.</param>
    public MongoDuplicateEventException(String message, Exception? innerException = null)
        : base(message, innerException) { }
}
