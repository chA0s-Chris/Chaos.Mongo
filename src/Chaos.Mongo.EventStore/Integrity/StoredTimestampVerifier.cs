// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

/// <summary>
/// Verifies a timestamp token stored on an anchor, long after it was received.
/// </summary>
/// <remarks>
/// The token passes the same checks as on receipt (<see cref="TimestampTokenValidation"/>), except the nonce, which
/// only binds the token to its request, and the comparison with the local clock, which only applies while stamping.
/// The chain is built at the token's time from the stored certificates without online revocation checks. Its revocation
/// status is recomputed from the stored confirming CRLs instead of read from the stored status: each counts only for the
/// distribution point it was captured for, when its signature verifies against its issuer's certificate in the stored
/// chain and it was issued at least the grace period after the token's time.
/// </remarks>
internal static class StoredTimestampVerifier
{
    /// <summary>
    /// Verifies a stored token against the anchor digest it must cover.
    /// </summary>
    /// <param name="timestamp">The stored token with its resolved evidence.</param>
    /// <param name="anchorDigest">The anchor digest the token must cover.</param>
    /// <param name="trust">The trust settings of the token's authority, or <c>null</c> when none are configured.</param>
    /// <param name="gracePeriod">The time after the token's time that confirming CRLs must be issued at or after.</param>
    /// <returns>The result.</returns>
    public static TimestampVerificationResult Verify(AnchorTimestamp timestamp,
                                                     ReadOnlySpan<Byte> anchorDigest,
                                                     TimestampAuthorityTrust? trust,
                                                     TimeSpan gracePeriod)
    {
        ArgumentNullException.ThrowIfNull(timestamp);
        if (trust is null)
        {
            return Invalid(timestamp, TimestampRevocationStatus.Unconfirmed, $"no trust settings are configured for timestamp authority '{timestamp.Authority}'.");
        }

        if (!Rfc3161TimestampToken.TryDecode(timestamp.Token, out var token, out var bytesConsumed) || bytesConsumed != timestamp.Token.Length)
        {
            return Invalid(timestamp, TimestampRevocationStatus.Unconfirmed, "the stored token is not a single RFC 3161 token.");
        }

        var storedCertificates = new X509Certificate2Collection();
        try
        {
            foreach (var certificate in timestamp.CertificateChain)
            {
                storedCertificates.Add(X509CertificateLoader.LoadCertificate(certificate));
            }

            var failure = VerifyToken(token, timestamp, anchorDigest, trust, storedCertificates);
            if (failure is not null)
            {
                return Invalid(timestamp, TimestampRevocationStatus.Unconfirmed, failure);
            }

            var tokenTime = token.TokenInfo.Timestamp;
            var status = RecomputeRevocationStatus(timestamp, storedCertificates, tokenTime, gracePeriod);
            return status == TimestampRevocationStatus.Revoked
                ? Invalid(timestamp, status, "a certificate of the token's chain is revoked at or before the token's time.")
                : new TimestampVerificationResult(timestamp.Authority, timestamp.TimestampUtc, true, null, status, timestamp.RevocationStatus);
        }
        catch (CryptographicException)
        {
            return Invalid(timestamp, TimestampRevocationStatus.Unconfirmed, "a certificate of the stored chain is malformed.");
        }
        finally
        {
            foreach (var certificate in storedCertificates)
            {
                certificate.Dispose();
            }
        }
    }

    /// <summary>
    /// Checks a confirming CRL against the distribution point it was captured for and adds its entries when it counts.
    /// </summary>
    /// <returns><c>true</c> when the CRL counts as confirmation for the point.</returns>
    private static Boolean CheckConfirmingCrl(Byte[] crl,
                                              CrlSource source,
                                              X509Certificate2Collection chain,
                                              DateTimeOffset issuedNotBefore,
                                              List<RevokedCertificate> entries)
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
            if (!header.Issuer.Span.SequenceEqual(expectedIssuer) || header.ThisUpdate < issuedNotBefore)
            {
                return false;
            }

            var issuer = chain.FirstOrDefault(certificate => certificate.SubjectName.RawData.AsSpan().SequenceEqual(expectedIssuer));
            if (issuer is null || CertificateRevocationListSignature.Verify(crl, issuer) is not null)
            {
                return false;
            }

