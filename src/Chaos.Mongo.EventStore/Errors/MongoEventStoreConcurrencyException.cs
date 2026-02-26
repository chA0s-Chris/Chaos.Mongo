// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Errors;

/// <summary>
/// Thrown when a duplicate key error occurs on the <c>(AggregateId, Version)</c> compound index,
/// indicating that another process inserted an event for the same aggregate version.
/// The caller should typically retry with a new version.
/// </summary>
public sealed class MongoConcurrencyException : MongoEventStoreException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MongoConcurrencyException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The optional inner exception.</param>
    public MongoConcurrencyException(String message, Exception? innerException = null)
        : base(message, innerException) { }
}
