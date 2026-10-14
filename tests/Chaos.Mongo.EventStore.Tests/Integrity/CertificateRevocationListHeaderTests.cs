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

public class CertificateRevocationListHeaderTests
{
    private static readonly DateTimeOffset NextUpdate = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ThisUpdate = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public void Decode_CrlFromIssuer_ReturnsIssuerAndUpdateTimes()
    {
        using var issuer = CreateIssuer();
        var crl = new CertificateRevocationListBuilder().Build(issuer, BigInteger.One, NextUpdate, HashAlgorithmName.SHA256, thisUpdate: ThisUpdate);

        var header = CertificateRevocationListHeader.Decode(crl);

        header.Issuer.ToArray().Should().Equal(issuer.SubjectName.RawData);
        header.ThisUpdate.Should().Be(ThisUpdate);
        header.NextUpdate.Should().Be(NextUpdate);
    }

    [Test]
    public void Decode_NotACertificateList_ThrowsAsnContentException()
    {
        var act = () => CertificateRevocationListHeader.Decode("<html></html>"u8.ToArray());

        act.Should().Throw<AsnContentException>();
    }

    [Test]
    public void Decode_TrailingData_ThrowsAsnContentException()
    {
        using var issuer = CreateIssuer();
        var crl = new CertificateRevocationListBuilder().Build(issuer, BigInteger.One, NextUpdate, HashAlgorithmName.SHA256, thisUpdate: ThisUpdate);

        var act = () => CertificateRevocationListHeader.Decode((Byte[])[.. crl, 0x05, 0x00]);

        act.Should().Throw<AsnContentException>();
    }

    [Test]
    public void Decode_UnsupportedVersion_ThrowsAsnContentException()
    {
        var act = () => CertificateRevocationListHeader.Decode(EncodeCrl(2, true));

        act.Should().Throw<AsnContentException>();
    }

    [Test]
    public void Decode_WithoutVersionAndNextUpdate_ReturnsNoNextUpdate()
    {
        var header = CertificateRevocationListHeader.Decode(EncodeCrl(null, false));

        header.ThisUpdate.Should().Be(ThisUpdate);
        header.NextUpdate.Should().BeNull();
    }

    private static X509Certificate2 CreateIssuer()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Chaos.Mongo CRL Issuer", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(ThisUpdate.AddDays(-1), NextUpdate.AddDays(1));
    }

    /// <summary>
    /// Encodes an unsigned CRL shell; the header does not depend on the signature.
    /// </summary>
    private static Byte[] EncodeCrl(Int32? version, Boolean nextUpdate)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence())
            {
                if (version is { } value)
                {
                    writer.WriteInteger(value);
                }

                WriteAlgorithm(writer);
                writer.WriteEncodedValue(new X500DistinguishedName("CN=Chaos.Mongo CRL Issuer").RawData);
                writer.WriteUtcTime(ThisUpdate);
                if (nextUpdate)
                {
                    writer.WriteUtcTime(NextUpdate);
                }
            }

            WriteAlgorithm(writer);
            writer.WriteBitString([0x00]);
        }

        return writer.Encode();
    }

    private static void WriteAlgorithm(AsnWriter writer)
    {
        using (writer.PushSequence())
        {
            // ecdsa-with-SHA256
            writer.WriteObjectIdentifier("1.2.840.10045.4.3.2");
        }
    }
}
