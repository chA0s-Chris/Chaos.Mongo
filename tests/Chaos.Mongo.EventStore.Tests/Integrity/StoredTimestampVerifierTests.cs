// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity;

using Chaos.Mongo.EventStore.Integrity;
using FluentAssertions;
using NUnit.Framework;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

public class StoredTimestampVerifierTests
{
    private static readonly TimeSpan GracePeriod = TimeSpan.FromHours(24);
    private readonly List<TestTimestampAuthority> _authorities = [];

    [TearDown]
    public void DisposeAuthorities()
    {
        foreach (var authority in _authorities)
        {
            authority.Dispose();
        }

        _authorities.Clear();
    }

    [Test]
    public async Task Verify_ChainWithoutDistributionPoints_IsConfirmedWithoutCrls()
    {
        var authority = CreateAuthority(new TestTimestampAuthority([]));
        var (timestamp, digest) = await StampAsync(authority);

        var result = StoredTimestampVerifier.Verify(timestamp with
                                                    {
                                                        RevocationStatus = TimestampRevocationStatus.Confirmed
                                                    },
                                                    digest,
                                                    Trust(authority),
                                                    GracePeriod);

        result.IsValid.Should().BeTrue();
        result.RevocationStatus.Should().Be(TimestampRevocationStatus.Confirmed);
    }

    [Test]
    public async Task Verify_ConfirmingCrlRevokesSignerAfterTokenTime_IsConfirmed()
    {
        var authority = CreateAuthority();
        var (timestamp, digest) = await StampAsync(authority);
        var crl = authority.BuildCrl(timestamp.TimestampUtc + GracePeriod + TimeSpan.FromMinutes(1),
                                     null,
                                     (authority.SigningCertificate.SerialNumberBytes.ToArray(), timestamp.TimestampUtc.AddMinutes(1)));

        var result = StoredTimestampVerifier.Verify(timestamp with
                                                    {
                                                        ConfirmingRevocationLists = [crl]
                                                    },
                                                    digest,
                                                    Trust(authority),
                                                    GracePeriod);

        result.IsValid.Should().BeTrue();
        result.RevocationStatus.Should().Be(TimestampRevocationStatus.Confirmed);
    }

    [Test]
    [TestCase("Revoked")]
    [TestCase("Confirmed")]
    public async Task Verify_ConfirmingCrlRevokesSignerBeforeTokenTime_IsInvalidWhateverTheStoredStatus(String storedStatusName)
    {
        var storedStatus = Enum.Parse<TimestampRevocationStatus>(storedStatusName);
        var authority = CreateAuthority();
        var (timestamp, digest) = await StampAsync(authority);
        var crl = authority.BuildCrl(timestamp.TimestampUtc + GracePeriod + TimeSpan.FromMinutes(1),
                                     null,
                                     (authority.SigningCertificate.SerialNumberBytes.ToArray(), timestamp.TimestampUtc.AddMinutes(-1)));

        var result = StoredTimestampVerifier.Verify(timestamp with
                                                    {
                                                        ConfirmingRevocationLists = [crl],
                                                        RevocationStatus = storedStatus
                                                    },
                                                    digest,
                                                    Trust(authority),
                                                    GracePeriod);

        result.IsValid.Should().BeFalse();
        result.Failure.Should().Contain("revoked");
        result.RevocationStatus.Should().Be(TimestampRevocationStatus.Revoked);
        result.StoredRevocationStatus.Should().Be(storedStatus);
    }

    [Test]
    public async Task Verify_ConfirmingCrl_IsValidAndConfirmed()
    {
        var authority = CreateAuthority();
        var (timestamp, digest) = await StampAsync(authority);
        timestamp = timestamp with
        {
            ConfirmingRevocationLists = [authority.BuildCrl(timestamp.TimestampUtc + GracePeriod + TimeSpan.FromMinutes(1))]
        };

        var result = StoredTimestampVerifier.Verify(timestamp, digest, Trust(authority), GracePeriod);

        result.Should().Be(new TimestampVerificationResult("test-tsa",
                                                           timestamp.TimestampUtc,
                                                           true,
                                                           null,
                                                           TimestampRevocationStatus.Confirmed,
                                                           TimestampRevocationStatus.Unconfirmed));
    }