            entries.AddRange(RevokedCertificate.DecodeAll(crl));
            return true;
        }
        catch (AsnContentException)
        {
            return false;
        }
    }

    private static TimestampVerificationResult Invalid(AnchorTimestamp timestamp, TimestampRevocationStatus status, String failure)
        => new(timestamp.Authority, timestamp.TimestampUtc, false, failure, status, timestamp.RevocationStatus);

    /// <summary>
    /// Recomputes the revocation status of the stored chain at the token's time from the stored confirming CRLs, which
    /// are stored in the order of the chain's distribution points, as they were captured.
    /// </summary>
    private static TimestampRevocationStatus RecomputeRevocationStatus(AnchorTimestamp timestamp,
                                                                       X509Certificate2Collection chain,
                                                                       DateTimeOffset tokenTime,
                                                                       TimeSpan gracePeriod)
    {
        IReadOnlyList<CrlSource> sources;
        try
        {
            sources = TimestampTokenValidation.GetCrlSources(timestamp.CertificateChain);
        }
        catch (TimestampTokenValidationException)
        {
            return TimestampRevocationStatus.Unconfirmed;
        }

        var lists = timestamp.ConfirmingRevocationLists;
        var confirmed = sources.Count == lists.Count;
        var entries = new List<RevokedCertificate>();
        for (var index = 0; index < Math.Min(sources.Count, lists.Count); index++)
        {
            confirmed &= CheckConfirmingCrl(lists[index], sources[index], chain, tokenTime + gracePeriod, entries);
        }

        // A verified CRL that revokes a certificate of the chain decides, even when other points are unconfirmed.
        var nonRootCertificates = chain.Take(chain.Count - 1);
        if (nonRootCertificates.Any(certificate => entries.Any(entry => entry.Revokes(certificate, tokenTime))))
        {
            return TimestampRevocationStatus.Revoked;
        }

        return confirmed ? TimestampRevocationStatus.Confirmed : TimestampRevocationStatus.Unconfirmed;
    }

    /// <summary>
    /// Runs the receipt-time checks on the stored token and its chain.
    /// </summary>
    /// <returns><c>null</c> when the token passes; otherwise why it does not.</returns>
    private static String? VerifyToken(Rfc3161TimestampToken token,
                                       AnchorTimestamp timestamp,
                                       ReadOnlySpan<Byte> anchorDigest,
                                       TimestampAuthorityTrust trust,
                                       X509Certificate2Collection storedCertificates)
    {
        if (!token.VerifySignatureForHash(anchorDigest, HashAlgorithmName.SHA256, out var signer, storedCertificates))
        {
            return "the token's imprint does not match the anchor digest, or its signature or signing-certificate attribute is invalid.";
        }

        using (signer)
        {
            // BSON dates keep milliseconds, so the stored time is the token's time truncated to milliseconds.
            var tokenTicks = token.TokenInfo.Timestamp.UtcDateTime.Ticks;
            if (tokenTicks - (tokenTicks % TimeSpan.TicksPerMillisecond) != timestamp.TimestampUtc.ToUniversalTime().Ticks)
            {
                return $"the token's time {token.TokenInfo.Timestamp:O} differs from the stored time {timestamp.TimestampUtc:O}.";
            }

            try
            {
                TimestampTokenValidation.ValidateToken(token, signer, trust.PolicyId is { } policyId ? new Oid(policyId) : null);
                var chain = TimestampTokenValidation.BuildChain(token,
                                                                signer,
                                                                new ChainSettings(trust.TrustedRoots,
                                                                                  X509RevocationMode.NoCheck,
                                                                                  TimeSpan.Zero,
                                                                                  storedCertificates));
                var matchesStoredChain = chain.Certificates.Count == timestamp.CertificateChain.Count &&
                                         chain.Certificates.Zip(timestamp.CertificateChain).All(pair => pair.First.AsSpan().SequenceEqual(pair.Second));
                return matchesStoredChain ? null : "the stored certificate chain differs from the token's validated chain.";
            }
            catch (TimestampTokenValidationException ex)
            {
                return ex.Message;
            }
        }
    }
}
