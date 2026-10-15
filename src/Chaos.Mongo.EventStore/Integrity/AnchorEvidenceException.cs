// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using Chaos.Mongo.EventStore.Errors;

/// <summary>
/// Thrown when evidence an anchor references cannot be resolved because it is missing or does not match its hash.
/// </summary>
internal sealed class AnchorEvidenceException : MongoEventStoreException
{
    public AnchorEvidenceException(Byte[] hash, String message, Exception? innerException = null)
        : base($"Anchor evidence {Convert.ToHexString(hash)} cannot be resolved: {message}", innerException)
    {
        Hash = hash;
    }

    /// <summary>
    /// Gets the SHA-256 of the evidence that cannot be resolved.
    /// </summary>
    public Byte[] Hash { get; }
}