    [Test]
    public async Task Verify_ConfirmingCrlsInAnotherOrder_AreMatchedByContent()
    {
        var authority = CreateAuthority(new TestTimestampAuthority(rawCrlDistributionPoints: TestTimestampAuthority.EncodeDistributionPoints(
                                                                       TestTimestampAuthority.CrlUri,
                                                                       TestTimestampAuthority.CrlMirrorUri)));
        var (timestamp, digest) = await StampAsync(authority);
        var lateEnough = timestamp.TimestampUtc + GracePeriod + TimeSpan.FromMinutes(1);
        var first = authority.BuildCrlWithExtensions(lateEnough, ("2.5.29.28", true, TestTimestampAuthority.EncodeIssuingDistributionPoint(TestTimestampAuthority.CrlUri)));
        var second = authority.BuildCrlWithExtensions(lateEnough,
                                                      [(authority.SigningCertificate.SerialNumberBytes.ToArray(), timestamp.TimestampUtc.AddMinutes(-1))],
                                                      ("2.5.29.28", true, TestTimestampAuthority.EncodeIssuingDistributionPoint(TestTimestampAuthority.CrlMirrorUri)));

        var result = StoredTimestampVerifier.Verify(timestamp with
                                                    {
                                                        ConfirmingRevocationLists = [second, first]
                                                    },
                                                    digest,
                                                    Trust(authority),
                                                    GracePeriod);

        result.IsValid.Should().BeFalse();
        result.RevocationStatus.Should().Be(TimestampRevocationStatus.Revoked);
    }

    [Test]
    public async Task Verify_CrlIssuerWithoutCrlSignUsage_StaysUnconfirmed()
    {
        var authority = CreateAuthority(new TestTimestampAuthority(rootKeyUsage: X509KeyUsageFlags.KeyCertSign));
        var (timestamp, digest) = await StampAsync(authority);

        var result = StoredTimestampVerifier.Verify(timestamp with
                                                    {
                                                        ConfirmingRevocationLists =
                                                        [authority.BuildCrlWithExtensions(timestamp.TimestampUtc + GracePeriod + TimeSpan.FromMinutes(1))]
                                                    },
                                                    digest,
                                                    Trust(authority),
                                                    GracePeriod);

        result.IsValid.Should().BeTrue();
        result.RevocationStatus.Should().Be(TimestampRevocationStatus.Unconfirmed);
    }

    [Test]
    public async Task Verify_CrlOfIndirectIssuer_StaysUnconfirmed()
    {
        var authority = CreateAuthority(new TestTimestampAuthority(rawCrlDistributionPoints: EncodePointWithCrlIssuer(TestTimestampAuthority.CrlUri)));
        var (timestamp, digest) = await StampAsync(authority);

        var result = StoredTimestampVerifier.Verify(timestamp with
                                                    {
                                                        ConfirmingRevocationLists = [authority.BuildCrl(timestamp.TimestampUtc + GracePeriod + TimeSpan.FromMinutes(1))]
                                                    },
                                                    digest,
                                                    Trust(authority),
                                                    GracePeriod);

        result.IsValid.Should().BeTrue();
        result.RevocationStatus.Should().Be(TimestampRevocationStatus.Unconfirmed);
    }

    [Test]
    public async Task Verify_DeltaCrl_StaysUnconfirmed()
    {
        var authority = CreateAuthority();
        var (timestamp, digest) = await StampAsync(authority);
        var lateEnough = timestamp.TimestampUtc + GracePeriod + TimeSpan.FromMinutes(1);
        Byte[] baseCrlNumber = [0x02, 0x01, 0x01];

        var delta = StoredTimestampVerifier.Verify(timestamp with
                                                   {
                                                       ConfirmingRevocationLists = [authority.BuildCrlWithExtensions(lateEnough, ("2.5.29.27", true, baseCrlNumber))]
                                                   },
                                                   digest,
                                                   Trust(authority),
                                                   GracePeriod);
        var complete = StoredTimestampVerifier.Verify(timestamp with
                                                      {
                                                          ConfirmingRevocationLists = [authority.BuildCrlWithExtensions(lateEnough, ("2.5.29.20", false, baseCrlNumber))]
                                                      },
                                                      digest,
                                                      Trust(authority),
                                                      GracePeriod);

        delta.IsValid.Should().BeTrue();
        delta.RevocationStatus.Should().Be(TimestampRevocationStatus.Unconfirmed);
        complete.RevocationStatus.Should().Be(TimestampRevocationStatus.Confirmed);
    }

