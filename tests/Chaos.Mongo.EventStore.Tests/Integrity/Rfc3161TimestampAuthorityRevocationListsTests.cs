// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity;

using Chaos.Mongo.EventStore.Errors;
using Chaos.Mongo.EventStore.Integrity;
using FluentAssertions;
using NUnit.Framework;

public class Rfc3161TimestampAuthorityRevocationListsTests
{
    private readonly List<TestTimestampAuthority> _testAuthorities = [];

    [Test]
    public async Task GetRevocationListsAsync_ChainWithoutDistributionPoints_ReturnsNoCrls()
    {
        var testAuthority = CreateTestAuthority([]);

        var lists = await GetRevocationListsAsync(testAuthority, DateTime.UtcNow.AddHours(-1));

        lists.IsIssuedTooEarly.Should().BeFalse();
        lists.Lists.Should().BeEmpty();
        testAuthority.CrlRequests.Should().Be(0);
    }

    [Test]
    public async Task GetRevocationListsAsync_CrlDownloadHangs_FailsAtTheAttemptTimeout()
    {
        var testAuthority = CreateTestAuthority();
        testAuthority.Mode = TestTimestampAuthorityMode.CrlHangs;
        var authority = testAuthority.CreateAuthority(configure: options => options.AttemptTimeout = TimeSpan.FromMilliseconds(200));

        var act = () => authority.GetRevocationListsAsync(GetChain(testAuthority), DateTime.UtcNow.AddHours(-1), CancellationToken.None);

        (await act.Should().ThrowAsync<TimestampAuthorityException>()).Which.Message.Should().Contain("attempt timeout");
    }

    [Test]
    public async Task GetRevocationListsAsync_CrlExpiredBeforeTheRequiredTime_Throws()
    {
        var testAuthority = CreateTestAuthority();
        testAuthority.ServedCrl = testAuthority.BuildCrl(DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow.AddHours(-2));

        var exception = await GetRevocationListsExpectingFailureAsync(testAuthority, DateTime.UtcNow.AddHours(-1));

        exception.InnerException.Should().BeOfType<InvalidDataException>().Which.Message.Should().Contain("before the required issue time");
    }

    [Test]
    public async Task GetRevocationListsAsync_CrlIssuedAfterTheRequiredTime_ReturnsIt()
    {
        var testAuthority = CreateTestAuthority();
        var crl = testAuthority.BuildCrl(DateTimeOffset.UtcNow.AddMinutes(-10));
        testAuthority.ServedCrl = crl;

        var lists = await GetRevocationListsAsync(testAuthority, DateTime.UtcNow.AddHours(-1));

        lists.IsIssuedTooEarly.Should().BeFalse();
        lists.NextUpdateUtc.Should().BeNull();
        lists.Lists.Should().ContainSingle().Which.Should().Equal(crl);
    }

    [Test]
    public async Task GetRevocationListsAsync_CrlIssuedTooEarly_ReturnsItsNextUpdateWithoutFailing()
    {
        var testAuthority = CreateTestAuthority();
        var nextUpdate = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(5), TimeSpan.Zero);
        testAuthority.ServedCrlsByLocation[TestTimestampAuthority.CrlUri] = testAuthority.BuildCrl(DateTimeOffset.UtcNow.AddHours(-2), nextUpdate.AddDays(1));
        testAuthority.ServedCrlsByLocation[TestTimestampAuthority.CrlMirrorUri] = testAuthority.BuildCrl(DateTimeOffset.UtcNow.AddHours(-3), nextUpdate);

        var lists = await GetRevocationListsAsync(testAuthority, DateTime.UtcNow.AddHours(-1));

