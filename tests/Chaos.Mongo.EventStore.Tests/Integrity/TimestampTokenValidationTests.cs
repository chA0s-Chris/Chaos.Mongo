// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity;

using Chaos.Mongo.EventStore.Integrity;
using FluentAssertions;
using NUnit.Framework;
using System.Formats.Asn1;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

public class TimestampTokenValidationTests
{
    [Test]
    public void BuildChain_IntermediateOnlyReachableByAiaDownload_DoesNotFetchItAndFailsToBuild()
    {
        using var scenario = CreateAiaScenario();

        var act = () => TimestampTokenValidation.BuildChain(scenario.Token,
                                                            scenario.Signer,
                                                            new ChainSettings(new X509Certificate2Collection(scenario.Root),
                                                                              X509RevocationMode.NoCheck,
                                                                              TimeSpan.FromSeconds(5),
                                                                              new X509Certificate2Collection()));

        act.Should().Throw<TimestampTokenValidationException>();
        scenario.Listener.RequestCount.Should().Be(0, "chain building must never fetch a certificate named by an AIA extension");
    }

    /// <summary>
    /// A positive control for <see cref="BuildChain_IntermediateOnlyReachableByAiaDownload_DoesNotFetchItAndFailsToBuild"/>:
    /// the same incomplete chain, built directly with <see cref="X509Chain"/> instead of through production code, with
    /// certificate downloads left enabled. It must reach the listener and complete the chain, so the paired test above
    /// is shown to actually depend on the fix rather than passing regardless of it.
    /// </summary>
    [Test]
    public void BuildChain_IntermediateOnlyReachableByAiaDownload_PositiveControlWithDownloadsEnabledFetchesItAndBuilds()
    {
        using var scenario = CreateAiaScenario();
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(5);
        chain.ChainPolicy.VerificationTime = scenario.Token.TokenInfo.Timestamp.UtcDateTime;
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(scenario.Root);
        chain.ChainPolicy.ExtraStore.AddRange(scenario.Token.AsSignedCms().Certificates);

        var built = chain.Build(scenario.Signer);

        built.Should().BeTrue("without the fix, the missing intermediate is downloaded and completes the chain");
        scenario.Listener.RequestCount.Should().BeGreaterThan(0, "the test setup must actually require fetching the intermediate");
    }

    [Test]
    public void GetCrlSources_CaCertificate_MarksItsPoints()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Chaos.Mongo Test Intermediate", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509Extension("2.5.29.31", TestTimestampAuthority.EncodeDistributionPoints(TestTimestampAuthority.CrlUri), false));
        using var caCertificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var authority = new TestTimestampAuthority();

        var caSources = TimestampTokenValidation.GetCrlSources([caCertificate]);
        var signerSources = TimestampTokenValidation.GetCrlSources([authority.SigningCertificate]);

