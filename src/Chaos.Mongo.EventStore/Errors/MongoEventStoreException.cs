// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Errors;

/// <summary>
/// Base exception for event store errors.
/// </summary>
public class MongoEventStoreException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MongoEventStoreException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The optional inner exception.</param>
    public MongoEventStoreException(String message, Exception? innerException = null)
        : base(message, innerException) { }
}
