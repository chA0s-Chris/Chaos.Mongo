// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity;

using Chaos.Mongo.EventStore.Integrity;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

/// <summary>
/// An in-process <see cref="ITimestampAuthority"/> whose chain has a genuine intermediate between the signer and
/// the self-signed root, with independently configurable CRL distribution points on the intermediate and the
/// signer, so tests can construct a chain with structurally partial, entirely absent, or demonstrated revocation
/// coverage: situations <see cref="TestTimestampAuthority"/>'s two-certificate chain cannot represent, since its
/// only non-root certificate is the signer itself. <see cref="GetRevocationListsAsync"/> genuinely serves
/// <see cref="BuildIntermediateCrl"/>, counted by <see cref="CrlRequests"/>, so a structurally partial chain's
/// one existing source is actually requested and found to cover it, rather than merely never being checked.
/// </summary>
internal sealed class ChainedTimestampAuthority : ITimestampAuthority, IDisposable
{
    private const String CrlDistributionPointsOid = "2.5.29.31";
    private const String Sha256Oid = "2.16.840.1.101.3.4.2.1";
    private const String SigningCertificateV2Oid = "1.2.840.113549.1.9.16.2.47";
    private const String TstInfoContentTypeOid = "1.2.840.113549.1.9.16.1.4";
    private readonly ECDsa _intermediateKey;
    private Int32 _crlRequests;