        caSources.Should().ContainSingle().Which.Should().Match<CrlSource>(source => source.FromCaCertificate && !source.FromEndEntityCertificate);
        signerSources.Should().OnlyContain(source => !source.FromCaCertificate && source.FromEndEntityCertificate).And.NotBeEmpty();
    }

    [Test]
    [TestCase("Chaos Root", UniversalTagNumber.PrintableString, "chaos ROOT", UniversalTagNumber.UTF8String, true)]
    [TestCase("Chaos Root", UniversalTagNumber.BMPString, "Chaos Root", UniversalTagNumber.IA5String, true)]
    [TestCase("Chaos Root", UniversalTagNumber.T61String, "CHAOS ROOT", UniversalTagNumber.PrintableString, true)]
    [TestCase("Chaos Root", UniversalTagNumber.UTF8String, "Chaos Root 2", UniversalTagNumber.UTF8String, false)]
    [TestCase("Ärger", UniversalTagNumber.UTF8String, "ärger", UniversalTagNumber.BMPString, false)]
    public void NamesMatch_CommonNamesOfStringTypes_ComparesDecodedTextIgnoringAsciiCase(String first,
                                                                                         UniversalTagNumber firstType,
                                                                                         String second,
                                                                                         UniversalTagNumber secondType,
                                                                                         Boolean expected)
    {
        var firstName = new X500DistinguishedNameBuilder();
        firstName.Add("2.5.4.3", first, firstType);
        var secondName = new X500DistinguishedNameBuilder();
        secondName.Add("2.5.4.3", second, secondType);

        TimestampTokenValidation.NamesMatch(firstName.Build(), secondName.Build()).Should().Be(expected);
    }

    [Test]
    public void NamesMatch_SameAttributesInAnotherOrder_ReturnsFalse()
    {
        var first = new X500DistinguishedNameBuilder();
        first.AddOrganizationName("Chaos");
        first.AddCommonName("Root");
        var second = new X500DistinguishedNameBuilder();
        second.AddCommonName("Root");
        second.AddOrganizationName("Chaos");

        TimestampTokenValidation.NamesMatch(first.Build(), second.Build()).Should().BeFalse();
    }

    [Test]
    public async Task RequireDer_IndefiniteLength_ThrowsTimestampTokenValidationException()
    {
        using var authority = new TestTimestampAuthority();
        var token = (await authority.CreateAuthority().TimestampAsync(SHA256.HashData([1]), HashAlgorithmName.SHA256, CancellationToken.None)).Token;
        AsnDecoder.ReadEncodedValue(token, AsnEncodingRules.DER, out var contentOffset, out var contentLength, out _);
        Byte[] berToken = [0x30, 0x80, .. token.AsSpan(contentOffset, contentLength), 0x00, 0x00];

        var der = () => TimestampTokenValidation.RequireDer(token);
        var ber = () => TimestampTokenValidation.RequireDer(berToken);

        der.Should().NotThrow();
        ber.Should().Throw<TimestampTokenValidationException>().WithMessage("*not DER-encoded*");
    }

    [Test]
    [TestCase(40, false)]
    [TestCase(70, true)]
    public void RequireDer_NestedSequences_RejectsNestingBeyondTheLimit(Int32 depth, Boolean rejected)
    {
        Byte[] value = [0x05, 0x00];
        for (var level = 0; level < depth; level++)
        {
            Byte[] length = value.Length < 0x80 ? [(Byte)value.Length] : [0x81, (Byte)value.Length];
            value = [0x30, .. length, .. value];
        }

        var act = () => TimestampTokenValidation.RequireDer(value);

        if (rejected)
        {
            act.Should().Throw<TimestampTokenValidationException>().WithMessage("*DER*");
        }
        else
        {
            act.Should().NotThrow();
        }
    }

    [Test]
    public async Task ValidateContent_CountersignatureAttributeOfAnyValue_ThrowsTimestampTokenValidationException()
    {
        using var authority = new TestTimestampAuthority();
        var token = (await authority.CreateAuthority().TimestampAsync(SHA256.HashData([1]), HashAlgorithmName.SHA256, CancellationToken.None)).Token;
        var signedCms = new SignedCms();
        signedCms.Decode(token);
        // The attribute is unsigned, so anyone storing the token can add it; its type alone marks a countersignature.
        signedCms.SignerInfos[0].AddUnsignedAttribute(new AsnEncodedData("1.2.840.113549.1.9.6", [0x05, 0x00]));
        Rfc3161TimestampToken.TryDecode(signedCms.Encode(), out var modified, out _).Should().BeTrue();

        var act = () => TimestampTokenValidation.ValidateContent(modified ?? throw new AssertionException("The modified token does not decode."));

        act.Should().Throw<TimestampTokenValidationException>().WithMessage("*countersignature*");
    }

    [Test]
    public void ValidateSigner_CriticalTimestampingPurpose_Passes()
    {
        using var authority = new TestTimestampAuthority();
        var signer = authority.SigningCertificate;

        var act = () => TimestampTokenValidation.ValidateSigner(signer);

        act.Should().NotThrow();
    }

    [Test]
    public void ValidateSigner_KeyUsageBeyondSignatures_ThrowsTimestampTokenValidationException()
    {
        using var authority = new TestTimestampAuthority(keyUsage: X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment);
        var signer = authority.SigningCertificate;

        var act = () => TimestampTokenValidation.ValidateSigner(signer);

        act.Should().Throw<TimestampTokenValidationException>().WithMessage("*more than digitalSignature and nonRepudiation*");
    }

    [Test]
    public void ValidateSigner_NonCriticalPurpose_ThrowsTimestampTokenValidationException()
    {
        using var authority = new TestTimestampAuthority(nonCriticalPurpose: true);
        var signer = authority.SigningCertificate;

        var act = () => TimestampTokenValidation.ValidateSigner(signer);

        act.Should().Throw<TimestampTokenValidationException>().WithMessage("*must be critical*");
    }

    /// <summary>
    /// Creates a token whose signing certificate is issued by an intermediate that is not among the token's own
    /// certificates or the chain settings' extra certificates, with an Authority Information Access extension naming
    /// the returned scenario's listener as the location to complete the chain from.
    /// </summary>
    private static AiaScenario CreateAiaScenario()
    {
        var listener = new AiaListener();
        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = DateTimeOffset.UtcNow.AddDays(1);

        // Each certificate's subject must be unique per call: .NET's chain engine on Linux keeps a persistent on-disk
        // cache of CA certificates it has already fetched or seen, keyed so that a later chain build can find a
        // candidate issuer by subject name without downloading again. A fixed subject name would let a stale, unrelated
        // intermediate from an earlier test run satisfy that lookup, which would mask the fetch this test must observe.
        var unique = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootRequest = new CertificateRequest($"CN=Chaos.Mongo AIA Test Root {unique}", rootKey, HashAlgorithmName.SHA256);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        var root = rootRequest.CreateSelfSigned(notBefore, notAfter);

        using var intermediateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var intermediateRequest = new CertificateRequest($"CN=Chaos.Mongo AIA Test Intermediate {unique}", intermediateKey, HashAlgorithmName.SHA256);
        intermediateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        intermediateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var intermediate = intermediateRequest.Create(root.SubjectName, X509SignatureGenerator.CreateForECDsa(rootKey), notBefore, notAfter,
                                                            RandomNumberGenerator.GetBytes(8));
        listener.Response = intermediate.RawData;

        using var signerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signerRequest = new CertificateRequest($"CN=Chaos.Mongo AIA Test Signer {unique}", signerKey, HashAlgorithmName.SHA256);
        signerRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        signerRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        signerRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
        {
            new Oid(TimestampTokenValidation.TimeStampingPurposeOid)
        }, true));
        signerRequest.CertificateExtensions.Add(new X509Extension("1.3.6.1.5.5.7.1.1", EncodeAuthorityInfoAccess(listener.Uri), false));
        using var signerPublic = signerRequest.Create(intermediate.SubjectName, X509SignatureGenerator.CreateForECDsa(intermediateKey), notBefore, notAfter,
                                                      RandomNumberGenerator.GetBytes(8));
        var signer = signerPublic.CopyWithPrivateKey(signerKey);

        var info = new Rfc3161TimestampTokenInfo(new Oid("1.2.3.4.5.6"), new Oid("2.16.840.1.101.3.4.2.1"), SHA256.HashData([1]), new Byte[] { 0x01 }, DateTimeOffset.UtcNow);
        var signedCms = new SignedCms(new ContentInfo(new Oid("1.2.840.113549.1.9.16.1.4"), info.Encode()));
        var cmsSigner = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, signer)
        {
            IncludeOption = X509IncludeOption.EndCertOnly,
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1")
        };

        // RFC 3161 requires the signing-certificate attribute (RFC 5035, ESSCertIDv2); without it, Rfc3161TimestampToken
        // does not recognize the structure as a timestamp token.
        var essCertId = new AsnWriter(AsnEncodingRules.DER);
        using (essCertId.PushSequence())
        using (essCertId.PushSequence())
        using (essCertId.PushSequence())
        {
            essCertId.WriteOctetString(SHA256.HashData(signer.RawData));
        }

        cmsSigner.SignedAttributes.Add(new AsnEncodedData("1.2.840.113549.1.9.16.2.47", essCertId.Encode()));
        signedCms.ComputeSignature(cmsSigner);
        Rfc3161TimestampToken.TryDecode(signedCms.Encode(), out var token, out _).Should().BeTrue();

        return new AiaScenario(listener, root, signer, token ?? throw new AssertionException("The constructed token does not decode."));
    }

    /// <summary>
    /// Encodes an Authority Information Access extension value naming <paramref name="caIssuersUri"/> as the location
    /// of the issuing certificate (OID <c>1.3.6.1.5.5.7.48.2</c>, <c>id-ad-caIssuers</c>).
    /// </summary>
    private static Byte[] EncodeAuthorityInfoAccess(Uri caIssuersUri)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier("1.3.6.1.5.5.7.48.2");
                writer.WriteCharacterString(UniversalTagNumber.IA5String, caIssuersUri.AbsoluteUri, new Asn1Tag(TagClass.ContextSpecific, 6));
            }
        }

        return writer.Encode();
    }

    /// <summary>
    /// A loopback HTTP server bound to an ephemeral port, serving one fixed response to every request; used to detect
    /// whether chain building fetched a certificate from an Authority Information Access location.
    /// </summary>
    private sealed class AiaListener : IDisposable
    {
        private readonly HttpListener _listener;
        private Int32 _requestCount;

        public AiaListener()
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{GetFreePort()}/");
            _listener.Start();
            Uri = new Uri(_listener.Prefixes.Single() + "intermediate.cer");
            _ = ServeAsync();
        }

        public Int32 RequestCount => Volatile.Read(ref _requestCount);

        public Byte[] Response { get; set; } = [];

        public Uri Uri { get; }

        private static Int32 GetFreePort()
        {
            var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            var port = ((IPEndPoint)socket.LocalEndpoint).Port;
            socket.Stop();
            return port;
        }

        private async Task ServeAsync()
        {
            while (true)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    // The listener was closed while a request was pending, or the test completed.
                    return;
                }

                try
                {
                    Interlocked.Increment(ref _requestCount);
                    context.Response.ContentType = "application/pkix-cert";
                    context.Response.ContentLength64 = Response.Length;
                    await context.Response.OutputStream.WriteAsync(Response);
                    context.Response.OutputStream.Close();
                }
                catch (Exception)
                {
                    // Best effort: a response failure must not crash the background loop.
                }
            }
        }

        public void Dispose() => _listener.Close();
    }

    private sealed record AiaScenario(AiaListener Listener, X509Certificate2 Root, X509Certificate2 Signer, Rfc3161TimestampToken Token) : IDisposable
    {
        public void Dispose()
        {
            Listener.Dispose();
            Root.Dispose();
            Signer.Dispose();
        }
    }
}
