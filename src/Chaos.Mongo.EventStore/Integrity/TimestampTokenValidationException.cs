// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// Thrown when a timestamp token fails a check of <see cref="TimestampTokenValidation"/>. The message completes the
/// sentence "the token is invalid because …" in lower case, as the authority's error messages do.
/// </summary>
internal sealed class TimestampTokenValidationException : Exception
{
    public TimestampTokenValidationException(String message, Exception? innerException = null)
        : base(message, innerException) { }
}
