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

public class RevokedCertificateTests
{
    private const String CertificateIssuerOid = "2.5.29.29";
    private const String InvalidityDateOid = "2.5.29.24";
    private static readonly X500DistinguishedName OtherIssuer = new("CN=Chaos.Mongo Other CA");
    private const String ReasonCodeOid = "2.5.29.21";
    private static readonly DateTimeOffset RevokedAt = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Byte[] SerialNumber = [0x01, 0x02, 0x03];

    [Test]
    public void DecodeAll_CrlFromBuilder_ReturnsEntriesOfTheCrlIssuer()
    {
        using var issuer = CreateCertificate("CN=Chaos.Mongo Test CA", null, [0x01]);
        var builder = new CertificateRevocationListBuilder();
        builder.AddEntry(SerialNumber, RevokedAt, X509RevocationReason.KeyCompromise);
        builder.AddEntry([0x7F], RevokedAt.AddDays(1));
        var crl = builder.Build(issuer, BigInteger.One, RevokedAt.AddDays(7), HashAlgorithmName.SHA256, thisUpdate: RevokedAt.AddDays(1));

        var entries = RevokedCertificate.DecodeAll(crl);

        entries.Should().HaveCount(2);
        entries[0].SerialNumber.ToArray().Should().Equal(SerialNumber);
        entries[0].RevocationDate.Should().Be(RevokedAt);
        entries[0].InvalidityDate.Should().BeNull();
        entries[0].RemovedFromCrl.Should().BeFalse();
        entries[0].Issuer.GetValueOrDefault().ToArray().Should().Equal(issuer.SubjectName.RawData);
        entries[1].SerialNumber.ToArray().Should().Equal(0x7F);
    }

    [Test]
    public void DecodeAll_CrlWithoutEntries_ReturnsNone()
    {
        using var issuer = CreateCertificate("CN=Chaos.Mongo Test CA", null, [0x01]);
        var crl = new CertificateRevocationListBuilder().Build(issuer, BigInteger.One, RevokedAt.AddDays(7), HashAlgorithmName.SHA256, thisUpdate: RevokedAt);

        RevokedCertificate.DecodeAll(crl).Should().BeEmpty();
        RevokedCertificate.DecodeAll(EncodeCrl(OtherIssuer, false)).Should().BeEmpty();
    }

    [Test]
    public void DecodeAll_EntryExtensions_ReadsInvalidityDateRemovalAndIndirectIssuers()
    {
        var crlIssuer = new X500DistinguishedName("CN=Chaos.Mongo Indirect CRL Issuer");
        var crl = EncodeCrl(crlIssuer,
                            entries:
                            [
                                Entry([0x01], RevokedAt, (InvalidityDateOid, EncodeGeneralizedTime(RevokedAt.AddDays(-2)))),
                                Entry([0x02], RevokedAt, (CertificateIssuerOid, EncodeDirectoryNames(OtherIssuer)), (ReasonCodeOid, EncodeReason(8))),
                                Entry([0x03], RevokedAt, (ReasonCodeOid, EncodeReason(1))),
                                Entry([0x04], RevokedAt, (CertificateIssuerOid, EncodeDnsName("ca.test")))
                            ]);

        var entries = RevokedCertificate.DecodeAll(crl);

        entries.Select(entry => entry.Issuer?.ToArray()).Should().BeEquivalentTo(
            [crlIssuer.RawData, OtherIssuer.RawData, OtherIssuer.RawData, null],
            options => options.WithStrictOrdering(),
            "a certificate issuer applies to the following entries until the next one");
        entries[0].InvalidityDate.Should().Be(RevokedAt.AddDays(-2));
        entries[0].RevokedFrom.Should().Be(RevokedAt.AddDays(-2));
        entries[1].RemovedFromCrl.Should().BeTrue();
        entries[2].RemovedFromCrl.Should().BeFalse();
        entries[2].RevokedFrom.Should().Be(RevokedAt);
    }

    [Test]
    public void DecodeAll_EntryWithoutRevocationDate_ThrowsAsnContentException()
    {
        var crl = EncodeCrl(OtherIssuer, entries: [writer => writer.WriteInteger(1)]);

        var act = () => RevokedCertificate.DecodeAll(crl);

        act.Should().Throw<AsnContentException>();
    }

