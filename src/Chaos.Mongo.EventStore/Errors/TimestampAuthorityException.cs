// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Errors;

/// <summary>
/// Thrown when a timestamp authority is unreachable, fails, or returns a token that does not pass validation.
/// </summary>
public sealed class TimestampAuthorityException : MongoEventStoreException
{
    /// <summary>
    /// Creates the exception.
    /// </summary>
    /// <param name="authorityName">The name of the authority that failed.</param>
    /// <param name="message">What failed; the authority's name is prepended.</param>
    /// <param name="innerException">The exception that caused the failure, if any.</param>
    public TimestampAuthorityException(String authorityName, String message, Exception? innerException = null)
        : base($"Timestamp authority '{authorityName}': {message}", innerException)
    {
        AuthorityName = authorityName;
    }

    /// <summary>
    /// Gets the name of the authority that failed.
    /// </summary>
    public String AuthorityName { get; }
}
