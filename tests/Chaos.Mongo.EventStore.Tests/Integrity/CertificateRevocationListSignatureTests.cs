// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity;

using Chaos.Mongo.EventStore.Integrity;
using FluentAssertions;
using NUnit.Framework;
using System.Formats.Asn1;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

public class CertificateRevocationListSignatureTests
{
    private const String Ed25519Oid = "1.3.101.112";
    private const String RsaPssOid = "1.2.840.113549.1.1.10";
    private const String Sha1WithRsaOid = "1.2.840.113549.1.1.5";
    private TestTimestampAuthority _authority;

    [OneTimeSetUp]
    public void CreateAuthority() => _authority = new TestTimestampAuthority();

    [OneTimeTearDown]
    public void DisposeAuthority() => _authority.Dispose();

    [Test]
    public void Verify_DifferentSignatureAlgorithmInSignedPart_ReturnsReason()
    {
        var crl = Rebuild(_authority.Crl, outerAlgorithm: EncodeAlgorithm("1.2.840.10045.4.3.3"));

        CertificateRevocationListSignature.Verify(crl, _authority.Root).Should().Contain("differs");
    }

    [Test]
    public void Verify_EcdsaCrlOfIssuer_ReturnsNull()
        => CertificateRevocationListSignature.Verify(_authority.Crl, _authority.Root).Should().BeNull();

    [Test]
    public void Verify_EcdsaKeyForRsaAlgorithm_ReturnsReason()
    {
        var algorithm = EncodeAlgorithm("1.2.840.113549.1.1.11", true);
        var crl = Rebuild(_authority.Crl, algorithm, algorithm);

        CertificateRevocationListSignature.Verify(crl, _authority.Root).Should().Contain("does not verify");
    }

    [Test]
    public void Verify_MalformedCrl_ReturnsReason()
        => CertificateRevocationListSignature.Verify(new Byte[]
        {
            0x30,
            0x03,
            0x02,
            0x01
        }, _authority.Root).Should().Contain("not a single DER-encoded CRL");

    [Test]
    public void Verify_OtherIssuer_ReturnsReason()
        => CertificateRevocationListSignature.Verify(_authority.OtherIssuerCrl, _authority.Root).Should().Contain("does not verify");

    [Test]
    [TestCase("SHA256", false)]
    [TestCase("SHA384", false)]
    [TestCase("SHA512", false)]
    [TestCase("SHA256", true)]
    [TestCase("SHA512", true)]
    public void Verify_RsaCrl_ReturnsNull(String hash, Boolean pss)
    {
        using var issuer = CreateRsaIssuer(2048);
        var crl = new CertificateRevocationListBuilder().Build(issuer,
                                                               BigInteger.One,
                                                               DateTimeOffset.UtcNow.AddDays(7),
                                                               new HashAlgorithmName(hash),
                                                               pss ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1);

        CertificateRevocationListSignature.Verify(crl, issuer).Should().BeNull();
    }

    [Test]
    public void Verify_RsaKeyBelowMinimum_ReturnsReason()
    {
        using var issuer = CreateRsaIssuer(1024);
        var crl = new CertificateRevocationListBuilder().Build(issuer, BigInteger.One, DateTimeOffset.UtcNow.AddDays(7), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        CertificateRevocationListSignature.Verify(crl, issuer).Should().Contain("required size");
    }

    [Test]
    [TestCase(false)]
    [TestCase(true)]
    public void Verify_RsaPssWithUnsupportedParameters_ReturnsReason(Boolean defaultParameters)
    {
        using var issuer = CreateRsaIssuer(2048);
        var crl = new CertificateRevocationListBuilder().Build(issuer,
                                                               BigInteger.One,
                                                               DateTimeOffset.UtcNow.AddDays(7),
                                                               HashAlgorithmName.SHA256,
                                                               RSASignaturePadding.Pss);
        var algorithm = defaultParameters ? EncodePssAlgorithm(null, null) : EncodePssAlgorithm("2.16.840.1.101.3.4.2.1", 20);

        CertificateRevocationListSignature.Verify(Rebuild(crl, algorithm, algorithm), issuer).Should().Contain("RSASSA-PSS parameters");
    }

    [Test]
    public void Verify_SignatureWithUnusedBits_ReturnsReason()
    {
        var parts = ReadParts(_authority.Crl);
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteEncodedValue(parts.Tbs.Span);
            writer.WriteEncodedValue(parts.Algorithm.Span);
            var signature = parts.Signature.ToArray();
            signature[^1] &= 0xF0;
            writer.WriteBitString(signature, 4);
        }

        CertificateRevocationListSignature.Verify(writer.Encode(), _authority.Root).Should().Contain("whole number of bytes");
    }