    [Test]
    public async Task Verify_IndefiniteLengthToken_IsInvalid()
    {
        var authority = CreateAuthority();
        var (timestamp, digest) = await StampAsync(authority);
        AsnDecoder.ReadEncodedValue(timestamp.Token, AsnEncodingRules.DER, out var contentOffset, out var contentLength, out _);
        Byte[] berToken = [0x30, 0x80, .. timestamp.Token.AsSpan(contentOffset, contentLength), 0x00, 0x00];

        var result = StoredTimestampVerifier.Verify(timestamp with
                                                    {
                                                        Token = berToken
                                                    },
                                                    digest,
                                                    Trust(authority),
                                                    GracePeriod);

        result.IsValid.Should().BeFalse();
        result.Failure.Should().Contain("DER");
    }

    [Test]
    public async Task Verify_MalformedStoredCertificate_IsInvalid()
    {
        var authority = CreateAuthority();
        var (timestamp, digest) = await StampAsync(authority);

        var result = StoredTimestampVerifier.Verify(timestamp with
                                                    {
                                                        CertificateChain = [[1, 2, 3]]
                                                    },
                                                    digest,
                                                    Trust(authority),
                                                    GracePeriod);

        result.IsValid.Should().BeFalse();
        result.Failure.Should().Contain("malformed");
    }

    [Test]
    public async Task Verify_MalformedToken_IsInvalid()
    {
        var authority = CreateAuthority();
        var (timestamp, digest) = await StampAsync(authority);

        var result = StoredTimestampVerifier.Verify(timestamp with
                                                    {
                                                        Token = [1, 2, 3]
                                                    },
                                                    digest,
                                                    Trust(authority),
                                                    GracePeriod);

        result.IsValid.Should().BeFalse();
        result.Failure.Should().Contain("not a single RFC 3161 token");
    }

    [Test]
    public async Task Verify_NoTrustSettings_IsInvalid()
    {
        var authority = CreateAuthority();
        var (timestamp, digest) = await StampAsync(authority);

        var result = StoredTimestampVerifier.Verify(timestamp, digest, null, GracePeriod);

        result.IsValid.Should().BeFalse();
        result.Failure.Should().Contain("no trust settings");
    }

    [Test]
    public async Task Verify_OtherDigest_IsInvalid()
    {
        var authority = CreateAuthority();
        var (timestamp, _) = await StampAsync(authority);

        var result = StoredTimestampVerifier.Verify(timestamp, SHA256.HashData([9]), Trust(authority), GracePeriod);

        result.IsValid.Should().BeFalse();
        result.Failure.Should().Contain("imprint");
    }

    [Test]
    public async Task Verify_OtherPolicyRequired_IsInvalid()
    {
        var authority = CreateAuthority();
        var (timestamp, digest) = await StampAsync(authority);
        var trust = Trust(authority);
        trust.PolicyId = "1.3.6.1.4.1.99999.2";

        var result = StoredTimestampVerifier.Verify(timestamp, digest, trust, GracePeriod);

        result.IsValid.Should().BeFalse();
        result.Failure.Should().Contain("policy");
    }

    [Test]
    public async Task Verify_StoredChainDiffersFromValidatedChain_IsInvalid()
    {
        var authority = CreateAuthority();
        var (timestamp, digest) = await StampAsync(authority);

        var result = StoredTimestampVerifier.Verify(timestamp with
                                                    {
                                                        CertificateChain = timestamp.CertificateChain.Take(1).ToList()
                                                    },
                                                    digest,
                                                    Trust(authority),
                                                    GracePeriod);

        result.IsValid.Should().BeFalse();
        result.Failure.Should().Contain("stored certificate chain differs");
    }

    [Test]
    public async Task Verify_StoredRevokedWithCompleteCrlsThatDoNotRevoke_IsInconsistent()
    {
        var authority = CreateAuthority();
        var (timestamp, digest) = await StampAsync(authority);
        var complete = authority.BuildCrl(timestamp.TimestampUtc + GracePeriod + TimeSpan.FromMinutes(1));

        var result = StoredTimestampVerifier.Verify(timestamp with
                                                    {
                                                        ConfirmingRevocationLists = [complete],
                                                        RevocationStatus = TimestampRevocationStatus.Revoked
                                                    },
                                                    digest,
                                                    Trust(authority),
                                                    GracePeriod);

        result.IsValid.Should().BeFalse();
        result.Failure.Should().Contain("inconsistent").And.Contain("no stored CRL");
    }

