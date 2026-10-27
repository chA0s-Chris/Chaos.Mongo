// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity;

using Chaos.Mongo.EventStore.Integrity;
using FluentAssertions;
using NUnit.Framework;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

public class TimestampTokenValidationTests
{
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
}