    [Test]
    public void Verify_TamperedSignedPart_ReturnsReason()
    {
        var crl = _authority.BuildCrl(DateTimeOffset.UtcNow);
        var tampered = _authority.BuildCrl(DateTimeOffset.UtcNow.AddMinutes(1));
        var forged = Rebuild(tampered, signature: ReadParts(crl).Signature);

        CertificateRevocationListSignature.Verify(forged, _authority.Root).Should().Contain("does not verify");
    }

    [Test]
    public void Verify_UnsupportedAlgorithm_ReturnsReason()
    {
        var algorithm = EncodeAlgorithm(Ed25519Oid);

        CertificateRevocationListSignature.Verify(Rebuild(_authority.Crl, algorithm, algorithm), _authority.Root).Should().Contain("not supported");
    }

    [Test]
    public void Verify_WeakAlgorithm_ReturnsReason()
    {
        var algorithm = EncodeAlgorithm(Sha1WithRsaOid, true);

        CertificateRevocationListSignature.Verify(Rebuild(_authority.Crl, algorithm, algorithm), _authority.Root).Should().Contain("weak algorithm");
    }

    private static X509Certificate2 CreateRsaIssuer(Int32 keySize)
    {
        using var key = RSA.Create(keySize);
        var request = new CertificateRequest("CN=Chaos.Mongo RSA CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    private static Byte[] EncodeAlgorithm(String oid, Boolean withNull = false)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier(oid);
            if (withNull)
            {
                writer.WriteNull();
            }
        }

        return writer.Encode();
    }

    /// <summary>
    /// Encodes RSASSA-PSS with an explicit SHA-2 hash, MGF1 with the same hash and the given salt length, or with the
    /// default parameters when <paramref name="hashOid"/> is <c>null</c>.
    /// </summary>
    private static Byte[] EncodePssAlgorithm(String? hashOid, Int32? saltLength)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier(RsaPssOid);
            using (writer.PushSequence())
            {
                if (hashOid is not null)
                {
                    using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
                    {
                        writer.WriteEncodedValue(EncodeAlgorithm(hashOid));
                    }

                    using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1, true)))
                    using (writer.PushSequence())
                    {
                        writer.WriteObjectIdentifier("1.2.840.113549.1.1.8");
                        writer.WriteEncodedValue(EncodeAlgorithm(hashOid));
                    }

                    using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 2, true)))
                    {
                        writer.WriteInteger(saltLength ?? 32);
                    }
                }
            }
        }

        return writer.Encode();
    }

    private static (ReadOnlyMemory<Byte> Tbs, ReadOnlyMemory<Byte> Algorithm, ReadOnlyMemory<Byte> Signature) ReadParts(Byte[] crl)
    {
        var certificateList = new AsnReader(crl, AsnEncodingRules.DER).ReadSequence();
        var tbs = certificateList.ReadEncodedValue();
        var algorithm = certificateList.ReadEncodedValue();
        var signature = certificateList.ReadBitString(out _);
        return (tbs, algorithm, signature);
    }

    /// <summary>
    /// Re-encodes a CRL with a replaced signature algorithm in its signed part and outside it, or a replaced signature.
    /// </summary>
    private static Byte[] Rebuild(Byte[] crl, Byte[]? innerAlgorithm = null, Byte[]? outerAlgorithm = null, ReadOnlyMemory<Byte>? signature = null)
    {
        var parts = ReadParts(crl);
        var tbs = parts.Tbs;
        if (innerAlgorithm is not null)
        {
            var reader = new AsnReader(parts.Tbs, AsnEncodingRules.DER).ReadSequence();
            var tbsWriter = new AsnWriter(AsnEncodingRules.DER);
            using (tbsWriter.PushSequence())
            {
                if (reader.PeekTag().HasSameClassAndValue(Asn1Tag.Integer))
                {
                    tbsWriter.WriteEncodedValue(reader.ReadEncodedValue().Span);
                }

                reader.ReadEncodedValue();
                tbsWriter.WriteEncodedValue(innerAlgorithm);
                while (reader.HasData)
                {
                    tbsWriter.WriteEncodedValue(reader.ReadEncodedValue().Span);
                }
            }

            tbs = tbsWriter.Encode();
        }

        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteEncodedValue(tbs.Span);
            writer.WriteEncodedValue(outerAlgorithm ?? parts.Algorithm.Span);
            writer.WriteBitString((signature ?? parts.Signature).Span);
        }

        return writer.Encode();
    }
}