    [Test]
    public void Revokes_EntryForTheCertificate_DependsOnIssuerSerialTimeAndRemoval()
    {
        using var certificate = CreateCertificate("CN=Chaos.Mongo Test TSA", OtherIssuer, SerialNumber);
        var entry = new RevokedCertificate(OtherIssuer.RawData, SerialNumber, RevokedAt, null, false);

        entry.Revokes(certificate, RevokedAt).Should().BeTrue();
        entry.Revokes(certificate, RevokedAt.AddSeconds(-1)).Should().BeFalse("the revocation lies after the time");
        (entry with
        {
            InvalidityDate = RevokedAt.AddHours(-1)
        }).Revokes(certificate, RevokedAt.AddSeconds(-1)).Should().BeTrue();
        (entry with
        {
            SerialNumber = new Byte[]
            {
                0x01,
                0x02,
                0x04
            }
        }).Revokes(certificate, RevokedAt).Should().BeFalse();
        (entry with
        {
            Issuer = new X500DistinguishedName("CN=Someone Else").RawData
        }).Revokes(certificate, RevokedAt).Should().BeFalse();
        (entry with
        {
            Issuer = null
        }).Revokes(certificate, RevokedAt).Should().BeTrue("an unknown issuer matches by serial number");
        (entry with
        {
            RemovedFromCrl = true
        }).Revokes(certificate, RevokedAt).Should().BeFalse();
        FluentActions.Invoking(() => entry.Revokes(null!, RevokedAt)).Should().Throw<ArgumentNullException>();
    }

    /// <summary>
    /// Creates a certificate with the given serial number, self-signed or with <paramref name="issuer"/> as its issuer
    /// name.
    /// </summary>
    private static X509Certificate2 CreateCertificate(String subject, X500DistinguishedName? issuer, Byte[] serialNumber)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(issuer is null, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature, true));
        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = DateTimeOffset.UtcNow.AddDays(1);
        if (issuer is null)
        {
            return request.CreateSelfSigned(notBefore, notAfter);
        }

        using var certificate = request.Create(issuer, X509SignatureGenerator.CreateForECDsa(key), notBefore, notAfter, serialNumber);
        return certificate.CopyWithPrivateKey(key);
    }

    /// <summary>
    /// Encodes an unsigned CRL; the CRL's signature is never verified.
    /// </summary>
    private static Byte[] EncodeCrl(X500DistinguishedName issuer, Boolean withEntries = true, params Action<AsnWriter>[] entries)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence())
            {
                writer.WriteInteger(1);
                WriteEcdsaWithSha256(writer);
                writer.WriteEncodedValue(issuer.RawData);
                writer.WriteUtcTime(RevokedAt.AddDays(1));
                writer.WriteUtcTime(RevokedAt.AddDays(8));
                if (withEntries)
                {
                    using (writer.PushSequence())
                    {
                        foreach (var entry in entries)
                        {
                            using (writer.PushSequence())
                            {
                                entry(writer);
                            }
                        }
                    }
                }
            }

            WriteEcdsaWithSha256(writer);
            writer.WriteBitString([0x00]);
        }

        return writer.Encode();

        static void WriteEcdsaWithSha256(AsnWriter writer)
        {
            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier("1.2.840.10045.4.3.2");
            }
        }
    }

    private static Byte[] EncodeDirectoryNames(X500DistinguishedName name)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 4, true)))
        {
            writer.WriteEncodedValue(name.RawData);
        }

        return writer.Encode();
    }

    private static Byte[] EncodeDnsName(String name)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteCharacterString(UniversalTagNumber.IA5String, name, new Asn1Tag(TagClass.ContextSpecific, 2));
        }

        return writer.Encode();
    }

    private static Byte[] EncodeGeneralizedTime(DateTimeOffset time)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.WriteGeneralizedTime(time);
        return writer.Encode();
    }

    private static Byte[] EncodeReason(Int32 reason)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.WriteEnumeratedValue((X509RevocationReason)reason);
        return writer.Encode();
    }

    /// <summary>
    /// Writes the content of a revoked certificate entry. A certificate issuer extension is marked critical, as RFC 5280
    /// requires; the others leave the default.
    /// </summary>
    private static Action<AsnWriter> Entry(Byte[] serialNumber, DateTimeOffset revokedAt, params (String Oid, Byte[] Value)[] extensions)
        => writer =>
        {
            writer.WriteIntegerUnsigned(serialNumber);
            writer.WriteUtcTime(revokedAt);
            if (extensions.Length == 0)
            {
                return;
            }

            using (writer.PushSequence())
            {
                foreach (var (oid, value) in extensions)
                {
                    using (writer.PushSequence())
                    {
                        writer.WriteObjectIdentifier(oid);
                        if (oid == CertificateIssuerOid)
                        {
                            writer.WriteBoolean(true);
                        }

                        writer.WriteOctetString(value);
                    }
                }
            }
        };
}
