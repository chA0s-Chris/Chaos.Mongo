// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using System.Security.Cryptography;

/// <summary>
/// Obtains RFC 3161 timestamp tokens for digests.
/// </summary>
/// <remarks>
/// Implementations for APIs other than RFC 3161 over HTTP still return an RFC 3161 token, so every token can be
/// verified the same way.
/// </remarks>
internal interface ITimestampAuthority
{
    /// <summary>
    /// Gets the name that identifies the authority in stored anchors.
    /// </summary>
    String Name { get; }

    /// <summary>
    /// Obtains and validates a timestamp token for a digest.
    /// </summary>
    /// <param name="digest">The digest to timestamp.</param>
    /// <param name="algorithm">The hash algorithm of the digest; only SHA-256 is accepted.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The validated token with the evidence captured at stamping time.</returns>
    /// <exception cref="ArgumentException">Thrown when the algorithm is not SHA-256 or the digest has the wrong length.</exception>
    /// <exception cref="TimestampAuthorityException">Thrown when no valid token could be obtained.</exception>
    Task<TimestampToken> TimestampAsync(ReadOnlyMemory<Byte> digest, HashAlgorithmName algorithm, CancellationToken cancellationToken);
}
