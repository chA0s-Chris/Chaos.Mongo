// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

/// <summary>
/// Verifies a timestamp token stored on an anchor, long after it was received.
/// </summary>
/// <remarks>
///     <para>
///     The token passes the same checks as on receipt (<see cref="TimestampTokenValidation"/>), except the nonce, which
///     only binds the token to its request, and the comparison with the local clock, which only applies while stamping.
///     The chain is built at the token's time from the stored certificates without online revocation checks. Its
///     revocation status is recomputed from the stored confirming CRLs instead of read from the stored status, by the
///     rule of <see cref="RevocationEvidence"/>: revoked when a CRL issued after the token's time revokes a certificate
///     of the chain at or before it, confirmed when CRLs issued at least the grace period after the token's time
///     confirm every distribution point. A token whose stored status is confirmed while its stored CRLs do not confirm
///     every distribution point whose issuer is in the chain, or revoked while no stored CRL revokes, is invalid,
///     because its revocation evidence was removed or replaced. A
///     stored status of unconfirmed can be set by anyone with write access, so treat tokens that stay unconfirmed
///     beyond the confirmation delay threshold as suspicious. The token must be DER-encoded.
///     </para>
///     <para>
///     Verification compares the chain it builds with the stored chain. With the system trust store, the chain built
///     years later may take another path, for example through a cross-signed root, so configure the authority's roots
///     explicitly in <see cref="TimestampAuthorityTrust.TrustedRoots"/>.
///     </para>
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
            if (status == TimestampRevocationStatus.Revoked)
            {
                return Invalid(timestamp, status, "a certificate of the token's chain is revoked at or before the token's time.");
            }

            return FindInconsistency(timestamp, storedCertificates, tokenTime) is { } inconsistency
                ? Invalid(timestamp, TimestampRevocationStatus.Unconfirmed, $"the stored revocation evidence is inconsistent: {inconsistency}")
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
    /// Finds why a stored status of confirmed or revoked lacks the evidence it was recorded from. Anchoring records
    /// confirmed only when, for every distribution point whose issuer's certificate is in the chain, a stored CRL from
    /// that issuer is complete for it, verifies, and was issued after the token's time; and revoked only when such a CRL
    /// revokes a certificate of the chain at or before the token's time. A removed, substituted or partially removed CRL
    /// therefore shows. The grace period is not part of the check, so a verification grace period longer than the
    /// anchoring one causes no false report; points of indirect CRL issuers are exempt, because their CRLs cannot be
    /// verified. Revoked tokens are reported before this check, so a stored revoked status reaching it lacks its CRL.
    /// </summary>
    /// <returns>Why the stored evidence is inconsistent, or <c>null</c> when it is consistent.</returns>
    private static String? FindInconsistency(AnchorTimestamp timestamp, X509Certificate2Collection chain, DateTimeOffset tokenTime)
    {
        if (timestamp.RevocationStatus == TimestampRevocationStatus.Revoked)
        {
            return "the stored status is revoked, but no stored CRL issued after the token's time revokes a certificate of its chain.";
        }

        if (timestamp.RevocationStatus != TimestampRevocationStatus.Confirmed)
        {
            return null;
        }

        IReadOnlyList<CrlSource> sources;
        try
        {
            sources = TimestampTokenValidation.GetCrlSources(timestamp.CertificateChain);
        }
        catch (TimestampTokenValidationException)
        {
            return null;
        }

        var evaluation = RevocationEvidence.Evaluate(sources, timestamp.ConfirmingRevocationLists, chain, tokenTime.AddTicks(1));
        return sources.Where((source, index) => source.Issuer is not null && !evaluation.Covered[index]).Any()
            ? "the stored status is confirmed, but confirming CRLs are missing for a distribution point."
            : null;
    }

    private static TimestampVerificationResult Invalid(AnchorTimestamp timestamp, TimestampRevocationStatus status, String failure)
        => new(timestamp.Authority, timestamp.TimestampUtc, false, failure, status, timestamp.RevocationStatus);

    /// <summary>
    /// Recomputes the revocation status of the stored chain at the token's time from the stored confirming CRLs, matched
    /// with the chain's distribution points by content. A CRL issued after the token's time that revokes a certificate
    /// at or before it decides, whatever the grace period and whether other points are confirmed: the grace period
    /// only guards the conclusion that nothing is revoked.
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
        if (RevocationEvidence.FindRevoked(chain, RevocationEvidence.Evaluate(sources, lists, chain, tokenTime.AddTicks(1)).Entries, tokenTime) is not null)
        {
            return TimestampRevocationStatus.Revoked;
        }

        var evaluation = RevocationEvidence.Evaluate(sources, lists, chain, tokenTime + gracePeriod);
        var confirmed = sources.Count > 0 ? evaluation.Covered.All(covered => covered) : lists.Count == 0;
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
                TimestampTokenValidation.RequireDer(timestamp.Token);
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
