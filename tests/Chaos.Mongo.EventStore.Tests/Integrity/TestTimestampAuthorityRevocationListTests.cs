// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity;

using Chaos.Mongo.EventStore.Integrity;
using FluentAssertions;
using NUnit.Framework;
using System.Formats.Asn1;

/// <summary>
/// Pins the directly encoded CRL of <see cref="TestTimestampAuthority"/> to what the BCL's
/// <c>CertificateRevocationListBuilder</c> would build, so tests that need large CRLs keep exercising the same evidence.
/// </summary>
public class TestTimestampAuthorityRevocationListTests
{
    private const Int32 RevokedCertificates = 1_000;
    private TestTimestampAuthority _authority;

    [OneTimeSetUp]
    public void CreateAuthority() => _authority = new TestTimestampAuthority(revokedCertificates: RevokedCertificates);

    [OneTimeTearDown]
    public void DisposeAuthority() => _authority.Dispose();

    [Test]
    public void BuildRevocationListCrl_SameInputAsTheBuilder_EncodesTheSameSignedPart()
    {
        var thisUpdate = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var nextUpdate = thisUpdate.AddDays(30);
        var revokedAt = thisUpdate.AddDays(-30);
        var revoked = Enumerable.Range(0, 3).Select(index => (TestTimestampAuthority.GetRevokedSerialNumber(index), revokedAt)).ToArray();

        var encoded = _authority.BuildRevocationListCrl(revoked.Length, revokedAt, thisUpdate, nextUpdate);
        var built = _authority.BuildCrl(thisUpdate, nextUpdate, revoked);

        // The signatures differ, because ECDSA signs with a random nonce; the signed part must be identical.
        ReadSignedPart(encoded).Should().Equal(ReadSignedPart(built));
    }

    [Test]
    public void Crl_WithRevokedCertificates_IsSignedByTheRoot()
        => CertificateRevocationListSignature.Verify(_authority.Crl, _authority.Root).Should().BeNull();

    [Test]
    public void Crl_WithRevokedCertificates_IsCompleteForTheDistributionPoint()
        => CertificateRevocationListScope.IsCompleteFor(_authority.Crl, [TestTimestampAuthority.CrlUri], fromCaCertificate: false, fromEndEntityCertificate: true)
                                         .Should().BeTrue();

    [Test]
    public void Crl_WithRevokedCertificates_ListsEverySequentialSerialNumber()
    {
        var revoked = RevokedCertificate.DecodeAll(_authority.Crl);

        revoked.Should().HaveCount(RevokedCertificates);
        revoked.Select(entry => entry.SerialNumber.ToArray())
               .Should().BeEquivalentTo(Enumerable.Range(0, RevokedCertificates).Select(index => TestTimestampAuthority.GetRevokedSerialNumber(index)),
                                        options => options.WithStrictOrdering());
    }

    private static Byte[] ReadSignedPart(Byte[] crl)
        => new AsnReader(crl, AsnEncodingRules.DER).ReadSequence().ReadEncodedValue().ToArray();
}
