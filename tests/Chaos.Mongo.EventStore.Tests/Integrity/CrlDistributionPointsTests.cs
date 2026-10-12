// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity;

using Chaos.Mongo.EventStore.Integrity;
using FluentAssertions;
using NUnit.Framework;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

public class CrlDistributionPointsTests
{
    private static readonly Asn1Tag UriTag = new(TagClass.ContextSpecific, 6);

    [Test]
    public void GetHttpUris_DuplicateNameInDistributionPoint_ThrowsAsnContentException()
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        using (writer.PushSequence())
        using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
        {
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            {
                WriteUris(writer, "http://crl.test/a.crl");
            }

            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            {
                WriteUris(writer, "http://crl.test/b.crl");
            }
        }

        var act = () =>
        {
            using var certificate = CreateCertificate(writer.Encode());
            return CrlDistributionPoints.GetHttpUris(certificate);
        };

        act.Should().Throw<AsnContentException>();
    }

    [Test]
    public void GetHttpUris_MalformedExtension_ThrowsAsnContentException()
    {
        var act = () =>
        {
            using var certificate = CreateCertificate([0x30, 0x03, 0x02, 0x01, 0x00]);
            return CrlDistributionPoints.GetHttpUris(certificate);
        };

        act.Should().Throw<AsnContentException>();
    }

    [Test]
    public void GetHttpUris_MixedDistributionPoints_ReturnsHttpLocationsPerPoint()
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            WriteFullNamePoint(writer, names =>
            {
                WriteUris(names, "ldap://ldap.test/cn=ca", "http://crl.test/a.crl");
                using (names.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 4)))
                using (names.PushSequence()) { }

                WriteUris(names, "https://crl.test/a.crl", "not a uri");
            });
            WriteFullNamePoint(writer, names => WriteUris(names, "ldap://ldap.test/cn=only"));
            using (writer.PushSequence())
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            using (writer.PushSetOf(new Asn1Tag(TagClass.ContextSpecific, 1)))
            {
                // A name relative to the CRL issuer has no location.
            }

            using (writer.PushSequence())
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            using (writer.PushSetOf(new Asn1Tag(TagClass.ContextSpecific, 1)))
            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier("2.5.4.3");
                writer.WriteCharacterString(UniversalTagNumber.UTF8String, "relative");
            }

            using (writer.PushSequence())
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 2)))
            {
                WriteUris(writer, "http://issuer.test/only-in-crl-issuer");
            }

            WriteFullNamePoint(writer, names => WriteUris(names, "http://crl.test/b.crl"));
        }

        using var certificate = CreateCertificate(writer.Encode());

        var points = CrlDistributionPoints.GetHttpUris(certificate);

        points.Should().HaveCount(2);
        points[0].Should().Equal(new Uri("http://crl.test/a.crl"), new Uri("https://crl.test/a.crl"));
        points[1].Should().Equal(new Uri("http://crl.test/b.crl"));
    }

    [Test]
    public void GetHttpUris_WithoutExtension_ReturnsEmpty()
    {
        using var certificate = CreateCertificate(null);

        CrlDistributionPoints.GetHttpUris(certificate).Should().BeEmpty();
    }

    private static X509Certificate2 CreateCertificate(Byte[]? distributionPoints)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Distribution Points", key, HashAlgorithmName.SHA256);
        if (distributionPoints is not null)
        {
            request.CertificateExtensions.Add(new X509Extension("2.5.29.31", distributionPoints, false));
        }

        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static void WriteFullNamePoint(AsnWriter writer, Action<AsnWriter> writeGeneralNames)
    {
        using (writer.PushSequence())
        using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
        using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
        {
            writeGeneralNames(writer);
        }
    }

    private static void WriteUris(AsnWriter writer, params String[] uris)
    {
        foreach (var uri in uris)
        {
            writer.WriteCharacterString(UniversalTagNumber.IA5String, uri, UriTag);
        }
    }
}