    /// <summary>
    /// Creates the authority. The intermediate names <paramref name="intermediateCrlLocations"/> as its CRL
    /// distribution points, or none when omitted; the signer likewise for <paramref name="signerCrlLocations"/>.
    /// </summary>
    public ChainedTimestampAuthority(String name = "chained-tsa",
                                     IReadOnlyList<Uri>? intermediateCrlLocations = null,
                                     IReadOnlyList<Uri>? signerCrlLocations = null,
                                     String subjectPrefix = "Chaos.Mongo Chained TSA")
    {
        Name = name;
        var notBefore = DateTimeOffset.UtcNow.AddDays(-30);
        var notAfter = DateTimeOffset.UtcNow.AddDays(30);

        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootRequest = new CertificateRequest($"CN={subjectPrefix} Root", rootKey, HashAlgorithmName.SHA256);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        Root = rootRequest.CreateSelfSigned(notBefore, notAfter);

        _intermediateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var intermediateRequest = new CertificateRequest($"CN={subjectPrefix} Intermediate", _intermediateKey, HashAlgorithmName.SHA256);
        intermediateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        intermediateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        if (intermediateCrlLocations is { Count: > 0 })
        {
            intermediateRequest.CertificateExtensions.Add(
                new X509Extension(CrlDistributionPointsOid, TestTimestampAuthority.EncodeDistributionPoints(intermediateCrlLocations.ToArray()), false));
        }

        using (var issuedIntermediate = intermediateRequest.Create(Root.SubjectName, X509SignatureGenerator.CreateForECDsa(rootKey), notBefore, notAfter,
                                                                   RandomNumberGenerator.GetBytes(8)))
        {
            Intermediate = issuedIntermediate.CopyWithPrivateKey(_intermediateKey);
        }

        using var signerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signerRequest = new CertificateRequest($"CN={subjectPrefix} Signer", signerKey, HashAlgorithmName.SHA256);
        signerRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        signerRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        signerRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
        {
            new Oid(TimestampTokenValidation.TimeStampingPurposeOid)
        }, true));
        if (signerCrlLocations is { Count: > 0 })
        {
            signerRequest.CertificateExtensions.Add(
                new X509Extension(CrlDistributionPointsOid, TestTimestampAuthority.EncodeDistributionPoints(signerCrlLocations.ToArray()), false));
        }

        using (var issuedSigner = signerRequest.Create(Intermediate.SubjectName, X509SignatureGenerator.CreateForECDsa(_intermediateKey), notBefore, notAfter,
                                                       RandomNumberGenerator.GetBytes(8)))
        {
            Signer = issuedSigner.CopyWithPrivateKey(signerKey);
        }
    }

    /// <summary>
    /// Gets the number of CRL requests received so far.
    /// </summary>
    public Int32 CrlRequests => Volatile.Read(ref _crlRequests);

    /// <summary>
    /// Gets the genuine, non-root intermediate between <see cref="Signer"/> and <see cref="Root"/>.
    /// </summary>
    public X509Certificate2 Intermediate { get; }

    /// <summary>
    /// Gets the self-signed root of the test chain.
    /// </summary>
    public X509Certificate2 Root { get; }

    /// <summary>
    /// Gets the authority's signing certificate, issued by <see cref="Intermediate"/>.
    /// </summary>
    public X509Certificate2 Signer { get; }

    public String Name { get; }

    /// <summary>
    /// Builds a CRL of the intermediate, issued at <paramref name="thisUpdate"/>, valid until
    /// <paramref name="nextUpdate"/> or for a week, that revokes the given certificates.
    /// </summary>
    public Byte[] BuildIntermediateCrl(DateTimeOffset thisUpdate, DateTimeOffset? nextUpdate = null, params (Byte[] SerialNumber, DateTimeOffset RevokedAt)[] revoked)
    {
        var builder = new CertificateRevocationListBuilder();
        foreach (var (serialNumber, revokedAt) in revoked)
        {
            builder.AddEntry(serialNumber, revokedAt);
        }

        return builder.Build(Intermediate, System.Numerics.BigInteger.One, nextUpdate ?? thisUpdate.AddDays(7), HashAlgorithmName.SHA256, thisUpdate: thisUpdate);
    }

    public void Dispose()
    {
        Root.Dispose();
        Intermediate.Dispose();
        Signer.Dispose();
        _intermediateKey.Dispose();
    }

    /// <summary>
    /// Counts the request and returns a genuine CRL of <see cref="Intermediate"/>, issued a second after
    /// <paramref name="issuedNotBeforeUtc"/> (CRLs only encode whole seconds, so issuing exactly at it could round
    /// down below the required instant) so it is never too early, that revokes nothing: whichever certificate
    /// actually names a CRL distribution point in the caller's configuration expects it to come from its own
    /// issuer (the library's default for a point without an explicit CRL issuer), so a source on the signer is
    /// genuinely covered by this CRL, while a source on the intermediate
    /// itself is not (it would need one issued by the root instead). Tests that need an actually uncovered or a
    /// revoking CRL still wrap this authority, for example with a fixed-list <see cref="ITimestampAuthority"/>.
    /// </summary>
    public Task<RevocationLists> GetRevocationListsAsync(IReadOnlyList<Byte[]> certificateChain, DateTime issuedNotBeforeUtc, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _crlRequests);
        var crl = BuildIntermediateCrl(new DateTimeOffset(issuedNotBeforeUtc, TimeSpan.Zero) + TimeSpan.FromSeconds(1));
        return Task.FromResult(RevocationLists.Issued([crl]));
    }

    public Task<TimestampToken> TimestampAsync(ReadOnlyMemory<Byte> digest, HashAlgorithmName algorithm, CancellationToken cancellationToken)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var info = new Rfc3161TimestampTokenInfo(new Oid("1.3.6.1.4.1.99999.2"), new Oid(Sha256Oid), digest.ToArray(), RandomNumberGenerator.GetBytes(8), timestamp);
        var signedCms = new SignedCms(new ContentInfo(new Oid(TstInfoContentTypeOid), info.Encode()));
        var cmsSigner = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, Signer)
        {
            IncludeOption = X509IncludeOption.EndCertOnly,
            DigestAlgorithm = new Oid(Sha256Oid)
        };

        // RFC 3161 requires the signing-certificate attribute (RFC 5035, ESSCertIDv2).
        var essCertId = new AsnWriter(AsnEncodingRules.DER);
        using (essCertId.PushSequence())
        using (essCertId.PushSequence())
        using (essCertId.PushSequence())
        {
            essCertId.WriteOctetString(SHA256.HashData(Signer.RawData));
        }

        cmsSigner.SignedAttributes.Add(new AsnEncodedData(SigningCertificateV2Oid, essCertId.Encode()));
        signedCms.ComputeSignature(cmsSigner);
        var encoded = signedCms.Encode();

        // Read the time back from the encoded token, as a real authority's caller would, rather than trust the
        // pre-encoding value: GeneralizedTime's round trip through DER is what TimestampToken.TimestampUtc must
        // agree with to the millisecond.
        Rfc3161TimestampToken.TryDecode(encoded, out var decoded, out _);
        var timestampUtc = (decoded ?? throw new InvalidOperationException("The constructed token does not decode.")).TokenInfo.Timestamp.UtcDateTime;
        return Task.FromResult(new TimestampToken(encoded, [Signer.RawData, Intermediate.RawData, Root.RawData], [], timestampUtc));
    }
}
