// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity;

using Chaos.Mongo.EventStore.Integrity;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

public class Rfc3161TimestampAuthorityTests
{
    private readonly List<TestTimestampAuthority> _testAuthorities = [];

    [Test]
    public void Constructor_DefaultHttpClientFactory_CreatesAuthority()
    {
        var options = new Rfc3161TimestampAuthorityOptions
        {
            Name = "default",
            Uri = TestTimestampAuthority.TimestampUri
        };

        var authority = new Rfc3161TimestampAuthority(options, new ServiceCollection().BuildServiceProvider());

        authority.Name.Should().Be("default");
        options.RevocationMode.Should().Be(X509RevocationMode.Online);
    }

    [Test]
    public void Constructor_HttpClientFactoryReturnsNull_ThrowsInvalidOperationException()
    {
        var options = new Rfc3161TimestampAuthorityOptions
        {
            Name = "null-client",
            Uri = TestTimestampAuthority.TimestampUri,
            HttpClientFactory = _ => null!
        };

        var act = () => new Rfc3161TimestampAuthority(options, new ServiceCollection().BuildServiceProvider());

        act.Should().Throw<InvalidOperationException>().WithMessage("*returned null*");
    }

    [Test]
    public void Constructor_HttpClientFactory_ReceivesServiceProvider()
    {
        var serviceProvider = new ServiceCollection().BuildServiceProvider();
        IServiceProvider? received = null;
        var options = new Rfc3161TimestampAuthorityOptions
        {
            Name = "factory",
            Uri = TestTimestampAuthority.TimestampUri,
            HttpClientFactory = provider =>
            {
                received = provider;
                return new HttpClient();
            }
        };

        _ = new Rfc3161TimestampAuthority(options, serviceProvider);

        received.Should().BeSameAs(serviceProvider);
    }

