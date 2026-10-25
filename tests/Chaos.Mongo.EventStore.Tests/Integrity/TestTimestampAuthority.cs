// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity;

using Chaos.Mongo.EventStore.Integrity;
using Microsoft.Extensions.DependencyInjection;
using System.Buffers.Binary;
using System.Formats.Asn1;
using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

/// <summary>
/// An in-process RFC 3161 timestamp authority with a self-signed test CA, served through an
/// <see cref="HttpMessageHandler"/>. Producing tokens is test code; validating them is not.
/// </summary>
internal sealed class TestTimestampAuthority : HttpMessageHandler
{
    public static readonly Uri CrlMirrorUri = new("http://tsa.test/mirror/ca.crl");
    public static readonly Uri CrlUri = new("http://tsa.test/ca.crl");

    /// <summary>
    /// The policy the authority issues tokens under unless another one is requested.
    /// </summary>
    public const String DefaultPolicyId = "1.3.6.1.4.1.99999.1";

    public const String DnsName = "tsa.test";

    /// <summary>
    /// The subject of the CA that issues <see cref="OtherIssuerCrl"/>.
    /// </summary>
    public const String OtherIssuerName = "CN=Chaos.Mongo Other CA";

    /// <summary>
    /// The size of the bodies the authority sends in its oversized modes.
    /// </summary>
    public const Int32 OversizedLength = 64 * 1024;

    public static readonly Uri TimestampUri = new("http://tsa.test/timestamp");
    private const String CodeSigningPurposeOid = "1.3.6.1.5.5.7.3.3";
    private const String CrlDistributionPointsOid = "2.5.29.31";
    private const String KeyUsageOid = "2.5.29.15";
    private const String SigningCertificateV2Oid = "1.2.840.113549.1.9.16.2.47";
    private const String SubjectAlternativeNameOid = "2.5.29.17";
    private const String TimeStampingPurposeOid = "1.3.6.1.5.5.7.3.8";
    private const String TstInfoContentTypeOid = "1.2.840.113549.1.9.16.1.4";
    private readonly Boolean _alternativeTsaName;
    private readonly List<Byte[]?> _nonces = [];
    private Int32 _crlRequests;