        lists.IsIssuedTooEarly.Should().BeTrue();
        lists.NextUpdateUtc.Should().Be(nextUpdate.UtcDateTime, "the earliest next update of the CRLs issued too early counts");
        lists.Lists.Should().BeEmpty();
    }

    [TestCase(TestTimestampAuthorityMode.CrlNotACrl, "not a single DER-encoded CRL")]
    [TestCase(TestTimestampAuthorityMode.CrlFromOtherIssuer, "not issued by the issuer")]
    [TestCase(TestTimestampAuthorityMode.CrlWithoutNextUpdate, "states no next update")]
    [TestCase(TestTimestampAuthorityMode.CrlIssuedInFuture, "after the local time allows")]
    [TestCase(TestTimestampAuthorityMode.CrlOversized, "exceeds the maximum response size")]
    public async Task GetRevocationListsAsync_CrlNotUsable_Throws(TestTimestampAuthorityMode mode, String reason)
    {
        var testAuthority = CreateTestAuthority();
        testAuthority.Mode = mode;

        var exception = await GetRevocationListsExpectingFailureAsync(testAuthority, DateTime.UtcNow.AddHours(-1),
                                                                      options => options.MaximumResponseSize = TestTimestampAuthority.OversizedLength - 1);

        exception.Message.Should().Contain("could not be downloaded");
        exception.InnerException.Should().BeOfType<InvalidDataException>().Which.Message.Should().Contain(reason);
    }

    [Test]
    public async Task GetRevocationListsAsync_CrlUnavailable_Throws()
    {
        var testAuthority = CreateTestAuthority();
        testAuthority.Mode = TestTimestampAuthorityMode.CrlUnavailable;

        var exception = await GetRevocationListsExpectingFailureAsync(testAuthority, DateTime.UtcNow.AddHours(-1));

        exception.InnerException.Should().BeOfType<HttpRequestException>();
    }

    [Test]
    public async Task GetRevocationListsAsync_MalformedCertificate_Throws()
    {
        var testAuthority = CreateTestAuthority();
        var authority = testAuthority.CreateAuthority();

        var act = () => authority.GetRevocationListsAsync(["not a certificate"u8.ToArray(), testAuthority.Root.RawData], DateTime.UtcNow, CancellationToken.None);

        (await act.Should().ThrowAsync<TimestampAuthorityException>()).Which.Message.Should().Contain("stored chain is malformed");
    }

    [Test]
    public async Task GetRevocationListsAsync_MalformedDistributionPoint_Throws()
    {
        var testAuthority = CreateTestAuthority(rawCrlDistributionPoints: [0x05, 0x00]);
        var authority = testAuthority.CreateAuthority();

        var act = () => authority.GetRevocationListsAsync(GetChain(testAuthority), DateTime.UtcNow, CancellationToken.None);

        (await act.Should().ThrowAsync<TimestampAuthorityException>()).Which.Message.Should().Contain("malformed CRL distribution point");
    }

    [Test]
    public async Task GetRevocationListsAsync_NonUtcIssuedNotBefore_ThrowsArgumentException()
    {
        var testAuthority = CreateTestAuthority();
        var authority = testAuthority.CreateAuthority();

        var act = () => authority.GetRevocationListsAsync(GetChain(testAuthority), DateTime.Now, CancellationToken.None);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("issuedNotBeforeUtc");
    }

    [Test]
    public async Task GetRevocationListsAsync_NullChain_ThrowsArgumentNullException()
    {
        var authority = CreateTestAuthority().CreateAuthority();

        var act = () => authority.GetRevocationListsAsync(null!, DateTime.UtcNow, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Test]
    public async Task GetRevocationListsAsync_TooEarlyAtFirstLocation_ReturnsTheLaterCrlOfTheNext()
    {
        var testAuthority = CreateTestAuthority();
        var later = testAuthority.BuildCrl(DateTimeOffset.UtcNow.AddMinutes(-10));
        testAuthority.ServedCrlsByLocation[TestTimestampAuthority.CrlUri] = testAuthority.BuildCrl(DateTimeOffset.UtcNow.AddHours(-2));
        testAuthority.ServedCrlsByLocation[TestTimestampAuthority.CrlMirrorUri] = later;

        var lists = await GetRevocationListsAsync(testAuthority, DateTime.UtcNow.AddHours(-1));

        lists.Lists.Should().ContainSingle().Which.Should().Equal(later);
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

    /// <summary>
    /// Gets the chain a token of the test authority carries: its signing certificate and its root.
    /// </summary>
    private static IReadOnlyList<Byte[]> GetChain(TestTimestampAuthority testAuthority)
        => [testAuthority.SigningCertificate.RawData, testAuthority.Root.RawData];

    private static Task<RevocationLists> GetRevocationListsAsync(TestTimestampAuthority testAuthority, DateTime issuedNotBeforeUtc)
        => testAuthority.CreateAuthority().GetRevocationListsAsync(GetChain(testAuthority), issuedNotBeforeUtc, CancellationToken.None);

    private static async Task<TimestampAuthorityException> GetRevocationListsExpectingFailureAsync(TestTimestampAuthority testAuthority,
                                                                                                   DateTime issuedNotBeforeUtc,
                                                                                                   Action<Rfc3161TimestampAuthorityOptions>? configure = null)
    {
        var authority = testAuthority.CreateAuthority(configure: configure);

        var act = () => authority.GetRevocationListsAsync(GetChain(testAuthority), issuedNotBeforeUtc, CancellationToken.None);

        var exception = (await act.Should().ThrowAsync<TimestampAuthorityException>()).Which;
        exception.Message.Should().Contain(TestTimestampAuthority.CrlUri.AbsoluteUri);
        return exception;
    }

    private TestTimestampAuthority CreateTestAuthority(IReadOnlyList<Uri>? crlLocations = null, Byte[]? rawCrlDistributionPoints = null)
    {
        var testAuthority = new TestTimestampAuthority(crlLocations, rawCrlDistributionPoints);
        _testAuthorities.Add(testAuthority);
        return testAuthority;
    }
}
