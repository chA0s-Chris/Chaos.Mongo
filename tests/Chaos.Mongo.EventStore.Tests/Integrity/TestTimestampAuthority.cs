// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity;

using Chaos.Mongo.EventStore.Integrity;
using Microsoft.Extensions.DependencyInjection;
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
    public static readonly Uri TimestampUri = new("http://tsa.test/timestamp");
    private const String CrlDistributionPointsOid = "2.5.29.31";
    private static readonly Oid PolicyId = new("1.3.6.1.4.1.99999.1");
    private const String SigningCertificateV2Oid = "1.2.840.113549.1.9.16.2.47";
    private const String TimeStampingPurposeOid = "1.3.6.1.5.5.7.3.8";
    private const String TstInfoContentTypeOid = "1.2.840.113549.1.9.16.1.4";
    private readonly List<Byte[]?> _nonces = [];

    /// <summary>
    /// Creates the authority. Its signing certificate names <paramref name="crlLocations"/> as one CRL distribution
    /// point, or carries <paramref name="rawCrlDistributionPoints"/> as the extension value when given.
    /// </summary>
    public TestTimestampAuthority(IReadOnlyList<Uri>? crlLocations = null,
                                  Byte[]? rawCrlDistributionPoints = null,
                                  Boolean timestampingUsage = true,
                                  Boolean expiredSigner = false)
    {
        var notBefore = DateTimeOffset.UtcNow.AddDays(-30);
        var notAfter = DateTimeOffset.UtcNow.AddDays(30);
        Root = CreateRoot(notBefore, notAfter);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Chaos.Mongo Test TSA", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        if (timestampingUsage)
        {
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(TimeStampingPurposeOid)], true));
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
        using var issued = request.Create(Root, notBefore, signerNotAfter, RandomNumberGenerator.GetBytes(8));
        SigningCertificate = issued.CopyWithPrivateKey(key);
        Crl = new CertificateRevocationListBuilder().Build(Root, BigInteger.One, notAfter, HashAlgorithmName.SHA256);
    }

    /// <summary>
    /// Gets the CRL the authority serves for its CA.
    /// </summary>
    public Byte[] Crl { get; }

    /// <summary>
    /// Gets or sets how the authority answers.
    /// </summary>
    public TestTimestampAuthorityMode Mode { get; set; } = TestTimestampAuthorityMode.Valid;

    /// <summary>
    /// Gets the nonces of the timestamp requests received so far.
    /// </summary>
    public IReadOnlyList<Byte[]?> Nonces => _nonces;

    /// <summary>
    /// Gets the self-signed root of the test CA.
    /// </summary>
    public X509Certificate2 Root { get; }

    /// <summary>
    /// Gets the authority's signing certificate, including its private key.
    /// </summary>
    public X509Certificate2 SigningCertificate { get; }

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
    /// Creates the production authority configured against this test authority, trusting its root unless another
    /// root is given, without revocation checks.
    /// </summary>
    public Rfc3161TimestampAuthority CreateAuthority(X509Certificate2? trustedRoot = null, String name = "test-tsa")
    {
        var options = new Rfc3161TimestampAuthorityOptions
        {
            Name = name,
            Uri = TimestampUri,
            HttpClientFactory = _ => new HttpClient(this, false),
            RevocationMode = X509RevocationMode.NoCheck
        };
        options.TrustedRoots.Add(trustedRoot ?? Root);
        return new Rfc3161TimestampAuthority(options, new ServiceCollection().BuildServiceProvider());
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

        if ((request.Method == HttpMethod.Get) && request.RequestUri is { } uri && ((uri == CrlUri) || (uri == CrlMirrorUri)))
        {
            return Task.FromResult(ServeCrl(uri));
        }

        return Task.FromResult(Respond(HttpStatusCode.NotFound));
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

    private static X509Certificate2 CreateRoot(DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Chaos.Mongo Test TSA Root", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(notBefore, notAfter);
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
        var info = new Rfc3161TimestampTokenInfo(PolicyId, request.HashAlgorithmId, hash, serialNumber, timestamp, nonce: nonce);

        var signedCms = new SignedCms(new ContentInfo(new Oid(TstInfoContentTypeOid), info.Encode()));
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, SigningCertificate)
        {
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"),
            IncludeOption = X509IncludeOption.EndCertOnly
        };
        var attributeCertificate = Mode == TestTimestampAuthorityMode.WrongEssCertId ? Root : SigningCertificate;
        signer.SignedAttributes.Add(new AsnEncodedData(SigningCertificateV2Oid, EncodeSigningCertificateV2(attributeCertificate)));
        signedCms.ComputeSignature(signer);

        // Without unsigned attributes the signature value is the last element, so this breaks only the signature.
        var token = signedCms.Encode();
        if (Mode == TestTimestampAuthorityMode.BrokenSignature)
        {
            token[^1] ^= 0x01;
        }

        return token;
    }

    private HttpResponseMessage ServeCrl(Uri uri)
    {
        var unavailable = Mode == TestTimestampAuthorityMode.CrlUnavailable ||
                          ((Mode == TestTimestampAuthorityMode.CrlPrimaryUnavailable) && (uri == CrlUri));
        if (!unavailable)
        {
            return Respond(HttpStatusCode.OK, Mode == TestTimestampAuthorityMode.CrlEmpty ? [] : Crl);
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
        return Respond(HttpStatusCode.OK, CreateResponse(query));
    }
}