    /// <summary>
    /// Creates the authority. Its signing certificate names <paramref name="crlLocations"/> as one CRL distribution
    /// point, or carries <paramref name="rawCrlDistributionPoints"/> as the extension value when given. Tokens name
    /// the signer's subject as their TSA name, or its DNS alternative name when <paramref name="alternativeTsaName"/>
    /// is set. The signer's key usage extension carries <paramref name="rawKeyUsage"/> as its value when given; the
    /// signer then has an RSA key, because the ECDSA key accessors decode the key usage themselves, and it has one
    /// of <paramref name="rsaKeySize"/> bits when given. With
    /// <paramref name="duplicateKeyUsage"/> the signer carries its key usage extension twice, and with an alternative
    /// TSA name its subject alternative names extension carries <paramref name="rawAlternativeNames"/> when given.
    /// The CA's CRL lists <paramref name="revokedCertificates"/> revoked certificates, about 28 bytes each, so tests
    /// can capture large CRLs. With <paramref name="nonCriticalPurpose"/> the signer's extended key usage extension is
    /// not critical. The root carries <paramref name="rootKeyUsage"/>.
    /// </summary>
    public TestTimestampAuthority(IReadOnlyList<Uri>? crlLocations = null,
                                  Byte[]? rawCrlDistributionPoints = null,
                                  Boolean timestampingUsage = true,
                                  Boolean expiredSigner = false,
                                  Boolean additionalPurpose = false,
                                  X509KeyUsageFlags keyUsage = X509KeyUsageFlags.DigitalSignature,
                                  Boolean alternativeTsaName = false,
                                  Byte[]? rawKeyUsage = null,
                                  Boolean duplicateKeyUsage = false,
                                  Byte[]? rawAlternativeNames = null,
                                  Int32? rsaKeySize = null,
                                  Int32 revokedCertificates = 0,
                                  Boolean nonCriticalPurpose = false,
                                  X509KeyUsageFlags rootKeyUsage = X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign)
    {
        var notBefore = DateTimeOffset.UtcNow.AddDays(-30);
        var notAfter = DateTimeOffset.UtcNow.AddDays(30);
        Root = CreateRoot("CN=Chaos.Mongo Test TSA Root", notBefore, notAfter, rootKeyUsage);
        _alternativeTsaName = alternativeTsaName;

        var rsaSigner = rawKeyUsage is not null || rsaKeySize is not null;
        using var ecdsaKey = rsaSigner ? null : ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var rsaKey = rsaSigner ? RSA.Create(rsaKeySize ?? 2048) : null;
        var request = ecdsaKey is not null
            ? new CertificateRequest("CN=Chaos.Mongo Test TSA", ecdsaKey, HashAlgorithmName.SHA256)
            : new CertificateRequest("CN=Chaos.Mongo Test TSA", rsaKey!, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        var keyUsageExtension = rawKeyUsage is null ? new X509KeyUsageExtension(keyUsage, true) : new X509Extension(KeyUsageOid, rawKeyUsage, true);
        request.CertificateExtensions.Add(keyUsageExtension);
        if (duplicateKeyUsage)
        {
            request.CertificateExtensions.Add(new X509Extension(DuplicateExtensions.PlaceholderOid, keyUsageExtension.RawData, true));
        }

        if (timestampingUsage)
        {
            var purposes = new OidCollection
            {
                new Oid(TimeStampingPurposeOid)
            };
            if (additionalPurpose)
            {
                purposes.Add(new Oid(CodeSigningPurposeOid));
            }

            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(purposes, !nonCriticalPurpose));
        }

        if (alternativeTsaName)
        {
            var alternativeNames = new SubjectAlternativeNameBuilder();
            alternativeNames.AddDnsName(DnsName);
            request.CertificateExtensions.Add(rawAlternativeNames is null
                                                  ? alternativeNames.Build()
                                                  : new X509Extension(SubjectAlternativeNameOid, rawAlternativeNames, false));
        }

        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(Root, true, false));
        var locations = crlLocations ?? [CrlUri, CrlMirrorUri];
        if (rawCrlDistributionPoints is not null)
        {
            request.CertificateExtensions.Add(new X509Extension(CrlDistributionPointsOid, rawCrlDistributionPoints, false));
        }
        else if (locations.Count > 0)
        {
            request.CertificateExtensions.Add(new X509Extension(CrlDistributionPointsOid, EncodeDistributionPoints(locations.Select(uri => uri.AbsoluteUri).ToArray()), false));
        }