    [Test]
    [TestCase(true)]
    [TestCase(false)]
    public async Task Verify_StoredRevokedWithRevokingCrlForOnePointOnly_IsRevoked(Boolean issuedWithinGracePeriod)
    {
        var authority = CreateAuthority(new TestTimestampAuthority(rawCrlDistributionPoints: TestTimestampAuthority.EncodeDistributionPoints(
                                                                       TestTimestampAuthority.CrlUri,
                                                                       TestTimestampAuthority.CrlMirrorUri)));
        var (timestamp, digest) = await StampAsync(authority);
        var issued = timestamp.TimestampUtc + (issuedWithinGracePeriod ? TimeSpan.FromMinutes(1) : GracePeriod + TimeSpan.FromMinutes(1));
        var revoking = authority.BuildCrlWithExtensions(issued,
                                                        [(authority.SigningCertificate.SerialNumberBytes.ToArray(), timestamp.TimestampUtc.AddMinutes(-1))],
                                                        ("2.5.29.28", true, TestTimestampAuthority.EncodeIssuingDistributionPoint(TestTimestampAuthority.CrlMirrorUri)));

        var result = StoredTimestampVerifier.Verify(timestamp with
                                                    {
                                                        ConfirmingRevocationLists = [revoking],
                                                        RevocationStatus = TimestampRevocationStatus.Revoked
                                                    },
                                                    digest,
                                                    Trust(authority),
                                                    GracePeriod);

        result.IsValid.Should().BeFalse();
        result.RevocationStatus.Should().Be(TimestampRevocationStatus.Revoked);
        result.Failure.Should().Contain("revoked at or before");
    }

    [Test]
    [TestCase("substituted")]
    [TestCase("partiallyRemoved")]
    [TestCase("complete")]
    public async Task Verify_StoredStatusWithInconsistentCrls_IsInvalid(String evidence)
    {
        var authority = CreateAuthority(new TestTimestampAuthority(rawCrlDistributionPoints: TestTimestampAuthority.EncodeDistributionPoints(
                                                                       TestTimestampAuthority.CrlUri,
                                                                       TestTimestampAuthority.CrlMirrorUri)));
        var (timestamp, digest) = await StampAsync(authority);
        var lateEnough = timestamp.TimestampUtc + GracePeriod + TimeSpan.FromMinutes(1);
        var first = authority.BuildCrlWithExtensions(lateEnough, ("2.5.29.28", true, TestTimestampAuthority.EncodeIssuingDistributionPoint(TestTimestampAuthority.CrlUri)));
        var second = authority.BuildCrlWithExtensions(lateEnough, ("2.5.29.28", true, TestTimestampAuthority.EncodeIssuingDistributionPoint(TestTimestampAuthority.CrlMirrorUri)));
        IReadOnlyList<Byte[]> lists = evidence switch
        {
            "substituted" => [timestamp.CertificateRevocationLists[0], second],
            "partiallyRemoved" => [first],
            _ => [first, second]
        };

        var result = StoredTimestampVerifier.Verify(timestamp with
                                                    {
                                                        ConfirmingRevocationLists = lists,
                                                        RevocationStatus = TimestampRevocationStatus.Confirmed
                                                    },
                                                    digest,
                                                    Trust(authority),
                                                    GracePeriod);

        result.IsValid.Should().Be(evidence == "complete", result.Failure);
        if (evidence != "complete")
        {
            result.Failure.Should().Contain("inconsistent");
        }
    }

    [Test]
    [TestCase("Confirmed")]
    [TestCase("Revoked")]
    public async Task Verify_StoredStatusWithoutConfirmingCrls_IsInvalid(String storedStatusName)
    {
        var storedStatus = Enum.Parse<TimestampRevocationStatus>(storedStatusName);
        var authority = CreateAuthority();
        var (timestamp, digest) = await StampAsync(authority);

        var result = StoredTimestampVerifier.Verify(timestamp with
                                                    {
                                                        RevocationStatus = storedStatus
                                                    },
                                                    digest,
                                                    Trust(authority),
                                                    GracePeriod);

        result.IsValid.Should().BeFalse();
        result.Failure.Should().Contain("inconsistent");
        result.StoredRevocationStatus.Should().Be(storedStatus);
    }

    [Test]
    public async Task Verify_StoredTimeDiffersFromTokenTime_IsInvalid()
    {
        var authority = CreateAuthority();
        var (timestamp, digest) = await StampAsync(authority);

        var result = StoredTimestampVerifier.Verify(timestamp with
                                                    {
                                                        TimestampUtc = timestamp.TimestampUtc.AddSeconds(1)
                                                    },
                                                    digest,
                                                    Trust(authority),
                                                    GracePeriod);

        result.IsValid.Should().BeFalse();
        result.Failure.Should().Contain("differs from the stored time");
    }