    [TestCase("")]
    [TestCase(" ")]
    public void Constructor_MissingName_ThrowsArgumentException(String name)
    {
        var options = new Rfc3161TimestampAuthorityOptions
        {
            Name = name,
            Uri = TestTimestampAuthority.TimestampUri
        };

        var act = () => new Rfc3161TimestampAuthority(options, new ServiceCollection().BuildServiceProvider());

        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void Constructor_NonHttpUri_ThrowsArgumentException()
    {
        var options = new Rfc3161TimestampAuthorityOptions
        {
            Name = "ftp",
            Uri = new Uri("ftp://tsa.test/timestamp")
        };

        var act = () => new Rfc3161TimestampAuthority(options, new ServiceCollection().BuildServiceProvider());

        act.Should().Throw<ArgumentException>().WithMessage("*absolute HTTP or HTTPS*");
    }

    [Test]
    public void Constructor_NonPositiveAttemptTimeout_ThrowsArgumentOutOfRangeException()
    {
        var options = new Rfc3161TimestampAuthorityOptions
        {
            Name = "no-attempt-timeout",
            Uri = TestTimestampAuthority.TimestampUri,
            AttemptTimeout = TimeSpan.Zero
        };

        var act = () => new Rfc3161TimestampAuthority(options, new ServiceCollection().BuildServiceProvider());

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void Constructor_NonPositiveClockSkew_ThrowsArgumentOutOfRangeException()
    {
        var options = new Rfc3161TimestampAuthorityOptions
        {
            Name = "no-skew",
            Uri = TestTimestampAuthority.TimestampUri,
            MaximumClockSkew = TimeSpan.Zero
        };

        var act = () => new Rfc3161TimestampAuthority(options, new ServiceCollection().BuildServiceProvider());

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void Constructor_NonPositiveMaximumResponseSize_ThrowsArgumentOutOfRangeException(Int32 maximumResponseSize)
    {
        var options = new Rfc3161TimestampAuthorityOptions
        {
            Name = "no-response-size",
            Uri = TestTimestampAuthority.TimestampUri,
            MaximumResponseSize = maximumResponseSize
        };

        var act = () => new Rfc3161TimestampAuthority(options, new ServiceCollection().BuildServiceProvider());

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void Constructor_NonPositiveRevocationCheckTimeout_ThrowsArgumentOutOfRangeException()
    {
        var options = new Rfc3161TimestampAuthorityOptions
        {
            Name = "no-revocation-timeout",
            Uri = TestTimestampAuthority.TimestampUri,
            RevocationCheckTimeout = TimeSpan.Zero
        };

        var act = () => new Rfc3161TimestampAuthority(options, new ServiceCollection().BuildServiceProvider());

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void Constructor_RelativeUri_ThrowsArgumentException()
    {
        var options = new Rfc3161TimestampAuthorityOptions
        {
            Name = "relative",
            Uri = new Uri("/timestamp", UriKind.Relative)
        };

        var act = () => new Rfc3161TimestampAuthority(options, new ServiceCollection().BuildServiceProvider());

        act.Should().Throw<ArgumentException>().WithMessage("*absolute HTTP or HTTPS*");
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var testAuthority in _testAuthorities)
        {
            testAuthority.Dispose();
        }

        _testAuthorities.Clear();
    }

    [Test]
    public async Task TimestampAsync_Cancelled_ThrowsOperationCanceledException()
    {
        var authority = CreateTestAuthority().CreateAuthority();

        var act = () => authority.TimestampAsync(CreateDigest(), HashAlgorithmName.SHA256, new CancellationToken(true));

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [TestCase(TestTimestampAuthorityMode.CrlOversized)]
    [TestCase(TestTimestampAuthorityMode.CrlOversizedWithoutLength)]
    public async Task TimestampAsync_CrlAboveMaximumResponseSize_Throws(TestTimestampAuthorityMode mode)
    {
        var testAuthority = CreateTestAuthority();
        testAuthority.Mode = mode;
        var authority = testAuthority.CreateAuthority(configure: options => options.MaximumResponseSize = TestTimestampAuthority.OversizedLength - 1);

        var act = () => authority.TimestampAsync(CreateDigest(), HashAlgorithmName.SHA256, CancellationToken.None);

        var exception = (await act.Should().ThrowAsync<TimestampAuthorityException>()).Which;
        exception.Message.Should().Contain("could not be downloaded");
        exception.InnerException.Should().BeOfType<InvalidDataException>().Which.Message.Should().Contain("maximum response size");
    }

    [Test]
    public async Task TimestampAsync_CrlConnectionLostWhileReading_Throws()
    {
        var exception = await TimestampExpectingFailureAsync(TestTimestampAuthorityMode.CrlConnectionLost);

        exception.Message.Should().Contain("could not be downloaded");
        exception.InnerException.Should().BeAssignableTo<IOException>();
    }

    [Test]
    public async Task TimestampAsync_CrlDownloadHangs_FailsAtTheAttemptTimeout()
    {
        var testAuthority = CreateTestAuthority();
        testAuthority.Mode = TestTimestampAuthorityMode.CrlHangs;
        var authority = testAuthority.CreateAuthority(configure: options => options.AttemptTimeout = TimeSpan.FromMilliseconds(200));

        var act = () => authority.TimestampAsync(CreateDigest(), HashAlgorithmName.SHA256, CancellationToken.None);

        (await act.Should().ThrowAsync<TimestampAuthorityException>()).Which.Message.Should().Contain("attempt timeout");
    }

    [Test]
    public async Task TimestampAsync_CrlUnavailableAtEveryLocation_Throws()
    {
        var exception = await TimestampExpectingFailureAsync(TestTimestampAuthorityMode.CrlUnavailable);

        exception.Message.Should().Contain(TestTimestampAuthority.CrlUri.AbsoluteUri);
        exception.InnerException.Should().BeOfType<HttpRequestException>();
    }

    [Test]
    public async Task TimestampAsync_CrlUnavailableAtFirstLocation_DownloadsItFromTheNext()
    {
        var testAuthority = CreateTestAuthority();
        testAuthority.Mode = TestTimestampAuthorityMode.CrlPrimaryUnavailable;

        var token = await testAuthority.CreateAuthority().TimestampAsync(CreateDigest(), HashAlgorithmName.SHA256, CancellationToken.None);

        token.CertificateRevocationLists.Should().ContainSingle().Which.Should().Equal(testAuthority.Crl);
    }

    [Test]
    public async Task TimestampAsync_DistributionPointsWithoutHttpLocation_Throws()
    {
        var authority = CreateTestAuthority(rawCrlDistributionPoints: TestTimestampAuthority.EncodeDistributionPoints(["ldap://ldap.test/cn=ca"]))
            .CreateAuthority();

        var act = () => authority.TimestampAsync(CreateDigest(), HashAlgorithmName.SHA256, CancellationToken.None);

        (await act.Should().ThrowAsync<TimestampAuthorityException>()).Which.Message.Should().Contain("none with an HTTP or HTTPS location covers all revocation reasons");
    }

    [Test]
    public async Task TimestampAsync_EmptyCrl_Throws()
    {
        var exception = await TimestampExpectingFailureAsync(TestTimestampAuthorityMode.CrlEmpty);

        exception.InnerException.Should().BeOfType<InvalidDataException>();
    }

    [Test]
    public async Task TimestampAsync_HttpAndLdapDistributionPoints_SkipsTheLdapPoint()
    {
        var testAuthority = CreateTestAuthority(rawCrlDistributionPoints: TestTimestampAuthority.EncodeDistributionPoints(
                                                    ["ldap://ldap.test/cn=ca"],
                                                    [TestTimestampAuthority.CrlUri.AbsoluteUri]));

        var token = await testAuthority.CreateAuthority().TimestampAsync(CreateDigest(), HashAlgorithmName.SHA256, CancellationToken.None);

        token.CertificateRevocationLists.Should().ContainSingle().Which.Should().Equal(testAuthority.Crl);
    }

    [TestCase(TestTimestampAuthorityMode.HttpFailure)]
    [TestCase(TestTimestampAuthorityMode.Unreachable)]
    [TestCase(TestTimestampAuthorityMode.Timeout)]
    public async Task TimestampAsync_HttpFailure_ThrowsWithAuthorityName(TestTimestampAuthorityMode mode)
    {
        var exception = await TimestampExpectingFailureAsync(mode);

        exception.AuthorityName.Should().Be("test-tsa");
        exception.Message.Should().Contain("timestamp request failed");
    }

    [TestCase(TestTimestampAuthorityMode.WrongImprint)]
    [TestCase(TestTimestampAuthorityMode.WrongNonce)]
    [TestCase(TestTimestampAuthorityMode.BrokenSignature)]
    [TestCase(TestTimestampAuthorityMode.WrongEssCertId)]
    public async Task TimestampAsync_InvalidToken_Throws(TestTimestampAuthorityMode mode)
    {
        var exception = await TimestampExpectingFailureAsync(mode);

        exception.Message.Should().Contain("does not match the request");
        exception.InnerException.Should().BeOfType<CryptographicException>();
    }

    [Test]
    public async Task TimestampAsync_MalformedCrlDistributionPoint_Throws()
    {
        var authority = CreateTestAuthority(rawCrlDistributionPoints: [0x30, 0x03, 0x02, 0x01, 0x00]).CreateAuthority();

        var act = () => authority.TimestampAsync(CreateDigest(), HashAlgorithmName.SHA256, CancellationToken.None);

        (await act.Should().ThrowAsync<TimestampAuthorityException>()).Which.AuthorityName.Should().Be("test-tsa");
    }

    [Test]
    public async Task TimestampAsync_MalformedKeyUsage_Throws()
    {
        // A NULL instead of the key usage BIT STRING.
        var authority = CreateTestAuthority(rawKeyUsage: [0x05, 0x00]).CreateAuthority();

        var act = () => authority.TimestampAsync(CreateDigest(), HashAlgorithmName.SHA256, CancellationToken.None);

        var exception = (await act.Should().ThrowAsync<TimestampAuthorityException>()).Which;
        exception.Message.Should().Contain("usage extensions are malformed");
        exception.InnerException.Should().BeAssignableTo<CryptographicException>();
    }

    [Test]
    public async Task TimestampAsync_NotSha256_ThrowsArgumentException()
    {
        var authority = CreateTestAuthority().CreateAuthority();

        var act = () => authority.TimestampAsync(SHA512.HashData([1]), HashAlgorithmName.SHA512, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>().WithParameterName("algorithm");
    }

    [Test]
    public async Task TimestampAsync_OnlyReasonLimitedHttpDistributionPoint_Throws()
    {
        var authority = CreateTestAuthority(rawCrlDistributionPoints: EncodeReasonLimitedDistributionPoints(TestTimestampAuthority.CrlUri))
            .CreateAuthority();

        var act = () => authority.TimestampAsync(CreateDigest(), HashAlgorithmName.SHA256, CancellationToken.None);

        (await act.Should().ThrowAsync<TimestampAuthorityException>()).Which.Message.Should().Contain("covers all revocation reasons");
    }

    [Test]
    public async Task TimestampAsync_PointsWhoseLocationsReadAlike_CapturesEach()
    {
        // Uri.ToString() unescapes, so both points read "http://tsa.test/ca.crl http://tsa.test/mirror/ca.crl".
        var readAlike = new Uri($"{TestTimestampAuthority.CrlUri.AbsoluteUri}%20{TestTimestampAuthority.CrlMirrorUri.AbsoluteUri}");
        var testAuthority = CreateTestAuthority(rawCrlDistributionPoints: TestTimestampAuthority.EncodeDistributionPoints(
                                                    [TestTimestampAuthority.CrlUri.AbsoluteUri, TestTimestampAuthority.CrlMirrorUri.AbsoluteUri],
                                                    [readAlike.AbsoluteUri]));
        testAuthority.AdditionalCrlUris.Add(readAlike);

        var token = await testAuthority.CreateAuthority().TimestampAsync(CreateDigest(), HashAlgorithmName.SHA256, CancellationToken.None);

        token.CertificateRevocationLists.Should().HaveCount(2);
    }

    [Test]
    public async Task TimestampAsync_ReasonLimitedBesideFullHttpDistributionPoint_CapturesBothCrls()
    {
        var testAuthority = CreateTestAuthority(rawCrlDistributionPoints: EncodeReasonLimitedDistributionPoints(TestTimestampAuthority.CrlMirrorUri,
                                                                                                                TestTimestampAuthority.CrlUri));

        var token = await testAuthority.CreateAuthority().TimestampAsync(CreateDigest(), HashAlgorithmName.SHA256, CancellationToken.None);

        token.CertificateRevocationLists.Should().HaveCount(2).And.AllSatisfy(crl => crl.Should().Equal(testAuthority.Crl));
    }

    [TestCase(TestTimestampAuthorityMode.Rejection)]
    [TestCase(TestTimestampAuthorityMode.TrailingData)]
    public async Task TimestampAsync_ResponseNotAccepted_Throws(TestTimestampAuthorityMode mode)
    {
        var exception = await TimestampExpectingFailureAsync(mode);

        exception.AuthorityName.Should().Be("test-tsa");
    }

    [TestCase(false, false)]
    [TestCase(true, true)]
    public async Task TimestampAsync_SigningCertificateNotValidForTimestamping_Throws(Boolean timestampingUsage, Boolean expiredSigner)
    {
        var authority = CreateTestAuthority(timestampingUsage: timestampingUsage, expiredSigner: expiredSigner).CreateAuthority();

        var act = () => authority.TimestampAsync(CreateDigest(), HashAlgorithmName.SHA256, CancellationToken.None);

        (await act.Should().ThrowAsync<TimestampAuthorityException>()).Which.AuthorityName.Should().Be("test-tsa");
    }

    [Test]
    public async Task TimestampAsync_SigningCertificateWithAdditionalPurpose_Throws()
    {
        var authority = CreateTestAuthority(additionalPurpose: true).CreateAuthority();

        var act = () => authority.TimestampAsync(CreateDigest(), HashAlgorithmName.SHA256, CancellationToken.None);

        (await act.Should().ThrowAsync<TimestampAuthorityException>()).Which.Message.Should().Contain("only id-kp-timeStamping");
    }

    [Test]
    public async Task TimestampAsync_SigningCertificateWithDuplicateExtension_Throws()
    {
        var authority = CreateTestAuthority(duplicateKeyUsage: true).CreateAuthority();

        var act = () => authority.TimestampAsync(CreateDigest(), HashAlgorithmName.SHA256, CancellationToken.None);

        (await act.Should().ThrowAsync<TimestampAuthorityException>()).Which.Message.Should().Contain("extension 2.5.29.15 more than once");
    }

    [Test]
    public async Task TimestampAsync_SigningCertificateWithoutDistributionPoint_ReturnsNoCrl()
    {
        var authority = CreateTestAuthority([]).CreateAuthority();

        var token = await authority.TimestampAsync(CreateDigest(), HashAlgorithmName.SHA256, CancellationToken.None);

        token.CertificateRevocationLists.Should().BeEmpty();
    }

    [Test]
    public async Task TimestampAsync_SigningCertificateWithoutSignatureKeyUsage_Throws()
    {
        var authority = CreateTestAuthority(keyUsage: X509KeyUsageFlags.KeyEncipherment).CreateAuthority();

        var act = () => authority.TimestampAsync(CreateDigest(), HashAlgorithmName.SHA256, CancellationToken.None);

        (await act.Should().ThrowAsync<TimestampAuthorityException>()).Which.Message.Should().Contain("key usage");
    }

    [Test]
    public async Task TimestampAsync_SuccessiveRequests_UseFreshNonces()
    {
        var testAuthority = CreateTestAuthority();
        var authority = testAuthority.CreateAuthority();

        await authority.TimestampAsync(CreateDigest(), HashAlgorithmName.SHA256, CancellationToken.None);
        await authority.TimestampAsync(CreateDigest(), HashAlgorithmName.SHA256, CancellationToken.None);

        testAuthority.Nonces.Should().HaveCount(2).And.OnlyContain(nonce => nonce != null);
        testAuthority.Nonces[0].Should().NotEqual(testAuthority.Nonces[1]);
    }

    [Test]
    public async Task TimestampAsync_SystemTrustWithoutTrustedRoots_RejectsTestRoot()
    {
        var testAuthority = CreateTestAuthority();
        var options = new Rfc3161TimestampAuthorityOptions
        {
            Name = "system-trust",
            Uri = TestTimestampAuthority.TimestampUri,
            HttpClientFactory = _ => new HttpClient(testAuthority, false),
            RevocationMode = X509RevocationMode.NoCheck
        };
        var authority = new Rfc3161TimestampAuthority(options, new ServiceCollection().BuildServiceProvider());

        var act = () => authority.TimestampAsync(CreateDigest(), HashAlgorithmName.SHA256, CancellationToken.None);

        (await act.Should().ThrowAsync<TimestampAuthorityException>()).Which.Message.Should().Contain("not trusted");
    }

    [Test]
    public async Task TimestampAsync_TimestampResponseAboveMaximumResponseSize_Throws()
    {
        var testAuthority = CreateTestAuthority();
        testAuthority.Mode = TestTimestampAuthorityMode.ResponseOversized;
        var authority = testAuthority.CreateAuthority(configure: options => options.MaximumResponseSize = TestTimestampAuthority.OversizedLength);

        var act = () => authority.TimestampAsync(CreateDigest(), HashAlgorithmName.SHA256, CancellationToken.None);

        (await act.Should().ThrowAsync<TimestampAuthorityException>()).Which.Message.Should().Contain("timestamp response exceeds the maximum response size");
    }

    [TestCase(TestTimestampAuthorityMode.Backdated)]
    [TestCase(TestTimestampAuthorityMode.FutureDated)]
    public async Task TimestampAsync_TokenTimeOutsideClockSkew_Throws(TestTimestampAuthorityMode mode)
    {
        var exception = await TimestampExpectingFailureAsync(mode);

        exception.Message.Should().Contain("differs from the local time");
    }

    [Test]
    public async Task TimestampAsync_TrailingDataAfterAlternativeNames_Throws()
    {
        var alternativeNames = new SubjectAlternativeNameBuilder();
        alternativeNames.AddDnsName(TestTimestampAuthority.DnsName);
        var authority = CreateTestAuthority(alternativeTsaName: true, rawAlternativeNames: [.. alternativeNames.Build().RawData, 0x05, 0x00])
            .CreateAuthority();

        var act = () => authority.TimestampAsync(CreateDigest(), HashAlgorithmName.SHA256, CancellationToken.None);

        (await act.Should().ThrowAsync<TimestampAuthorityException>()).Which.Message.Should().Contain("TSA name is malformed");
    }

    [Test]
    public async Task TimestampAsync_TrailingDataInsideTstInfo_Throws()
    {
        var exception = await TimestampExpectingFailureAsync(TestTimestampAuthorityMode.TrailingTstInfoData);

        exception.Message.Should().Contain("not exactly one TSTInfo");
    }

    [Test]
    public async Task TimestampAsync_TsaDnsNameDiffersFromAlternativeName_Throws()
    {
        var testAuthority = CreateTestAuthority(alternativeTsaName: true);
        testAuthority.TokenDnsName = "other.test";
        var authority = testAuthority.CreateAuthority();

        var act = () => authority.TimestampAsync(CreateDigest(), HashAlgorithmName.SHA256, CancellationToken.None);

        (await act.Should().ThrowAsync<TimestampAuthorityException>()).Which.Message.Should().Contain("TSA name");
    }

    [Test]
    public async Task TimestampAsync_TsaDnsNameInDifferentCase_ReturnsToken()
    {
        var testAuthority = CreateTestAuthority(alternativeTsaName: true);
        testAuthority.TokenDnsName = "TSA.TEST";
        var authority = testAuthority.CreateAuthority();

        var token = await authority.TimestampAsync(CreateDigest(), HashAlgorithmName.SHA256, CancellationToken.None);

        token.CertificateChain.Should().HaveCount(2);
    }

    [Test]
    public async Task TimestampAsync_TsaNameInSubjectAlternativeName_ReturnsToken()
    {
        var authority = CreateTestAuthority(alternativeTsaName: true).CreateAuthority();

        var token = await authority.TimestampAsync(CreateDigest(), HashAlgorithmName.SHA256, CancellationToken.None);

        token.CertificateChain.Should().HaveCount(2);
    }

    [Test]
    public async Task TimestampAsync_TsaNameNotASubjectName_Throws()
    {
        var exception = await TimestampExpectingFailureAsync(TestTimestampAuthorityMode.WrongTsaName);

        exception.Message.Should().Contain("TSA name");
    }

    [Test]
    public async Task TimestampAsync_UntrustedChain_Throws()
    {
        var otherRoot = CreateTestAuthority().Root;
        var authority = CreateTestAuthority().CreateAuthority(otherRoot);

        var act = () => authority.TimestampAsync(CreateDigest(), HashAlgorithmName.SHA256, CancellationToken.None);

        (await act.Should().ThrowAsync<TimestampAuthorityException>()).Which.Message.Should().Contain("not trusted");
    }

    [Test]
    public async Task TimestampAsync_ValidResponse_ReturnsTokenWithChainAndCrl()
    {
        var testAuthority = CreateTestAuthority();
        var digest = CreateDigest();
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        var token = await testAuthority.CreateAuthority().TimestampAsync(digest, HashAlgorithmName.SHA256, CancellationToken.None);

        Rfc3161TimestampToken.TryDecode(token.Token, out var decoded, out var bytesRead).Should().BeTrue();
        bytesRead.Should().Be(token.Token.Length);
        decoded.Should().NotBeNull();
        decoded.TokenInfo.GetMessageHash().ToArray().Should().Equal(digest);
        token.CertificateChain.Should().HaveCount(2);
        token.CertificateChain[0].Should().Equal(testAuthority.SigningCertificate.RawData);
        token.CertificateChain[1].Should().Equal(testAuthority.Root.RawData);
        token.CertificateRevocationLists.Should().ContainSingle().Which.Should().Equal(testAuthority.Crl);
        token.Timestamp.Should().Be(decoded.TokenInfo.Timestamp).And.BeOnOrAfter(before);
    }

    [TestCase(31)]
    [TestCase(33)]
    public async Task TimestampAsync_WrongDigestLength_ThrowsArgumentException(Int32 length)
    {
        var authority = CreateTestAuthority().CreateAuthority();

        var act = () => authority.TimestampAsync(new Byte[length], HashAlgorithmName.SHA256, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>().WithParameterName("digest");
    }

    private static Byte[] CreateDigest() => SHA256.HashData(Guid.NewGuid().ToByteArray());

    /// <summary>
    /// Encodes a distribution point at <paramref name="reasonLimitedLocation"/> limited to key compromise, followed by
    /// one at <paramref name="fullLocation"/> for all reasons when given.
    /// </summary>
    private static Byte[] EncodeReasonLimitedDistributionPoints(Uri reasonLimitedLocation, Uri? fullLocation = null)
    {
        var uriTag = new Asn1Tag(TagClass.ContextSpecific, 6);
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence())
            {
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
                {
                    writer.WriteCharacterString(UniversalTagNumber.IA5String, reasonLimitedLocation.AbsoluteUri, uriTag);
                }

                // keyCompromise is bit 1 of ReasonFlags.
                writer.WriteBitString([0x40], 6, new Asn1Tag(TagClass.ContextSpecific, 1));
            }

            if (fullLocation is not null)
            {
                using (writer.PushSequence())
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
                {
                    writer.WriteCharacterString(UniversalTagNumber.IA5String, fullLocation.AbsoluteUri, uriTag);
                }
            }
        }

        return writer.Encode();
    }

    private TestTimestampAuthority CreateTestAuthority(IReadOnlyList<Uri>? crlLocations = null,
                                                       Byte[]? rawCrlDistributionPoints = null,
                                                       Boolean timestampingUsage = true,
                                                       Boolean expiredSigner = false,
                                                       Boolean additionalPurpose = false,
                                                       X509KeyUsageFlags keyUsage = X509KeyUsageFlags.DigitalSignature,
                                                       Boolean alternativeTsaName = false,
                                                       Byte[]? rawKeyUsage = null,
                                                       Boolean duplicateKeyUsage = false,
                                                       Byte[]? rawAlternativeNames = null)
    {
        var testAuthority = new TestTimestampAuthority(crlLocations,
                                                       rawCrlDistributionPoints,
                                                       timestampingUsage,
                                                       expiredSigner,
                                                       additionalPurpose,
                                                       keyUsage,
                                                       alternativeTsaName,
                                                       rawKeyUsage,
                                                       duplicateKeyUsage,
                                                       rawAlternativeNames);
        _testAuthorities.Add(testAuthority);
        return testAuthority;
    }

    private async Task<TimestampAuthorityException> TimestampExpectingFailureAsync(TestTimestampAuthorityMode mode)
    {
        var testAuthority = CreateTestAuthority();
        testAuthority.Mode = mode;
        var authority = testAuthority.CreateAuthority();

        var act = () => authority.TimestampAsync(CreateDigest(), HashAlgorithmName.SHA256, CancellationToken.None);

        return (await act.Should().ThrowAsync<TimestampAuthorityException>()).Which;
    }
}
