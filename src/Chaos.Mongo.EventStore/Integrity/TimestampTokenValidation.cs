// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

/// <summary>
/// The checks of an RFC 3161 timestamp token that hold both when it is received and when it is verified later, so
/// receipt and verification cannot diverge.
/// </summary>
/// <remarks>
/// Imprint, signature and ESSCertID are validated by the <c>System.Security.Cryptography.Pkcs</c> types, the
/// certificate chain by <see cref="X509Chain"/>. These checks add what the Pkcs types leave open: the signing
/// certificate must be reserved for timestamping and permit signatures, a <c>tsa</c> name in the token must correspond
/// to one of its subject names, and the token must carry no signature besides the authority's, be signed with a
/// collision-resistant digest and a key of adequate size, and be issued under the required policy.
/// </remarks>
internal static class TimestampTokenValidation
{
    /// <summary>
    /// The object identifier of the <c>id-kp-timeStamping</c> extended key usage.
    /// </summary>
    public const String TimeStampingPurposeOid = "1.3.6.1.5.5.7.3.8";

    private const String CounterSignatureOid = "1.2.840.113549.1.9.6";
    private const String EcPublicKeyOid = "1.2.840.10045.2.1";

    /// <summary>
    /// The deepest DER nesting a token may have, far beyond real tokens; the specification states the same bound.
    /// </summary>
    private const Int32 MaximumDerDepth = 64;

    private const Int32 MinimumEcKeySize = 256;
    private const Int32 MinimumRsaKeySize = 2048;
    private const String RsaPssPublicKeyOid = "1.2.840.113549.1.1.10";
    private const String RsaPublicKeyOid = "1.2.840.113549.1.1.1";

    /// <summary>
    /// MD2, MD4, MD5 and SHA-1, which are not collision resistant.
    /// </summary>
    private static readonly HashSet<String> WeakDigestAlgorithmOids =
    [
        "1.2.840.113549.2.2",
        "1.2.840.113549.2.4",
        "1.2.840.113549.2.5",
        "1.3.14.3.2.26"
    ];

    /// <summary>
    /// Signature algorithms that name one of <see cref="WeakDigestAlgorithmOids"/>: RSA with MD2, MD4, MD5 and SHA-1,
    /// ECDSA with SHA-1 and DSA with SHA-1.
    /// </summary>
    private static readonly HashSet<String> WeakSignatureAlgorithmOids =
    [
        "1.2.840.113549.1.1.2",
        "1.2.840.113549.1.1.3",
        "1.2.840.113549.1.1.4",
        "1.2.840.113549.1.1.5",
        "1.3.14.3.2.29",
        "1.2.840.10045.4.1",
        "1.2.840.10040.4.3"
    ];

