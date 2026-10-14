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
    private const String DistributionPointsOid = "2.5.29.31";
    private const String PlaceholderOid = "2.5.29.99";
    private static readonly Byte[] PlaceholderOidEncoding = [0x06, 0x03, 0x55, 0x1D, 0x63];
    private static readonly Asn1Tag UriTag = new(TagClass.ContextSpecific, 6);

    [Test]
    public void GetHttpDistributionPoints_DuplicateExtension_ThrowsAsnContentException()
    {
        var distributionPoints = TestTimestampAuthority.EncodeDistributionPoints(["http://crl.test/a.crl"]);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Distribution Points", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509Extension(DistributionPointsOid, distributionPoints, false));

        // CertificateRequest refuses duplicate extensions, so the second one is added under a placeholder OID of the
        // same encoded length and renamed in the encoded certificate. Loading does not verify the signature.
        request.CertificateExtensions.Add(new X509Extension(PlaceholderOid, distributionPoints, false));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var encoded = created.RawData;
        var placeholder = encoded.AsSpan().IndexOf(PlaceholderOidEncoding);
        placeholder.Should().BePositive();
        encoded[placeholder + 4] = 0x1F;

        var act = () =>
        {
            using var certificate = X509CertificateLoader.LoadCertificate(encoded);
            return CrlDistributionPoints.GetHttpDistributionPoints(certificate);
        };

        act.Should().Throw<AsnContentException>();
    }

    [Test]
    public void GetHttpDistributionPoints_DuplicateNameFieldInDistributionPoint_ThrowsAsnContentException()
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        using (writer.PushSequence())
        {
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            {
                WriteUris(writer, "http://crl.test/a.crl");
            }

            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            {
                WriteUris(writer, "http://crl.test/b.crl");
            }
        }

        var act = () =>
        {
            using var certificate = CreateCertificate(writer.Encode());
            return CrlDistributionPoints.GetHttpDistributionPoints(certificate);
        };

        act.Should().Throw<AsnContentException>();
    }

    [Test]
    public void GetHttpDistributionPoints_DuplicateNameInDistributionPoint_ThrowsAsnContentException()
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
            return CrlDistributionPoints.GetHttpDistributionPoints(certificate);
        };

        act.Should().Throw<AsnContentException>();
    }

    [Test]
    public void GetHttpDistributionPoints_EmptyCrlIssuer_ThrowsAsnContentException()
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        using (writer.PushSequence())
        {
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            {
                WriteUris(writer, "http://crl.test/a.crl");
            }

            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 2)))
            {
                // A CRL issuer must name at least one general name.
            }
        }

        var act = () =>
        {
            using var certificate = CreateCertificate(writer.Encode());
            return CrlDistributionPoints.GetHttpDistributionPoints(certificate);
        };

        act.Should().Throw<AsnContentException>();
    }

    [Test]
    public void GetHttpDistributionPoints_EmptyFullName_ThrowsAsnContentException()
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            WriteFullNamePoint(writer, _ => { });
            WriteFullNamePoint(writer, names => WriteUris(names, "http://crl.test/a.crl"));
        }

        var act = () =>
        {
            using var certificate = CreateCertificate(writer.Encode());
            return CrlDistributionPoints.GetHttpDistributionPoints(certificate);
        };

        act.Should().Throw<AsnContentException>();
    }

    [Test]
    public void GetHttpDistributionPoints_InvalidGeneralNameInFullName_ThrowsAsnContentException()
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            WriteFullNamePoint(writer, names =>
            {
                WriteUris(names, "http://crl.test/a.crl");
                names.WriteNull();
            });
        }

        var act = () =>
        {
            using var certificate = CreateCertificate(writer.Encode());
            return CrlDistributionPoints.GetHttpDistributionPoints(certificate);
        };

        act.Should().Throw<AsnContentException>();
    }

    [Test]
    public void GetHttpDistributionPoints_MalformedExtension_ThrowsAsnContentException()
    {
        var act = () =>
        {
            using var certificate = CreateCertificate([0x30, 0x03, 0x02, 0x01, 0x00]);
            return CrlDistributionPoints.GetHttpDistributionPoints(certificate);
        };

        act.Should().Throw<AsnContentException>();
    }

    [Test]
    public void GetHttpDistributionPoints_MalformedReasons_ThrowsAsnContentException()
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        using (writer.PushSequence())
        {
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            {
                WriteUris(writer, "http://crl.test/a.crl");
            }

            // An empty [1] value is not a BIT STRING.
            writer.WriteEncodedValue([0x81, 0x00]);
        }

        var act = () =>
        {
            using var certificate = CreateCertificate(writer.Encode());
            return CrlDistributionPoints.GetHttpDistributionPoints(certificate);
        };

        act.Should().Throw<AsnContentException>();
    }

    [Test]
    public void GetHttpDistributionPoints_MixedDistributionPoints_ReturnsHttpLocationsPerPoint()
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
            using (writer.PushSequence())
            {
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
                {
                    WriteUris(writer, "http://crl.test/c.crl");
                }

                // Reasons and the CRL issuer may follow the name; the issuer's locations are not distribution points.
                writer.WriteBitString([0x80], 0, new Asn1Tag(TagClass.ContextSpecific, 1));
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 2)))
                {
                    WriteUris(writer, "http://issuer.test/ca");
                }
            }
        }

        using var certificate = CreateCertificate(writer.Encode());

        var points = CrlDistributionPoints.GetHttpDistributionPoints(certificate);

        points.Should().HaveCount(3);
        points[0].Locations.Should().Equal(new Uri("http://crl.test/a.crl"), new Uri("https://crl.test/a.crl"));
        points[0].CoversAllReasons.Should().BeTrue();
        points[1].Locations.Should().Equal(new Uri("http://crl.test/b.crl"));
        points[1].CoversAllReasons.Should().BeTrue();
        points[2].Locations.Should().Equal(new Uri("http://crl.test/c.crl"));
        points[2].CoversAllReasons.Should().BeFalse();
    }

    [TestCase("40", 6, false)]
    [TestCase("7F", 0, false)]
    [TestCase("7F80", 7, true)]
    [TestCase("FF80", 7, true)]
    public void GetHttpDistributionPoints_PointWithReasons_ReportsWhetherItCoversAllReasons(String reasons, Int32 unusedBitCount, Boolean coversAllReasons)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        using (writer.PushSequence())
        {
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            {
                WriteUris(writer, "http://crl.test/a.crl");
            }

            writer.WriteBitString(Convert.FromHexString(reasons), unusedBitCount, new Asn1Tag(TagClass.ContextSpecific, 1));
        }

        using var certificate = CreateCertificate(writer.Encode());

        var points = CrlDistributionPoints.GetHttpDistributionPoints(certificate);

        points.Should().ContainSingle().Which.CoversAllReasons.Should().Be(coversAllReasons);
    }

    [Test]
    public void GetHttpDistributionPoints_ReasonsOnlyDistributionPoint_ThrowsAsnContentException()
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            WriteFullNamePoint(writer, names => WriteUris(names, "http://crl.test/a.crl"));
            using (writer.PushSequence())
            {
                writer.WriteBitString([0x80], 0, new Asn1Tag(TagClass.ContextSpecific, 1));
            }
        }

        var act = () =>
        {
            using var certificate = CreateCertificate(writer.Encode());
            return CrlDistributionPoints.GetHttpDistributionPoints(certificate);
        };

        act.Should().Throw<AsnContentException>();
    }

    [Test]
    public void GetHttpDistributionPoints_UnknownFieldInDistributionPoint_ThrowsAsnContentException()
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        using (writer.PushSequence())
        {
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            {
                WriteUris(writer, "http://crl.test/a.crl");
            }

            writer.WriteNull(new Asn1Tag(TagClass.ContextSpecific, 3));
        }

        var act = () =>
        {
            using var certificate = CreateCertificate(writer.Encode());
            return CrlDistributionPoints.GetHttpDistributionPoints(certificate);
        };

        act.Should().Throw<AsnContentException>();
    }

    [Test]
    public void GetHttpDistributionPoints_WithoutExtension_ReturnsEmpty()
    {
        using var certificate = CreateCertificate(null);

        CrlDistributionPoints.GetHttpDistributionPoints(certificate).Should().BeEmpty();
    }

    private static X509Certificate2 CreateCertificate(Byte[]? distributionPoints)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Distribution Points", key, HashAlgorithmName.SHA256);
        if (distributionPoints is not null)
        {
            request.CertificateExtensions.Add(new X509Extension(DistributionPointsOid, distributionPoints, false));
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
