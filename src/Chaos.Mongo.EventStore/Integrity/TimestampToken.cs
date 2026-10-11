// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// A validated RFC 3161 timestamp token together with the evidence captured at stamping time.
/// </summary>
internal sealed class TimestampToken
{
    public TimestampToken(Byte[] token,
                          IReadOnlyList<Byte[]> certificateChain,
                          IReadOnlyList<Byte[]> certificateRevocationLists,
                          DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(certificateChain);
        ArgumentNullException.ThrowIfNull(certificateRevocationLists);
        Token = token;
        CertificateChain = certificateChain;
        CertificateRevocationLists = certificateRevocationLists;
        Timestamp = timestamp;
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
    /// Gets the time the authority asserts in the token.
    /// </summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>
    /// Gets the token exactly as the authority returned it: a CMS <c>ContentInfo</c> with the signed <c>TSTInfo</c>.
    /// </summary>
    public Byte[] Token { get; }
}
