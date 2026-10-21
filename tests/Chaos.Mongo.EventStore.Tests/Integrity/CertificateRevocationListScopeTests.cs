// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity;

using Chaos.Mongo.EventStore.Integrity;
using FluentAssertions;
using NUnit.Framework;
using System.Formats.Asn1;

public class CertificateRevocationListScopeTests
{
    private const String CrlNumberOid = "2.5.29.20";
    private const String DeltaCrlIndicatorOid = "2.5.29.27";
    private const String IssuingDistributionPointOid = "2.5.29.28";
    private static readonly Uri OtherUri = new("http://other.test/ca.crl");
    private TestTimestampAuthority _authority;

    [SetUp]
    public void CreateAuthority() => _authority = new TestTimestampAuthority();

    [TearDown]
    public void DisposeAuthority() => _authority.Dispose();

    [Test]
    [TestCase("delta", false)]
    [TestCase("unknownCritical", false)]
    [TestCase("duplicate", false)]
    [TestCase("otherPoint", false)]
    [TestCase("relativeName", false)]
    [TestCase("someReasons", false)]
    [TestCase("caCertificates", false)]
    [TestCase("indirect", false)]
    [TestCase("attributeCertificates", false)]
    [TestCase("none", true)]
    [TestCase("crlNumberCritical", true)]
    [TestCase("unknownNonCritical", true)]
    [TestCase("ownPoint", true)]
    [TestCase("pointWithoutName", true)]
    [TestCase("userCertificates", true)]
    public void IsCompleteFor_CrlExtensions_DecidesCompleteness(String extensions, Boolean expected)
    {
        Byte[] crlNumber = [0x02, 0x01, 0x01];
        (String, Boolean, Byte[])[] crlExtensions = extensions switch
        {
            "delta" => [(DeltaCrlIndicatorOid, true, crlNumber)],
            "unknownCritical" => [("1.2.3.4", true, [0x05, 0x00])],
            "duplicate" => [(CrlNumberOid, false, crlNumber), (CrlNumberOid, false, crlNumber)],
            "otherPoint" => [Point(OtherUri)],
            "relativeName" => [(IssuingDistributionPointOid, true, EncodePoint(null, relativeName: true))],
            "someReasons" => [Point(TestTimestampAuthority.CrlUri, 3)],
            "caCertificates" => [Point(TestTimestampAuthority.CrlUri, 2)],
            "indirect" => [Point(TestTimestampAuthority.CrlUri, 4)],
            "attributeCertificates" => [Point(TestTimestampAuthority.CrlUri, 5)],
            "none" => [],
            "crlNumberCritical" => [(CrlNumberOid, true, crlNumber)],
            "unknownNonCritical" => [("1.2.3.4", false, [0x05, 0x00])],
            "ownPoint" => [(CrlNumberOid, false, crlNumber), Point(TestTimestampAuthority.CrlUri)],
            "pointWithoutName" => [(IssuingDistributionPointOid, true, EncodePoint(null))],
            _ => [Point(TestTimestampAuthority.CrlUri, 1)]
        };
        var crl = _authority.BuildCrlWithExtensions(DateTimeOffset.UtcNow, crlExtensions);

        var result = CertificateRevocationListScope.IsCompleteFor(crl, [TestTimestampAuthority.CrlMirrorUri, TestTimestampAuthority.CrlUri]);

        result.Should().Be(expected);
    }

    [Test]
    public void IsCompleteFor_CrlWithRevokedEntries_ReadsExtensionsAfterThem()
    {
        var crl = _authority.BuildCrl(DateTimeOffset.UtcNow, null, ([1, 2, 3], DateTimeOffset.UtcNow.AddDays(-1)));

        CertificateRevocationListScope.IsCompleteFor(crl, [TestTimestampAuthority.CrlUri]).Should().BeTrue();
    }

    [Test]
    public void IsCompleteFor_NotACrl_ThrowsAsnContentException()
    {
        var act = () => CertificateRevocationListScope.IsCompleteFor(new Byte[]
        {
            1,
            2,
            3
        }, [TestTimestampAuthority.CrlUri]);

        act.Should().Throw<AsnContentException>();
    }

    /// <summary>
    /// Encodes an issuing distribution point: a full name with the location or, with <paramref name="relativeName"/>,
    /// a name relative to the CRL issuer, followed by the field with the given context-specific tag when given.
    /// </summary>
    private static Byte[] EncodePoint(Uri? location, Int32? field = null, Boolean relativeName = false)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            if (relativeName)
            {
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
                using (writer.PushSetOf(new Asn1Tag(TagClass.ContextSpecific, 1, true)))
                using (writer.PushSequence())
                {
                    writer.WriteObjectIdentifier("2.5.4.3");
                    writer.WriteCharacterString(UniversalTagNumber.UTF8String, "CRL");
                }
            }
            else if (location is { } uri)
            {
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
                {
                    writer.WriteCharacterString(UniversalTagNumber.IA5String, uri.AbsoluteUri, new Asn1Tag(TagClass.ContextSpecific, 6));
                }
            }

            if (field == 3)
            {
                writer.WriteBitString([0x60], 5, new Asn1Tag(TagClass.ContextSpecific, 3));
            }
            else if (field is { } tag)
            {
                writer.WriteBoolean(true, new Asn1Tag(TagClass.ContextSpecific, tag));
            }
        }

        return writer.Encode();
    }

    private static (String, Boolean, Byte[]) Point(Uri location, Int32? field = null)
        => (IssuingDistributionPointOid, true, EncodePoint(location, field));
}
