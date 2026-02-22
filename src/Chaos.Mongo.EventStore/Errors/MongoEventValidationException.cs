// Copyright (c) 2025-2026 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Errors;

/// <summary>
/// Thrown when an event cannot be applied to an aggregate because the aggregate's
/// current state does not permit the operation.
/// </summary>
/// <remarks>
/// This exception should be thrown from <see cref="Event{TAggregate}.Execute"/> when
/// the event's preconditions are not met. For example, an <c>OrderShippedEvent</c> might
/// throw this exception if the order has already been cancelled.
/// </remarks>
public sealed class MongoEventValidationException : MongoEventStoreException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MongoEventValidationException"/> class.
    /// </summary>
    /// <param name="message">The error message describing why the event cannot be applied.</param>
    /// <param name="innerException">The optional inner exception.</param>
    public MongoEventValidationException(String message, Exception? innerException = null)
        : base(message, innerException) { }
}
