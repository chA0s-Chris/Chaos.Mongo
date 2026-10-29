// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// A validated RFC 3161 timestamp token together with the evidence captured at stamping time.
/// </summary>
public sealed class TimestampToken
{
    /// <summary>
    /// Creates a token, as a custom <see cref="ITimestampAuthority"/> returns it after validating it.
    /// </summary>
    /// <param name="token">The token exactly as the authority returned it.</param>
    /// <param name="certificateChain">
    /// The DER-encoded certificates of the validated chain, from the signing certificate to the
    /// root.
    /// </param>
    /// <param name="certificateRevocationLists">
    /// The CRLs downloaded from the distribution points of the chain's non-root
    /// certificates.
    /// </param>
    /// <param name="timestampUtc">
    /// The time the authority asserts in the token. Its <see cref="DateTime.Kind"/> must be
    /// <see cref="DateTimeKind.Utc"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when an argument is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="timestampUtc"/>'s <see cref="DateTime.Kind"/> is not <see cref="DateTimeKind.Utc"/>.
    /// </exception>
    public TimestampToken(Byte[] token,
                          IReadOnlyList<Byte[]> certificateChain,
                          IReadOnlyList<Byte[]> certificateRevocationLists,
                          DateTime timestampUtc)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(certificateChain);
        ArgumentNullException.ThrowIfNull(certificateRevocationLists);
        if (timestampUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException($"{nameof(timestampUtc)} must have {nameof(DateTimeKind.Utc)} kind.", nameof(timestampUtc));
        }

        Token = token;
        CertificateChain = certificateChain;
        CertificateRevocationLists = certificateRevocationLists;
        TimestampUtc = timestampUtc;
    }

    /// <summary>
    /// Gets the DER-encoded certificates of the validated chain, from the signing certificate to the root.
    /// </summary>
    public IReadOnlyList<Byte[]> CertificateChain { get; }

    /// <summary>
    /// Gets the CRLs downloaded from the distribution points of the chain's non-root certificates. They are stored
    /// as evidence and are not verified when captured.
    /// </summary>
    public IReadOnlyList<Byte[]> CertificateRevocationLists { get; }

    /// <summary>
    /// Gets the time the authority asserts in the token. Its <see cref="DateTime.Kind"/> is
    /// <see cref="DateTimeKind.Utc"/>.
    /// </summary>
    public DateTime TimestampUtc { get; }

    /// <summary>
    /// Gets the token exactly as the authority returned it: a CMS <c>ContentInfo</c> with the signed <c>TSTInfo</c>.
    /// </summary>
    public Byte[] Token { get; }
}
