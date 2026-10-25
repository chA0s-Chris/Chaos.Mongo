// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity;

using Chaos.Mongo.EventStore.Integrity;
using FluentAssertions;
using NUnit.Framework;
using System.Formats.Asn1;
using System.Security.Cryptography;
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

        caSources.Should().ContainSingle().Which.FromCaCertificate.Should().BeTrue();
        signerSources.Should().OnlyContain(source => !source.FromCaCertificate).And.NotBeEmpty();
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
