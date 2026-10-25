// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

/// <summary>
/// Decides which CRLs confirm the distribution points of a timestamp token's chain, by one rule for confirmation
/// during anchoring and for verification, so they cannot diverge.
/// </summary>
/// <remarks>
/// A CRL counts for a point when the point names its issuer, the CRL comes from that issuer, it was issued at or after
/// a given time, it is complete for the point (<see cref="CertificateRevocationListScope"/>), the issuer's certificate
/// is part of the chain and, when it carries a key usage extension, permits <c>cRLSign</c>, and the CRL's signature
/// verifies against it (<see cref="CertificateRevocationListSignature"/>). CRLs are matched with points by content,
/// not by position, so the order an authority returns them in does not matter. Two limitations remain: a certificate
/// whose distribution points include one limited to some reasons keeps its chain unconfirmed, because a CRL of such a
/// point is never complete; and a CRL issued after a certificate expired may no longer list it, as RFC 5280 permits.
/// </remarks>
internal static class RevocationEvidence
{
    /// <summary>
    /// Evaluates CRLs for the points of a chain.
    /// </summary>
    /// <param name="sources">The chain's CRL sources.</param>
    /// <param name="lists">The DER-encoded CRLs.</param>
    /// <param name="chain">The chain's certificates, from the signing certificate to the root.</param>
    /// <param name="issuedNotBefore">The time the CRLs must be issued at or after.</param>
    /// <returns>For each source whether a CRL counts for it, and the entries of every CRL that counts for a source.</returns>
    public static RevocationEvaluation Evaluate(IReadOnlyList<CrlSource> sources,
                                                IReadOnlyList<Byte[]> lists,
                                                X509Certificate2Collection chain,
                                                DateTimeOffset issuedNotBefore)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(lists);
        ArgumentNullException.ThrowIfNull(chain);
        var covered = new Boolean[sources.Count];
        var entries = new List<RevokedCertificate>();
        foreach (var crl in lists)
        {
            var counts = false;
            for (var index = 0; index < sources.Count; index++)
            {
                if (CountsFor(crl, sources[index], chain, issuedNotBefore))
                {
                    covered[index] = true;
                    counts = true;
                }
            }

            if (counts)
            {
                entries.AddRange(RevokedCertificate.DecodeAll(crl));
            }
        }

        return new RevocationEvaluation(covered, entries);
    }

    /// <summary>
    /// Finds the first non-root certificate of a chain that an entry revokes at or before a time.
    /// </summary>
    /// <param name="chain">The chain's certificates, from the signing certificate to the root.</param>
    /// <param name="entries">The entries of CRLs that count.</param>
    /// <param name="time">The token's time.</param>
    /// <returns>The revoked certificate, or <c>null</c> when none is revoked.</returns>
    public static X509Certificate2? FindRevoked(X509Certificate2Collection chain, IReadOnlyList<RevokedCertificate> entries, DateTimeOffset time)
    {
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentNullException.ThrowIfNull(entries);
        return chain.Take(chain.Count - 1).FirstOrDefault(certificate => entries.Any(entry => entry.Revokes(certificate, time)));
    }

    private static Boolean CountsFor(Byte[] crl, CrlSource source, X509Certificate2Collection chain, DateTimeOffset issuedNotBefore)
    {
        // A point that names another CRL issuer is served by an issuer whose certificate is not captured, so its CRL
        // cannot be verified.
        if (source.Issuer is not { } expectedIssuer)
        {
            return false;
        }

        try
        {
            var header = CertificateRevocationListHeader.Decode(crl);
            if (!header.Issuer.Span.SequenceEqual(expectedIssuer) ||
                header.ThisUpdate < issuedNotBefore ||
                !CertificateRevocationListScope.IsCompleteFor(crl, source.Locations, source.FromCaCertificate))
            {
                return false;
            }

            var issuer = chain.FirstOrDefault(certificate => certificate.SubjectName.RawData.AsSpan().SequenceEqual(expectedIssuer));
            return issuer is not null && PermitsCrlSigning(issuer) && CertificateRevocationListSignature.Verify(crl, issuer) is null;
        }
        catch (AsnContentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Gets a value indicating whether a certificate may sign CRLs: it carries no key usage extension, or one that
    /// permits <c>cRLSign</c> (RFC 5280, section 4.2.1.3).
    /// </summary>
    private static Boolean PermitsCrlSigning(X509Certificate2 issuer)
    {
        try
        {
            return issuer.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault() is not { } keyUsage ||
                   (keyUsage.KeyUsages & X509KeyUsageFlags.CrlSign) != 0;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