    [Test]
    public async Task Verify_TrustSettingsWithOtherRoot_IsInvalid()
    {
        var authority = CreateAuthority();
        var other = CreateAuthority();
        var (timestamp, digest) = await StampAsync(authority);

        var result = StoredTimestampVerifier.Verify(timestamp, digest, Trust(other), GracePeriod);

        result.IsValid.Should().BeFalse();
        result.Failure.Should().Contain("not trusted");
    }

    [Test]
    public async Task Verify_UnusableConfirmingCrls_StayUnconfirmed()
    {
        var authority = CreateAuthority();
        var (timestamp, digest) = await StampAsync(authority);
        var lateEnough = timestamp.TimestampUtc + GracePeriod + TimeSpan.FromMinutes(1);
        var brokenSignature = authority.BuildCrl(lateEnough);
        brokenSignature[^1] ^= 0x01;
        Byte[][] unusable =
        [
            authority.BuildCrl(timestamp.TimestampUtc.AddHours(1)),
            authority.OtherIssuerCrl,
            brokenSignature,
            [1, 2, 3]
        ];

        foreach (var crl in unusable)
        {
            var result = StoredTimestampVerifier.Verify(timestamp with
                                                        {
                                                            ConfirmingRevocationLists = [crl]
                                                        },
                                                        digest,
                                                        Trust(authority),
                                                        GracePeriod);

            result.IsValid.Should().BeTrue();
            result.RevocationStatus.Should().Be(TimestampRevocationStatus.Unconfirmed);
        }

        // CRLs are matched with distribution points by content, so an unusable CRL beside a usable one does no harm.
        var withExtra = StoredTimestampVerifier.Verify(timestamp with
                                                       {
                                                           ConfirmingRevocationLists = [authority.OtherIssuerCrl, authority.BuildCrl(lateEnough)]
                                                       },
                                                       digest,
                                                       Trust(authority),
                                                       GracePeriod);
        withExtra.RevocationStatus.Should().Be(TimestampRevocationStatus.Confirmed);
    }

    [Test]
    public async Task Verify_WithoutConfirmingCrls_IsValidButUnconfirmed()
    {
        var authority = CreateAuthority();
        var (timestamp, digest) = await StampAsync(authority);

        var result = StoredTimestampVerifier.Verify(timestamp, digest, Trust(authority), GracePeriod);

        result.IsValid.Should().BeTrue();
        result.RevocationStatus.Should().Be(TimestampRevocationStatus.Unconfirmed);
    }

    /// <summary>
    /// Encodes one distribution point with a location and a CRL issuer, so its CRL comes from an indirect issuer.
    /// </summary>
    private static Byte[] EncodePointWithCrlIssuer(Uri location)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        using (writer.PushSequence())
        {
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
            {
                writer.WriteCharacterString(UniversalTagNumber.IA5String, location.AbsoluteUri, new Asn1Tag(TagClass.ContextSpecific, 6));
            }

            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 2, true)))
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 4, true)))
            {
                writer.WriteEncodedValue(new X500DistinguishedName(TestTimestampAuthority.OtherIssuerName).RawData);
            }
        }

        return writer.Encode();
    }

    private static async Task<(AnchorTimestamp Timestamp, Byte[] Digest)> StampAsync(TestTimestampAuthority authority)
    {
        var digest = SHA256.HashData(Guid.NewGuid().ToByteArray());
        var token = await authority.CreateAuthority().TimestampAsync(digest, HashAlgorithmName.SHA256, CancellationToken.None);
        var ticks = token.Timestamp.UtcDateTime.Ticks;
        var storedTime = new DateTime(ticks - (ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);
        return (new AnchorTimestamp("test-tsa", token.Token, token.CertificateChain, token.CertificateRevocationLists, storedTime, DateTime.UtcNow), digest);
    }

    private static TimestampAuthorityTrust Trust(TestTimestampAuthority authority)
    {
        var trust = new TimestampAuthorityTrust();
        trust.TrustedRoots.Add(authority.Root);
        return trust;
    }

    private TestTimestampAuthority CreateAuthority(TestTimestampAuthority? authority = null)
    {
        authority ??= new TestTimestampAuthority();
        _authorities.Add(authority);
        return authority;
    }
}