        var signerNotAfter = expiredSigner ? DateTimeOffset.UtcNow.AddDays(-1) : notAfter;
        using var rootKey = Root.GetECDsaPrivateKey()!;
        using var issued = request.Create(Root.SubjectName, X509SignatureGenerator.CreateForECDsa(rootKey), notBefore, signerNotAfter, RandomNumberGenerator.GetBytes(8));
        using var patched = duplicateKeyUsage
            ? X509CertificateLoader.LoadCertificate(DuplicateExtensions.RenamePlaceholder(issued.RawData, KeyUsageOid))
            : null;
        var certificate = patched ?? issued;
        SigningCertificate = ecdsaKey is not null ? certificate.CopyWithPrivateKey(ecdsaKey) : certificate.CopyWithPrivateKey(rsaKey!);
        // The BCL's builder refuses an issuer without cRLSign, so such a root's CRLs are encoded directly.
        var rootSignsCrls = rootKeyUsage.HasFlag(X509KeyUsageFlags.CrlSign);
        Crl = rootSignsCrls
            ? BuildRevocationList(revokedCertificates, notBefore).Build(Root, BigInteger.One, notAfter, HashAlgorithmName.SHA256)
            : BuildCrlWithExtensions(DateTimeOffset.UtcNow);
        ExpiredCrl = rootSignsCrls
            ? new CertificateRevocationListBuilder().Build(Root,
                                                           BigInteger.One,
                                                           DateTimeOffset.UtcNow.AddDays(-1),
                                                           HashAlgorithmName.SHA256,
                                                           thisUpdate: DateTimeOffset.UtcNow.AddDays(-2))
            : BuildCrlWithExtensions(DateTimeOffset.UtcNow.AddDays(-9));
        using var otherRoot = CreateRoot(OtherIssuerName, notBefore, notAfter, X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign);
        OtherIssuerCrl = new CertificateRevocationListBuilder().Build(otherRoot, BigInteger.One, notAfter, HashAlgorithmName.SHA256);
    }

    /// <summary>
    /// Gets the locations besides <see cref="CrlUri"/> and <see cref="CrlMirrorUri"/> at which the authority serves
    /// its CRL.
    /// </summary>
    public ISet<Uri> AdditionalCrlUris { get; } = new HashSet<Uri>();

    /// <summary>
    /// Gets the CRL the authority serves for its CA.
    /// </summary>
    public Byte[] Crl { get; }

    /// <summary>
    /// Gets the number of CRL requests received so far.
    /// </summary>
    public Int32 CrlRequests => Volatile.Read(ref _crlRequests);

    /// <summary>
    /// Gets a CRL of the authority's CA that expired before any token the authority issues.
    /// </summary>
    public Byte[] ExpiredCrl { get; }

    /// <summary>
    /// Gets or sets how the authority answers.
    /// </summary>
    public TestTimestampAuthorityMode Mode { get; set; } = TestTimestampAuthorityMode.Valid;

    /// <summary>
    /// Gets the nonces of the timestamp requests received so far.
    /// </summary>
    public IReadOnlyList<Byte[]?> Nonces => _nonces;

    /// <summary>
    /// Gets a CRL issued by another CA, named <see cref="OtherIssuerName"/>.
    /// </summary>
    public Byte[] OtherIssuerCrl { get; }

    /// <summary>
    /// Gets the self-signed root of the test CA.
    /// </summary>
    public X509Certificate2 Root { get; }

    /// <summary>
    /// Gets or sets the CRL served at every location in the modes that serve <see cref="Crl"/>, instead of it.
    /// </summary>
    public Byte[]? ServedCrl { get; set; }

    /// <summary>
    /// Gets the CRLs served at single locations in the modes that serve <see cref="Crl"/>, instead of it and of
    /// <see cref="ServedCrl"/>.
    /// </summary>
    public IDictionary<Uri, Byte[]> ServedCrlsByLocation { get; } = new Dictionary<Uri, Byte[]>();

    /// <summary>
    /// Gets the authority's signing certificate, including its private key.
    /// </summary>
    public X509Certificate2 SigningCertificate { get; }

    /// <summary>
    /// Gets or sets the DNS name tokens carry as their TSA name when the authority uses an alternative TSA name;
    /// defaults to the signer's alternative name.
    /// </summary>
    public String? TokenDnsName { get; set; }

    /// <summary>
    /// Encodes a CRL distribution points extension value with one distribution point per argument, each naming its
    /// locations as uniform resource identifiers in its full name.
    /// </summary>
    public static Byte[] EncodeDistributionPoints(params String[][] points)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            foreach (var locations in points)
            {
                using (writer.PushSequence())
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
                {
                    foreach (var location in locations)
                    {
                        writer.WriteCharacterString(UniversalTagNumber.IA5String, location, new Asn1Tag(TagClass.ContextSpecific, 6));
                    }
                }
            }
        }

        return writer.Encode();
    }

    /// <summary>
    /// Encodes a CRL distribution points extension value with one point per location, each with a full name.
    /// </summary>
    public static Byte[] EncodeDistributionPoints(params Uri[] locations)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            foreach (var location in locations)
            {
                using (writer.PushSequence())
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
                {
                    writer.WriteCharacterString(UniversalTagNumber.IA5String, location.OriginalString, new Asn1Tag(TagClass.ContextSpecific, 6));
                }
            }
        }

        return writer.Encode();
    }

    /// <summary>
    /// Encodes an issuing distribution point extension value whose full name names one location.
    /// </summary>
    public static Byte[] EncodeIssuingDistributionPoint(Uri location)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
        using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
        {
            writer.WriteCharacterString(UniversalTagNumber.IA5String, location.OriginalString, new Asn1Tag(TagClass.ContextSpecific, 6));
        }

        return writer.Encode();
    }

    /// <summary>
    /// Builds a CRL of the authority's CA issued at <paramref name="thisUpdate"/>, valid until
    /// <paramref name="nextUpdate"/> or for a week, that revokes the given certificates.
    /// </summary>
    public Byte[] BuildCrl(DateTimeOffset thisUpdate, DateTimeOffset? nextUpdate = null, params (Byte[] SerialNumber, DateTimeOffset RevokedAt)[] revoked)
    {
        var builder = new CertificateRevocationListBuilder();
        foreach (var (serialNumber, revokedAt) in revoked)
        {
            builder.AddEntry(serialNumber, revokedAt);
        }

        return builder.Build(Root, BigInteger.One, nextUpdate ?? thisUpdate.AddDays(7), HashAlgorithmName.SHA256, thisUpdate: thisUpdate);
    }

    /// <summary>
    /// Builds a CRL of the authority's CA, signed by its root, issued at <paramref name="thisUpdate"/> and valid for a
    /// week, that carries exactly the given CRL extensions.
    /// </summary>
    public Byte[] BuildCrlWithExtensions(DateTimeOffset thisUpdate, params (String Oid, Boolean Critical, Byte[] Value)[] extensions)
        => BuildCrlWithExtensions(thisUpdate, [], extensions);

    /// <summary>
    /// Builds a CRL of the authority's CA, signed by its root, issued at <paramref name="thisUpdate"/> and valid for a
    /// week, that lists the given revoked certificates and carries exactly the given CRL extensions.
    /// </summary>
    public Byte[] BuildCrlWithExtensions(DateTimeOffset thisUpdate,
                                         IReadOnlyList<(Byte[] SerialNumber, DateTimeOffset RevokedAt)> revoked,
                                         params (String Oid, Boolean Critical, Byte[] Value)[] extensions)
    {
        var tbs = new AsnWriter(AsnEncodingRules.DER);
        using (tbs.PushSequence())
        {
            tbs.WriteInteger(1);
            WriteEcdsaWithSha256(tbs);
            tbs.WriteEncodedValue(Root.SubjectName.RawData);
            tbs.WriteUtcTime(thisUpdate);
            tbs.WriteUtcTime(thisUpdate.AddDays(7));
            if (revoked.Count > 0)
            {
                using (tbs.PushSequence())
                {
                    foreach (var (serialNumber, revokedAt) in revoked)
                    {
                        using (tbs.PushSequence())
                        {
                            tbs.WriteIntegerUnsigned(serialNumber);
                            tbs.WriteUtcTime(revokedAt);
                        }
                    }
                }
            }

            if (extensions.Length > 0)
            {
                using (tbs.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
                using (tbs.PushSequence())
                {
                    foreach (var (oid, critical, value) in extensions)
                    {
                        using (tbs.PushSequence())
                        {
                            tbs.WriteObjectIdentifier(oid);
                            if (critical)
                            {
                                tbs.WriteBoolean(true);
                            }

                            tbs.WriteOctetString(value);
                        }
                    }
                }
            }
        }

        var tbsBytes = tbs.Encode();
        using var key = Root.GetECDsaPrivateKey() ?? throw new InvalidOperationException("The test root has no ECDSA private key.");
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteEncodedValue(tbsBytes);
            WriteEcdsaWithSha256(writer);
            writer.WriteBitString(key.SignData(tbsBytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        }

        return writer.Encode();
    }

    /// <summary>
    /// Encodes an unsigned CRL of the authority's CA issued at <paramref name="thisUpdate"/> without a next update; CRL
    /// signatures are not verified.
    /// </summary>
    public Byte[] BuildCrlWithoutNextUpdate(DateTimeOffset thisUpdate)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence())
            {
                writer.WriteInteger(1);
                WriteEcdsaWithSha256(writer);
                writer.WriteEncodedValue(Root.SubjectName.RawData);
                writer.WriteUtcTime(thisUpdate);
            }

            WriteEcdsaWithSha256(writer);
            writer.WriteBitString([0x00]);
        }

        return writer.Encode();
    }

    /// <summary>
    /// Creates the production authority configured against this test authority, trusting its root unless another
    /// root is given, without revocation checks. It reads the local time from <paramref name="timeProvider"/> when
    /// given.
    /// </summary>
    public Rfc3161TimestampAuthority CreateAuthority(X509Certificate2? trustedRoot = null,
                                                     String name = "test-tsa",
                                                     Action<Rfc3161TimestampAuthorityOptions>? configure = null,
                                                     TimeProvider? timeProvider = null)
    {
        var options = new Rfc3161TimestampAuthorityOptions
        {
            Name = name,
            Uri = TimestampUri,
            HttpClientFactory = _ => new HttpClient(this, false),
            RevocationMode = X509RevocationMode.NoCheck
        };
        options.TrustedRoots.Add(trustedRoot ?? Root);
        configure?.Invoke(options);
        var services = new ServiceCollection();
        if (timeProvider is not null)
        {
            services.AddSingleton(timeProvider);
        }

        return new Rfc3161TimestampAuthority(options, services.BuildServiceProvider());
    }

    protected override void Dispose(Boolean disposing)
    {
        if (disposing)
        {
            Root.Dispose();
            SigningCertificate.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if ((request.Method == HttpMethod.Post) && (request.RequestUri == TimestampUri))
        {
            return ServeTimestampAsync(request, cancellationToken);
        }

        if ((request.Method == HttpMethod.Get) && request.RequestUri is { } uri && ((uri == CrlUri) || (uri == CrlMirrorUri) || AdditionalCrlUris.Contains(uri)))
        {
            return ServeCrlAsync(uri, cancellationToken);
        }

        return Task.FromResult(Respond(HttpStatusCode.NotFound));
    }

    private static CertificateRevocationListBuilder BuildRevocationList(Int32 revokedCertificates, DateTimeOffset revokedAt)
    {
        var builder = new CertificateRevocationListBuilder();
        var serialNumber = new Byte[9];
        serialNumber[0] = 0x01;
        for (var index = 0L; index < revokedCertificates; index++)
        {
            BinaryPrimitives.WriteInt64BigEndian(serialNumber.AsSpan(1), index);
            builder.AddEntry(serialNumber, revokedAt);
        }

        return builder;
    }

    /// <summary>
    /// Creates random bytes that encode as a minimal positive DER integer: the first byte is in <c>0x01..0x7F</c>,
    /// so it is neither a redundant leading zero nor a sign bit.
    /// </summary>
    private static Byte[] CreateRandomInteger(Int32 length)
    {
        var value = RandomNumberGenerator.GetBytes(length);
        value[0] = (Byte)(1 + (value[0] % 0x7F));
        return value;
    }

    private static X509Certificate2 CreateRoot(String subject, DateTimeOffset notBefore, DateTimeOffset notAfter, X509KeyUsageFlags keyUsage)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(keyUsage, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(notBefore, notAfter);
    }

    /// <summary>
    /// Encodes a certificate's subject as a <c>directoryName</c> general name.
    /// </summary>
    private static Byte[] EncodeDirectoryName(X509Certificate2 certificate)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 4)))
        {
            writer.WriteEncodedValue(certificate.SubjectName.RawData);
        }

        return writer.Encode();
    }

    /// <summary>
    /// Encodes a <c>dNSName</c> general name.
    /// </summary>
    private static Byte[] EncodeDnsName(String name)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.WriteCharacterString(UniversalTagNumber.IA5String, name, new Asn1Tag(TagClass.ContextSpecific, 2));
        return writer.Encode();
    }

    private static Byte[] EncodeSigningCertificateV2(X509Certificate2 certificate)
    {
        // SigningCertificateV2 ::= SEQUENCE { certs SEQUENCE OF ESSCertIDv2 }, ESSCertIDv2 with the default SHA-256.
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        using (writer.PushSequence())
        using (writer.PushSequence())
        {
            writer.WriteOctetString(SHA256.HashData(certificate.RawData));
        }

        return writer.Encode();
    }

    private static HttpResponseMessage Respond(HttpStatusCode statusCode, Byte[]? content = null)
        => new(statusCode)
        {
            Content = new ByteArrayContent(content ?? [])
        };

    private static void WriteEcdsaWithSha256(AsnWriter writer)
    {
        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier("1.2.840.10045.4.3.2");
        }
    }

    private Byte[] CreateResponse(Byte[] query)
    {
        if (!Rfc3161TimestampRequest.TryDecode(query, out var request, out _))
        {
            throw new InvalidOperationException("The test authority received an invalid timestamp query.");
        }

        _nonces.Add(request.GetNonce()?.ToArray());
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence())
            {
                writer.WriteInteger(Mode == TestTimestampAuthorityMode.Rejection ? 2 : 0);
            }

            if (Mode != TestTimestampAuthorityMode.Rejection)
            {
                writer.WriteEncodedValue(CreateToken(request));
            }
        }

        var response = writer.Encode();
        return Mode == TestTimestampAuthorityMode.TrailingData ? [.. response, 0x05, 0x00] : response;
    }

    private Byte[] CreateToken(Rfc3161TimestampRequest request)
    {
        var hash = request.GetMessageHash().ToArray();
        if (Mode == TestTimestampAuthorityMode.WrongImprint)
        {
            hash[0] ^= 0xFF;
        }

        var nonce = Mode == TestTimestampAuthorityMode.WrongNonce ? CreateRandomInteger(16) : request.GetNonce();
        var serialNumber = CreateRandomInteger(8);
        var timestamp = Mode switch
        {
            TestTimestampAuthorityMode.Backdated => DateTimeOffset.UtcNow.AddHours(-1),
            TestTimestampAuthorityMode.FutureDated => DateTimeOffset.UtcNow.AddHours(1),
            _ => DateTimeOffset.UtcNow
        };
        var tsaName = Mode == TestTimestampAuthorityMode.WrongTsaName ? EncodeDirectoryName(Root) :
            _alternativeTsaName ? EncodeDnsName(TokenDnsName ?? DnsName) : EncodeDirectoryName(SigningCertificate);
        var policyId = Mode == TestTimestampAuthorityMode.IgnoresRequestedPolicy ? null : request.RequestedPolicyId?.Value;
        var info = new Rfc3161TimestampTokenInfo(new Oid(policyId ?? DefaultPolicyId),
                                                 request.HashAlgorithmId,
                                                 hash,
                                                 serialNumber,
                                                 timestamp,
                                                 nonce: nonce,
                                                 timestampAuthorityName: tsaName);

        var content = Mode == TestTimestampAuthorityMode.TrailingTstInfoData ? [.. info.Encode(), 0x05, 0x00] : info.Encode();
        var signedCms = new SignedCms(new ContentInfo(new Oid(TstInfoContentTypeOid), content));
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, SigningCertificate)
        {
            DigestAlgorithm = new Oid(Mode == TestTimestampAuthorityMode.Sha1Digest ? "1.3.14.3.2.26" : "2.16.840.1.101.3.4.2.1"),
            IncludeOption = X509IncludeOption.EndCertOnly
        };
        var attributeCertificate = Mode == TestTimestampAuthorityMode.WrongEssCertId ? Root : SigningCertificate;
        signer.SignedAttributes.Add(new AsnEncodedData(SigningCertificateV2Oid, EncodeSigningCertificateV2(attributeCertificate)));
        signedCms.ComputeSignature(signer);
        if (Mode == TestTimestampAuthorityMode.CounterSigned)
        {
            signedCms.SignerInfos[0].ComputeCounterSignature(new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, SigningCertificate));
        }

        // Without unsigned attributes the signature value is the last element, so this breaks only the signature.
        var token = signedCms.Encode();
        if (Mode == TestTimestampAuthorityMode.BrokenSignature)
        {
            token[^1] ^= 0x01;
        }

        return token;
    }

    private async Task<HttpResponseMessage> ServeCrlAsync(Uri uri, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _crlRequests);
        if (Mode == TestTimestampAuthorityMode.CrlHangs)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        switch (Mode)
        {
            case TestTimestampAuthorityMode.CrlOversized:
                return Respond(HttpStatusCode.OK, new Byte[OversizedLength]);
            case TestTimestampAuthorityMode.CrlOversizedWithoutLength:
                var response = Respond(HttpStatusCode.OK, new Byte[OversizedLength]);
                response.Content.Headers.ContentLength = null;
                return response;
            case TestTimestampAuthorityMode.CrlNotACrl:
                return Respond(HttpStatusCode.OK, "<html><body>Please sign in to the network.</body></html>"u8.ToArray());
            case TestTimestampAuthorityMode.CrlFromOtherIssuer:
                return Respond(HttpStatusCode.OK, OtherIssuerCrl);
            case TestTimestampAuthorityMode.CrlExpired:
                return Respond(HttpStatusCode.OK, ExpiredCrl);
            case TestTimestampAuthorityMode.CrlWithoutNextUpdate:
                return Respond(HttpStatusCode.OK, BuildCrlWithoutNextUpdate(DateTimeOffset.UtcNow.AddHours(-1)));
            case TestTimestampAuthorityMode.CrlIssuedInFuture:
                return Respond(HttpStatusCode.OK, BuildCrl(DateTimeOffset.UtcNow.AddDays(1)));
            case TestTimestampAuthorityMode.CrlIssuedAfterToken:
                // Built when served, so its issue lies after the token's time but within the allowed clock skew.
                return Respond(HttpStatusCode.OK, BuildCrl(DateTimeOffset.UtcNow.AddMinutes(1)));
            case TestTimestampAuthorityMode.CrlConnectionLost:
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ConnectionLostContent()
                };
        }

        var unavailable = Mode == TestTimestampAuthorityMode.CrlUnavailable ||
                          ((Mode == TestTimestampAuthorityMode.CrlPrimaryUnavailable) && (uri == CrlUri));
        if (!unavailable)
        {
            var served = ServedCrlsByLocation.TryGetValue(uri, out var located) ? located : ServedCrl ?? Crl;
            return Respond(HttpStatusCode.OK, Mode == TestTimestampAuthorityMode.CrlEmpty ? [] : served);
        }

        return uri == CrlMirrorUri ? throw new HttpRequestException("The CRL mirror is unreachable.") : Respond(HttpStatusCode.NotFound);
    }

    private async Task<HttpResponseMessage> ServeTimestampAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        switch (Mode)
        {
            case TestTimestampAuthorityMode.HttpFailure:
                return Respond(HttpStatusCode.InternalServerError);
            case TestTimestampAuthorityMode.Unreachable:
                throw new HttpRequestException("The test authority is unreachable.");
            case TestTimestampAuthorityMode.Timeout:
                throw new TaskCanceledException("The request to the test authority timed out.");
        }

        if (request.Content?.Headers.ContentType?.MediaType != "application/timestamp-query")
        {
            return Respond(HttpStatusCode.UnsupportedMediaType);
        }

        var query = await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var response = CreateResponse(query);
        return Respond(HttpStatusCode.OK, Mode == TestTimestampAuthorityMode.ResponseOversized ? [.. response, .. new Byte[OversizedLength]] : response);
    }

    /// <summary>
    /// A body whose connection is lost after the headers arrived. Like a socket handler's content, it hands out its
    /// read stream directly, so the loss surfaces from reading that stream.
    /// </summary>
    private sealed class ConnectionLostContent : HttpContent
    {
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new ConnectionLostStream());

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => throw new NotSupportedException("The body is only read as a stream.");

        protected override Boolean TryComputeLength(out Int64 length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class ConnectionLostStream : Stream
    {
        public override Boolean CanRead => true;

        public override Boolean CanSeek => false;

        public override Boolean CanWrite => false;

        public override Int64 Length => throw new NotSupportedException();

        public override Int64 Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override Int32 Read(Byte[] buffer, Int32 offset, Int32 count) => throw new IOException("The connection to the CRL host was lost.");

        public override Int64 Seek(Int64 offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(Int64 value) => throw new NotSupportedException();

        public override void Write(Byte[] buffer, Int32 offset, Int32 count) => throw new NotSupportedException();
    }
}