    /// <summary>
    /// Builds the token's certificate chain at the token's time and gets the CRLs the distribution points of its
    /// non-root certificates name.
    /// </summary>
    /// <param name="token">The token.</param>
    /// <param name="signer">The signing certificate.</param>
    /// <param name="settings">The trust settings.</param>
    /// <returns>The DER-encoded certificates from the signing certificate to the root, and their CRL sources.</returns>
    /// <exception cref="TimestampTokenValidationException">Thrown when the chain is not trusted or malformed.</exception>
    public static ValidatedChain BuildChain(Rfc3161TimestampToken token, X509Certificate2 signer, ChainSettings settings)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(settings);

        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = settings.RevocationMode;
        chain.ChainPolicy.UrlRetrievalTimeout = settings.RevocationCheckTimeout;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(TimeStampingPurposeOid));
        chain.ChainPolicy.VerificationTime = token.TokenInfo.Timestamp.UtcDateTime;
        var tokenCertificates = token.AsSignedCms().Certificates;
        chain.ChainPolicy.ExtraStore.AddRange(tokenCertificates);
        chain.ChainPolicy.ExtraStore.AddRange(settings.ExtraCertificates);
        if (settings.TrustedRoots.Count > 0)
        {
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.AddRange(settings.TrustedRoots);
        }

        var elements = new List<X509Certificate2>();
        try
        {
            if (!chain.Build(signer))
            {
                var statuses = String.Join(", ", chain.ChainStatus.Select(status => status.Status));
                throw new TimestampTokenValidationException($"the token's certificate chain is not trusted ({statuses}).");
            }

            // Everything needed later is copied out, so the certificates can be disposed here.
            elements.AddRange(chain.ChainElements.Select(element => element.Certificate));
            var crlSources = GetCrlSources(elements.Take(elements.Count - 1));
            return new ValidatedChain(elements.Select(certificate => certificate.RawData).ToList(), crlSources);
        }
        catch (AsnContentException ex)
        {
            throw new TimestampTokenValidationException("a certificate of the token's chain has a malformed CRL distribution point.", ex);
        }
        finally
        {
            foreach (var certificate in elements.Concat(tokenCertificates))
            {
                certificate.Dispose();
            }
        }
    }

    /// <summary>
    /// Gets the distinct CRLs the distribution points of certificates name, in the order they are captured. Sources with
    /// the same normalized locations and issuer are listed once, and count as named by a CA certificate when any of
    /// their certificates is one.
    /// </summary>
    /// <param name="certificates">The certificates, usually the non-root certificates of a chain.</param>
    /// <returns>The CRL sources.</returns>
    /// <exception cref="TimestampTokenValidationException">
    /// Thrown when a certificate names distribution points, but none with an HTTP location covers all reasons.
    /// </exception>
    /// <exception cref="AsnContentException">Thrown when a distribution point extension is malformed.</exception>
    public static IReadOnlyList<CrlSource> GetCrlSources(IEnumerable<X509Certificate2> certificates)
        => certificates.SelectMany(GetCrlSources)
                       .GroupBy(source => (String.Join(" ", source.Locations.Select(uri => CrlLocation.Normalize(uri.OriginalString) ?? uri.AbsoluteUri)),
                                           Convert.ToHexString(source.Issuer ?? [])))
                       .Select(group => group.First() with
                       {
                           FromCaCertificate = group.Any(source => source.FromCaCertificate),
                           FromEndEntityCertificate = group.Any(source => source.FromEndEntityCertificate)
                       })
                       .ToList();

    /// <summary>
    /// Gets the distinct CRLs the distribution points of a stored chain's non-root certificates name, in the order they
    /// are captured.
    /// </summary>
    /// <param name="certificateChain">The DER-encoded certificates from the signing certificate to the root.</param>
    /// <returns>The CRL sources.</returns>
    /// <exception cref="TimestampTokenValidationException">Thrown when a certificate or its distribution points are malformed.</exception>
    public static IReadOnlyList<CrlSource> GetCrlSources(IReadOnlyList<Byte[]> certificateChain)
    {
        ArgumentNullException.ThrowIfNull(certificateChain);
        var certificates = new List<X509Certificate2>();
        try
        {
            foreach (var certificate in certificateChain.Take(certificateChain.Count - 1))
            {
                certificates.Add(X509CertificateLoader.LoadCertificate(certificate));
            }

            return GetCrlSources(certificates);
        }
        catch (CryptographicException ex)
        {
            throw new TimestampTokenValidationException("a certificate of the stored chain is malformed.", ex);
        }
        catch (AsnContentException ex)
        {
            throw new TimestampTokenValidationException("a certificate of the stored chain has a malformed CRL distribution point.", ex);
        }
        finally
        {
            foreach (var certificate in certificates)
            {
                certificate.Dispose();
            }
        }
    }

    /// <summary>
    /// Gets the size of an RSA or ECDSA key and the minimum required for it; other key types are not size-checked. The
    /// size is <c>null</c> when the platform cannot read the key, as for RSASSA-PSS keys today.
    /// </summary>
    /// <param name="certificate">The certificate holding the key.</param>
    /// <returns>The key size and the required minimum.</returns>
    public static (Int32? KeySize, Int32 Minimum) GetKeySize(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        switch (certificate.PublicKey.Oid.Value)
        {
            case RsaPublicKeyOid:
            case RsaPssPublicKeyOid:
                using (var key = certificate.GetRSAPublicKey())
                {
                    return (key?.KeySize, MinimumRsaKeySize);
                }
            case EcPublicKeyOid:
                using (var key = certificate.GetECDsaPublicKey())
                {
                    return (key?.KeySize, MinimumEcKeySize);
                }
            default:
                return (null, 0);
        }
    }

    /// <summary>
    /// Gets a value indicating whether a signature algorithm names a digest that is not collision resistant.
    /// </summary>
    /// <param name="signatureAlgorithmOid">The object identifier of the signature algorithm.</param>
    /// <returns><c>true</c> for RSA with MD2, MD4, MD5 or SHA-1, ECDSA with SHA-1 and DSA with SHA-1.</returns>
    public static Boolean IsWeakSignatureAlgorithm(String signatureAlgorithmOid)
        => WeakSignatureAlgorithmOids.Contains(signatureAlgorithmOid);

    /// <summary>
    /// Requires a token to be exactly one DER value: definite, minimal lengths throughout, no constructed encoding of a
    /// string type and no constructed value nested 64 or more levels deep, also inside the signed <c>TSTInfo</c>. The
    /// CMS decoder accepts BER, which an independent verification of the stored bytes may not.
    /// </summary>
    /// <param name="token">The encoded token.</param>
    /// <exception cref="TimestampTokenValidationException">Thrown when the token is not DER.</exception>
    public static void RequireDer(ReadOnlyMemory<Byte> token)
    {
        try
        {
            var reader = new AsnReader(token, AsnEncodingRules.DER);
            RequireDerValue(reader.ReadEncodedValue());
            reader.ThrowIfNotEmpty();

            // The TSTInfo is the content of an OCTET STRING, which the walk does not descend into.
            if (Rfc3161TimestampToken.TryDecode(token, out var decoded, out _))
            {
                var content = decoded.AsSignedCms().ContentInfo.Content;
                var contentReader = new AsnReader(content, AsnEncodingRules.DER);
                RequireDerValue(contentReader.ReadEncodedValue());
                contentReader.ThrowIfNotEmpty();
            }
        }
        catch (AsnContentException ex)
        {
            throw new TimestampTokenValidationException("the token is not DER-encoded.", ex);
        }
    }

    /// <summary>
    /// Requires the token to be signed with a collision-resistant digest and the signing key to have at least 2048 bits
    /// for RSA or 256 bits for ECDSA, so a token is not weak evidence from the day it is issued (ETSI TS 119 312).
    /// The Pkcs types accept any algorithm. The signing-certificate identifier may still use SHA-1 (ESSCertID), which
    /// only names the certificate.
    /// </summary>
    /// <param name="token">The token.</param>
    /// <param name="signer">The signing certificate.</param>
    /// <exception cref="TimestampTokenValidationException">Thrown when the check fails.</exception>
    public static void ValidateAlgorithms(Rfc3161TimestampToken token, X509Certificate2 signer)
    {
        var signerInfo = token.AsSignedCms().SignerInfos[0];
        if (WeakDigestAlgorithmOids.Contains(signerInfo.DigestAlgorithm.Value!) ||
            WeakSignatureAlgorithmOids.Contains(signerInfo.SignatureAlgorithm.Value!))
        {
            throw new TimestampTokenValidationException(
                $"the token is signed with a weak algorithm (digest {signerInfo.DigestAlgorithm.Value}, signature " +
                $"{signerInfo.SignatureAlgorithm.Value}); MD5 and SHA-1 are not accepted.");
        }

        var (keySize, minimum) = GetKeySize(signer);
        if (keySize is null && minimum > 0)
        {
            throw new TimestampTokenValidationException("the signing key's size could not be determined.");
        }

        if (keySize < minimum)
        {
            throw new TimestampTokenValidationException($"the signing key has {keySize} bits, fewer than the required {minimum}.");
        }
    }

    /// <summary>
    /// Requires a <c>tsa</c> name in the token to correspond to a subject name of the signing certificate
    /// (RFC 3161, section 2.4.2); a token without one is accepted.
    /// </summary>
    /// <param name="token">The token.</param>
    /// <param name="signer">The signing certificate.</param>
    /// <exception cref="TimestampTokenValidationException">Thrown when the check fails.</exception>
    public static void ValidateAuthorityName(Rfc3161TimestampToken token, X509Certificate2 signer)
    {
        if (token.TokenInfo.GetTimestampAuthorityName() is not { } authorityName)
        {
            return;
        }

        try
        {
            if (!TimestampAuthorityName.Matches(authorityName, signer))
            {
                throw new TimestampTokenValidationException("the token's TSA name does not correspond to a subject name of the signing certificate.");
            }
        }
        catch (AsnContentException ex)
        {
            throw new TimestampTokenValidationException("the token's TSA name is malformed.", ex);
        }
    }

    /// <summary>
    /// Requires the signed content to be exactly one DER <c>TSTInfo</c> whose message imprint names its hash algorithm
    /// without parameters or with <c>NULL</c> ones, and the token to carry no signature besides the authority's (RFC
    /// 3161, section 2.4.2). The Pkcs decoder does not reject data that follows the <c>TSTInfo</c>, although the
    /// signature covers that data, its imprint check compares only the algorithm's identifier, and it requires one
    /// signer but ignores countersignatures.
    /// </summary>
    /// <param name="token">The token.</param>
    /// <exception cref="TimestampTokenValidationException">Thrown when the check fails.</exception>
    public static void ValidateContent(Rfc3161TimestampToken token)
    {
        var signedCms = token.AsSignedCms();
        var content = signedCms.ContentInfo.Content;
        if (!Rfc3161TimestampTokenInfo.TryDecode(content, out _, out var bytesConsumed) || bytesConsumed != content.Length)
        {
            throw new TimestampTokenValidationException("the token's signed content is not exactly one TSTInfo.");
        }

        if (!HasImprintAlgorithmWithoutParameters(content))
        {
            throw new TimestampTokenValidationException("the token's message imprint names its hash algorithm with parameters other than NULL.");
        }

        // A countersignature attribute counts whatever values it holds, as an independent verifier sees it.
        if (signedCms.SignerInfos[0].UnsignedAttributes.Cast<CryptographicAttributeObject>().Any(attribute => attribute.Oid.Value == CounterSignatureOid))
        {
            throw new TimestampTokenValidationException("the token carries a countersignature besides the authority's signature.");
        }
    }

    /// <summary>
    /// Requires the token to be issued under the required policy (RFC 3161, section 2.2). The Pkcs types do not
    /// compare the token's policy with the requested one.
    /// </summary>
    /// <param name="token">The token.</param>
    /// <param name="policyId">The policy the token must be issued under, or <c>null</c> to accept any.</param>
    /// <exception cref="TimestampTokenValidationException">Thrown when the check fails.</exception>
    public static void ValidatePolicy(Rfc3161TimestampToken token, Oid? policyId)
    {
        if (policyId is not null && token.TokenInfo.PolicyId.Value != policyId.Value)
        {
            throw new TimestampTokenValidationException(
                $"the token is issued under policy {token.TokenInfo.PolicyId.Value}, not the required policy {policyId.Value}.");
        }
    }

    /// <summary>
    /// Runs the checks that need no trust settings on a token an authority returned, before anchoring stores it, so a
    /// custom authority cannot complete an anchor with a token that verification rejects anyway: it is one DER-encoded
    /// RFC 3161 token whose imprint is the SHA-256 digest and whose signature verifies with the returned chain, which
    /// starts with the signing certificate; its time is the returned time; and its content, signer and algorithms pass
    /// the checks verification applies. The chain's trust and the policy are checked at verification, which knows the
    /// trust settings.
    /// </summary>
    /// <param name="received">The token as the authority returned it.</param>
    /// <param name="digest">The SHA-256 digest the token must cover.</param>
    /// <exception cref="TimestampTokenValidationException">Thrown when the token fails a check.</exception>
    public static void ValidateReceived(TimestampToken received, ReadOnlySpan<Byte> digest)
    {
        ArgumentNullException.ThrowIfNull(received);
        if (!Rfc3161TimestampToken.TryDecode(received.Token, out var token, out var bytesConsumed) || bytesConsumed != received.Token.Length)
        {
            throw new TimestampTokenValidationException("the token is not a single RFC 3161 token.");
        }

        var chain = new X509Certificate2Collection();
        try
        {
            foreach (var certificate in received.CertificateChain)
            {
                chain.Add(X509CertificateLoader.LoadCertificate(certificate));
            }

            if (chain.Count == 0)
            {
                throw new TimestampTokenValidationException("the token comes without its certificate chain.");
            }

            if (!token.VerifySignatureForHash(digest, HashAlgorithmName.SHA256, out var signer, chain))
            {
                throw new TimestampTokenValidationException(
                    "the token's imprint does not match the digest, or its signature or signing-certificate attribute is invalid.");
            }

            using (signer)
            {
                if (!signer.RawData.AsSpan().SequenceEqual(received.CertificateChain[0]))
                {
                    throw new TimestampTokenValidationException("the token's certificate chain does not start with its signing certificate.");
                }

                if (token.TokenInfo.Timestamp != received.Timestamp)
                {
                    throw new TimestampTokenValidationException("the token's time differs from the time returned with it.");
                }

                ValidateContent(token);
                RequireDer(received.Token);
                ValidateSigner(signer);
                ValidateAlgorithms(token, signer);
                ValidateAuthorityName(token, signer);
            }
        }
        catch (CryptographicException ex)
        {
            throw new TimestampTokenValidationException("a certificate of the token's chain is malformed.", ex);
        }
        finally
        {
            foreach (var certificate in chain)
            {
                certificate.Dispose();
            }
        }
    }

    /// <summary>
    /// Requires the signing certificate to be reserved for timestamping by a critical extended key usage extension
    /// (RFC 3161, section 2.3) and, when it carries a key usage extension, to permit digitalSignature or
    /// nonRepudiation and nothing else, the rule OpenSSL applies, so an independent verification agrees. Both extensions
    /// are decoded on first access, so an undecodable one fails the check here. A certificate must not carry an
    /// extension more than once (RFC 5280, section 4.2); the extension accessors would read only one instance.
    /// </summary>
    /// <param name="signer">The signing certificate.</param>
    /// <exception cref="TimestampTokenValidationException">Thrown when the check fails.</exception>
    public static void ValidateSigner(X509Certificate2 signer)
    {
        var duplicate = signer.Extensions.GroupBy(extension => extension.Oid?.Value)
                              .FirstOrDefault(extensions => extensions.Count() > 1);
        if (duplicate is not null)
        {
            throw new TimestampTokenValidationException($"the signing certificate carries the extension {duplicate.Key} more than once.");
        }

        List<String?> purposes;
        X509KeyUsageFlags? keyUsages;
        var purposeExtension = signer.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        try
        {
            purposes = purposeExtension?.EnhancedKeyUsages.Cast<Oid>().Select(purpose => purpose.Value).ToList() ?? [];
            keyUsages = signer.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault()?.KeyUsages;
        }
        catch (CryptographicException ex)
        {
            throw new TimestampTokenValidationException("the signing certificate's usage extensions are malformed.", ex);
        }

        if (purposes.Count == 0 || purposes.Any(purpose => purpose != TimeStampingPurposeOid))
        {
            throw new TimestampTokenValidationException("the signing certificate's extended key usage must contain only id-kp-timeStamping.");
        }

        if (purposeExtension is { Critical: false })
        {
            throw new TimestampTokenValidationException("the signing certificate's extended key usage extension must be critical.");
        }

        const X509KeyUsageFlags signatureUsages = X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation;
        if (keyUsages is { } usages && (usages & signatureUsages) == 0)
        {
            throw new TimestampTokenValidationException("the signing certificate's key usage permits neither digitalSignature nor nonRepudiation.");
        }

        if (keyUsages is { } allUsages && (allUsages & ~signatureUsages) != 0)
        {
            throw new TimestampTokenValidationException("the signing certificate's key usage permits more than digitalSignature and nonRepudiation.");
        }
    }

    /// <summary>
    /// Runs every check of a decoded token whose signature, imprint and signing-certificate attribute were verified:
    /// its content, policy, signing certificate, algorithms and TSA name.
    /// </summary>
    /// <param name="token">The token.</param>
    /// <param name="signer">The signing certificate.</param>
    /// <param name="policyId">The policy the token must be issued under, or <c>null</c> to accept any.</param>
    /// <exception cref="TimestampTokenValidationException">Thrown when a check fails.</exception>
    public static void ValidateToken(Rfc3161TimestampToken token, X509Certificate2 signer, Oid? policyId)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(signer);
        ValidateContent(token);
        ValidatePolicy(token, policyId);
        ValidateSigner(signer);
        ValidateAlgorithms(token, signer);
        ValidateAuthorityName(token, signer);
    }

    /// <summary>
    /// Gets the CRLs a certificate's distribution points name, with their HTTP locations and, unless a point names
    /// another CRL issuer, the issuer they must come from. Points without an HTTP location are skipped, but a
    /// certificate that names distribution points must yield at least one CRL that covers all revocation reasons
    /// (RFC 5280, section 4.2.1.13); points limited to some reasons are captured beside it.
    /// </summary>
    private static IReadOnlyList<CrlSource> GetCrlSources(X509Certificate2 certificate)
    {
        var points = CrlDistributionPoints.GetHttpDistributionPoints(certificate);
        if (!points.Any(point => point.CoversAllReasons) && CrlDistributionPoints.NamesDistributionPoints(certificate))
        {
            throw new TimestampTokenValidationException(
                $"the certificate '{certificate.Subject}' names CRL distribution points, but none with an HTTP or HTTPS location " +
                "covers all revocation reasons.");
        }

        var fromCaCertificate = certificate.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault() is { CertificateAuthority: true };
        return points.Select(point => new CrlSource(point.Locations,
                                                    point.NamesCrlIssuer ? null : certificate.IssuerName.RawData,
                                                    fromCaCertificate,
                                                    !fromCaCertificate))
                     .ToList();
    }

    /// <summary>
    /// Gets a value indicating whether the hash algorithm of a <c>TSTInfo</c>'s message imprint has no parameters or
    /// <c>NULL</c> ones: <c>TSTInfo ::= SEQUENCE { version, policy, messageImprint SEQUENCE { hashAlgorithm, ... }, ... }</c>.
    /// </summary>
    private static Boolean HasImprintAlgorithmWithoutParameters(ReadOnlyMemory<Byte> tstInfo)
    {
        try
        {
            var info = new AsnReader(tstInfo, AsnEncodingRules.DER).ReadSequence();
            info.ReadEncodedValue();
            info.ReadEncodedValue();
            var algorithm = info.ReadSequence().ReadSequence();
            algorithm.ReadObjectIdentifier();
            if (!algorithm.HasData)
            {
                return true;
            }

            algorithm.ReadNull();
            return !algorithm.HasData;
        }
        catch (AsnContentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Checks one encoded DER value and, for a constructed one, each of its children. Universal string and bit string
    /// types must be primitive in DER.
    /// </summary>
    private static void RequireDerValue(ReadOnlyMemory<Byte> encoded, Int32 depth = 0)
    {
        var tag = Asn1Tag.Decode(encoded.Span, out _);
        if (!tag.IsConstructed)
        {
            return;
        }

        // Unsigned attributes are not covered by the signature, so deep nesting there must not cost the verifiers.
        if (depth >= MaximumDerDepth)
        {
            throw new AsnContentException($"DER values are nested deeper than {MaximumDerDepth} levels.");
        }

        if (tag.TagClass == TagClass.Universal && tag.TagValue is not ((Int32)UniversalTagNumber.Sequence or (Int32)UniversalTagNumber.Set))
        {
            throw new AsnContentException("A universal primitive type is encoded as constructed.");
        }

        AsnDecoder.ReadEncodedValue(encoded.Span, AsnEncodingRules.DER, out var contentOffset, out var contentLength, out _);
        var children = new AsnReader(encoded.Slice(contentOffset, contentLength), AsnEncodingRules.DER);
        while (children.HasData)
        {
            RequireDerValue(children.ReadEncodedValue(), depth + 1);
        }
    }
}
