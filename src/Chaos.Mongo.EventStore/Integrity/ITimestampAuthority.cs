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
    /// Obtains the CRLs of every distribution point a stored certificate chain's non-root certificates name, each
    /// issued no earlier than a given time, to confirm the chain's revocation status after a token's time.
    /// </summary>
    /// <param name="certificateChain">The DER-encoded certificates from the signing certificate to the root.</param>
    /// <param name="issuedNotBefore">The time the CRLs must be issued at or after.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <remarks>
    /// Return one CRL for each distribution point with an HTTP or HTTPS location that the chain's non-root certificates
    /// name, in any order. A CRL confirms a point only when it comes from the point's issuer, is complete for the point
    /// (no delta CRL, no partition by reason or by another point) and its signature verifies against the issuer's
    /// certificate in the chain; otherwise confirmation fails and is retried.
    /// </remarks>
    /// <returns>
    /// The CRLs, an empty list when the chain names no distribution points, or the information that a point's CRL
    /// was issued too early.
    /// </returns>
    /// <exception cref="TimestampAuthorityException">Thrown when a CRL could not be obtained.</exception>
    Task<RevocationLists> GetRevocationListsAsync(IReadOnlyList<Byte[]> certificateChain,
                                                  DateTimeOffset issuedNotBefore,
                                                  CancellationToken cancellationToken);

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
