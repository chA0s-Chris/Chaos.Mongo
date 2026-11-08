// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Chaos.Mongo.EventStore.Integrity;
using Chaos.Mongo.EventStore.Tests.Integrity;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using NUnit.Framework;
using System.Security.Cryptography;
using Testcontainers.MongoDb;

[Category("Integration")]
public class TimestampRevocationConfirmationTests
{
    private static readonly TimeSpan GracePeriod = TimeSpan.FromHours(24);
    private readonly List<LedgerStoreContext> _contexts = [];
    private readonly List<TestTimestampAuthority> _testAuthorities = [];
    private MongoDbContainer _container;

    [OneTimeSetUp]
    public async Task GetMongoDbContainer() => _container = await MongoDbTestContainer.StartContainerAsync();

    [TearDown]
    public async Task TearDown()
    {
        foreach (var context in _contexts)
        {
            await context.DisposeAsync();
        }

        foreach (var testAuthority in _testAuthorities)
        {
            testAuthority.Dispose();
        }

        _contexts.Clear();
        _testAuthorities.Clear();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ConfirmAsync_LockLostWhileTheAuthorityAnswers_RecordsNothing(Boolean crlUnavailable)
    {
        var fixture = await CreateFixtureAsync();
        var anchor = await fixture.AnchorAsync();
        var timestamp = anchor.Timestamps[0];
        fixture.TestAuthority.ServedCrl = fixture.TestAuthority.BuildCrl(timestamp.TimestampUtc + GracePeriod + TimeSpan.FromMinutes(1));
        fixture.TestAuthority.Mode = crlUnavailable ? TestTimestampAuthorityMode.CrlUnavailable : TestTimestampAuthorityMode.Valid;
        fixture.Time.SetUtcNow(timestamp.TimestampUtc + GracePeriod + TimeSpan.FromMinutes(2));
        var confirmer = new TimestampRevocationConfirmer(fixture.Store,
                                                         new AnchorEvidenceStore(fixture.Context.MongoHelper, fixture.Options),
                                                         [fixture.Authority],
                                                         fixture.Options,
                                                         fixture.Time,
                                                         fixture.Logger,
                                                         fixture.Verification);
        var lockChecks = 0;

        // The check before the authority is asked succeeds; the one after it answers fails.
        await confirmer.ConfirmAsync(_ => Task.FromResult(++lockChecks == 1), CancellationToken.None);

        lockChecks.Should().Be(2);
        fixture.TestAuthority.CrlRequests.Should().BeGreaterThan(1, "the authority was asked");
        var unchanged = await fixture.ReadTimestampAsync(anchor);
        unchanged.RevocationStatus.Should().Be(TimestampRevocationStatus.Unconfirmed);
        unchanged.ConfirmingRevocationLists.Should().BeEmpty();
        unchanged.ConfirmationNotBeforeUtc.Should().BeNull();
        unchanged.ConfirmationFailure.Should().BeNull();
    }

    [Test]
    public async Task Constructor_InvalidRevocationSettings_ThrowsArgumentException()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Options.RevocationGracePeriod = TimeSpan.Zero;
        FluentActions.Invoking(() => fixture.CreateRun()).Should().Throw<ArgumentException>().WithMessage("*revocation grace period*");
        fixture.Options.RevocationGracePeriod = TimeSpan.FromHours(72);
        FluentActions.Invoking(() => fixture.CreateRun()).Should().Throw<ArgumentException>().WithMessage("*revocation grace period*");
    }

    [Test]
    public async Task FindOldestUnconfirmedTimestampUtcAsync_LaterAnchorWithEarlierToken_ReturnsTheEarliestToken()
    {
        var fixture = await CreateFixtureAsync();
        var first = await fixture.AnchorAsync();
        var second = await fixture.AnchorAsync();

        // An authority whose clock runs behind, within the allowed skew, stamps the later anchor with an earlier time.
        var earlier = first.Timestamps[0].TimestampUtc - TimeSpan.FromMinutes(3);
        await fixture.Store.Anchors.UpdateOneAsync(new BsonDocument("_id", new BsonBinaryData(second.Id, GuidRepresentation.Standard)),
                                                   new BsonDocument("$set", new BsonDocument("Timestamps.0.TimestampUtc", earlier)));

        var oldest = await fixture.Store.FindOldestUnconfirmedTimestampUtcAsync(CancellationToken.None);

        oldest.Should().BeCloseTo(earlier, TimeSpan.FromMilliseconds(1));
    }

    [Test]
    public async Task RunAsync_AnchoringPhasesThrow_ConfirmsAndRethrows()
    {
        var fixture = await CreateFixtureAsync();
        var anchor = await fixture.AnchorAsync();
        var timestamp = anchor.Timestamps[0];
        fixture.TestAuthority.ServedCrl = fixture.TestAuthority.BuildCrl(timestamp.TimestampUtc + GracePeriod + TimeSpan.FromMinutes(1));
        fixture.Time.SetUtcNow(timestamp.TimestampUtc + GracePeriod + TimeSpan.FromHours(1));
        var run = fixture.CreateRun(new InMemoryAnchorCandidateSource(InMemoryAnchorCandidateSource.CreateCandidates(1), fixture.Context.MongoHelper.Database),
                                    fixture.Authority);
        run.PhaseCompleted = (phase, _) => phase == AnchoringPhase.CandidatesRead ? throw new SimulatedCrashException() : Task.CompletedTask;

        var act = () => run.RunAsync();

        await act.Should().ThrowAsync<SimulatedCrashException>();
        (await fixture.ReadTimestampAsync(anchor)).RevocationStatus.Should().Be(TimestampRevocationStatus.Confirmed);
    }

    [Test]
    public async Task RunAsync_AuthorityNoLongerConfigured_LeavesItsTimestampsUnconfirmed()
    {
        var fixture = await CreateFixtureAsync();
        var anchor = await fixture.AnchorAsync();
        var timestamp = anchor.Timestamps[0];
        var other = new TestTimestampAuthority();
        _testAuthorities.Add(other);
        fixture.Time.SetUtcNow(timestamp.TimestampUtc + GracePeriod + TimeSpan.FromHours(1));

        await fixture.CreateRun(other.CreateAuthority(name: "other", timeProvider: fixture.Time)).RunAsync();

        var unconfirmed = await fixture.ReadTimestampAsync(anchor);
        unconfirmed.RevocationStatus.Should().Be(TimestampRevocationStatus.Unconfirmed);
        unconfirmed.ConfirmationFailure.Should().BeNull();
        fixture.TestAuthority.CrlRequests.Should().Be(1, "only stamping downloaded a CRL");
        (await fixture.Store.FindOldestUnconfirmedTimestampUtcAsync(CancellationToken.None)).Should().BeCloseTo(timestamp.TimestampUtc, TimeSpan.FromMilliseconds(1));
    }

    [Test]
    public async Task RunAsync_ChainKeepsFailing_DoesNotKeepOtherChainsFromBeingConfirmed()
    {
        var fixture = await CreateFixtureAsync();
        var healthyTestAuthority = new TestTimestampAuthority();
        _testAuthorities.Add(healthyTestAuthority);
        var healthy = healthyTestAuthority.CreateAuthority(name: "healthy", timeProvider: fixture.Time);
        fixture.Verification.Authorities["healthy"] = new TimestampAuthorityTrust
        {
            TrustedRoots =
            {
                healthyTestAuthority.Root
            }
        };
        var anchors = new List<AnchorRecord>();
        for (var anchor = 0; anchor < 4; anchor++)
        {
            anchors.Add(await fixture.AnchorAsync(authorities: [fixture.Authority, healthy]));
        }

        var latest = anchors.SelectMany(anchor => anchor.Timestamps).Max(timestamp => timestamp.TimestampUtc);
        fixture.TestAuthority.Mode = TestTimestampAuthorityMode.CrlUnavailable;
        healthyTestAuthority.ServedCrl = healthyTestAuthority.BuildCrl(latest + GracePeriod + TimeSpan.FromMinutes(1));
        fixture.Time.SetUtcNow(latest + GracePeriod + TimeSpan.FromMinutes(2));
        var run = fixture.CreateRun(fixture.Authority, healthy);
        run.MaximumConfirmationAnchorsPerRun = 1;

        // Each run reads one anchor: on the first, one chain fails and the other is confirmed; on the later ones, the
        // failing chain waits and its entries wait with it, so every run reaches the next anchor.
        for (var runs = 1; runs <= anchors.Count; runs++)
        {
            await run.RunAsync();
            (await ReadStatusesAsync("healthy")).Count(status => status == TimestampRevocationStatus.Confirmed).Should().Be(runs);
        }

        var failing = await Task.WhenAll(
            anchors.Select(async anchor => (await fixture.ReadAnchorAsync(anchor)).Timestamps.Single(timestamp => timestamp.Authority == fixture.Authority.Name)));
        failing.Should().OnlyContain(timestamp => timestamp.RevocationStatus == TimestampRevocationStatus.Unconfirmed);
        failing.Should().OnlyContain(timestamp => timestamp.ConfirmationNotBeforeUtc == failing[0].ConfirmationNotBeforeUtc);
        var firstFailure = failing[0].ConfirmationFailure;
        firstFailure.Should().NotBeNull();
        firstFailure.FailureCount.Should().Be(1);
        failing.Skip(1).Should().OnlyContain(timestamp => timestamp.ConfirmationFailure == null, "their chain waits after the failure of its first entry");

        async Task<TimestampRevocationStatus[]> ReadStatusesAsync(String authority)
            => await Task.WhenAll(anchors.Select(async anchor => (await fixture.ReadAnchorAsync(anchor)).Timestamps
                                                                                                        .Single(timestamp => timestamp.Authority == authority)
                                                                                                        .RevocationStatus));
    }

    /// <summary>
    /// Same shape as <see cref="RunAsync_ChainKeepsFailing_DoesNotKeepOtherChainsFromBeingConfirmed"/>, but the
    /// stuck chain reaches its bounded read every time not via the thrown exception that case catches, but via the
    /// stored <see cref="TimestampRevocationStatus.Unconfirmed"/> status this plan records instead of throwing for a
    /// CRL that genuinely never covers the chain (every certificate has a source, but the only CRL returned does not
    /// count for it): the deferral must still free the next run's bounded read for the healthy chain.
    /// </summary>
    [Test]
    public async Task RunAsync_ChainWithCrlThatNeverCoversIt_DoesNotKeepOtherChainsFromBeingConfirmed()
    {
        var fixture = await CreateFixtureAsync();
        var healthyTestAuthority = new TestTimestampAuthority();
        _testAuthorities.Add(healthyTestAuthority);
        var healthy = healthyTestAuthority.CreateAuthority(name: "healthy", timeProvider: fixture.Time);
        fixture.Verification.Authorities["healthy"] = new TimestampAuthorityTrust
        {
            TrustedRoots =
            {
                healthyTestAuthority.Root
            }
        };
        var anchors = new List<AnchorRecord>();
        for (var anchor = 0; anchor < 4; anchor++)
        {
            anchors.Add(await fixture.AnchorAsync(authorities: [fixture.Authority, healthy]));
        }

        var latest = anchors.SelectMany(anchor => anchor.Timestamps).Max(timestamp => timestamp.TimestampUtc);
        var neverCoveringCrl = BreakSignature(fixture.TestAuthority.BuildCrl(latest + GracePeriod + TimeSpan.FromMinutes(1)));
        var stuck = new FixedRevocationListsAuthority(fixture.Authority, RevocationLists.Issued([neverCoveringCrl]));
        healthyTestAuthority.ServedCrl = healthyTestAuthority.BuildCrl(latest + GracePeriod + TimeSpan.FromMinutes(1));
        fixture.Time.SetUtcNow(latest + GracePeriod + TimeSpan.FromMinutes(2));
        var run = fixture.CreateRun(stuck, healthy);
        run.MaximumConfirmationAnchorsPerRun = 1;

        // Each run reads one anchor: on the first, the stuck chain's CRL never covers it and the healthy one is
        // confirmed; on the later ones, the stuck chain's deferred entries wait, so every run reaches the next anchor.
        for (var runs = 1; runs <= anchors.Count; runs++)
        {
            await run.RunAsync();
            (await ReadStatusesAsync("healthy")).Count(status => status == TimestampRevocationStatus.Confirmed).Should().Be(runs);
        }

        var stuckEntries = await Task.WhenAll(
            anchors.Select(async anchor => (await fixture.ReadAnchorAsync(anchor)).Timestamps.Single(timestamp => timestamp.Authority == fixture.Authority.Name)));
        stuckEntries.Should().OnlyContain(timestamp => timestamp.RevocationStatus == TimestampRevocationStatus.Unconfirmed);
        stuckEntries.Should().OnlyContain(timestamp => timestamp.ConfirmationFailure == null,
                                          "a CRL that genuinely never covers the chain is recorded as unconfirmed, not a failure");
        stuckEntries.Should().OnlyContain(timestamp => timestamp.ConfirmationNotBeforeUtc == stuckEntries[0].ConfirmationNotBeforeUtc);

        async Task<TimestampRevocationStatus[]> ReadStatusesAsync(String authority)
            => await Task.WhenAll(anchors.Select(async anchor => (await fixture.ReadAnchorAsync(anchor)).Timestamps
                                                                                                        .Single(timestamp => timestamp.Authority == authority)
                                                                                                        .RevocationStatus));
    }

    /// <summary>
    /// Covers demonstrated revocation taking precedence over a sibling certificate's missing source: the signer
    /// names a CRL distribution point and is genuinely revoked by it, while the intermediate names none at all. The
    /// chain must still settle as <see cref="TimestampRevocationStatus.Revoked"/>, exactly as the plan requires,
    /// not <see cref="TimestampRevocationStatus.Unconfirmed"/> because of the intermediate's missing coverage.
    /// </summary>
    [Test]
    public async Task RunAsync_ChainWithDemonstratedRevocationAlongsideMissingCoverage_RecordsRevoked()
    {
        using var chained = new ChainedTimestampAuthority(signerCrlLocations: [new Uri("http://chained-tsa.test/signer.crl")]);
        var fixture = await CreateFixtureAsync();
        fixture.Verification.Authorities[chained.Name] = new TimestampAuthorityTrust
        {
            TrustedRoots =
            {
                chained.Root
            }
        };
        var anchor = await fixture.AnchorAsync(authorities: [chained]);
        var timestamp = anchor.Timestamps[0];
        var crl = chained.BuildIntermediateCrl(timestamp.TimestampUtc + GracePeriod + TimeSpan.FromMinutes(1),
                                               revoked: (chained.Signer.SerialNumberBytes.ToArray(), timestamp.TimestampUtc));
        var authority = new FixedRevocationListsAuthority(chained, RevocationLists.Issued([crl]));
        fixture.Time.SetUtcNow(timestamp.TimestampUtc + GracePeriod + TimeSpan.FromMinutes(2));

        await fixture.CreateRun(authority).RunAsync();

        var revoked = await fixture.ReadTimestampAsync(anchor);
        revoked.RevocationStatus.Should().Be(TimestampRevocationStatus.Revoked);
        revoked.ConfirmingRevocationLists.Should().ContainSingle().Which.Should().Equal(crl);
    }

    /// <summary>
    /// Same shape as <see cref="RunAsync_ChainWithCrlThatNeverCoversIt_DoesNotKeepOtherChainsFromBeingConfirmed"/>,
    /// but for the structurally partial case itself (one non-root certificate's missing CRL source, not a CRL that
    /// fails to cover a certificate that does have one), proving the bounded read's fairness guarantee also holds
    /// for the branch <see cref="RunAsync_ChainWithStructurallyPartialSourceCoverage_RecordsUnconfirmedUnconditionally"/>
    /// covers in isolation. The signer's own distribution point is backed by a genuine, confirming
    /// <see cref="ChainedTimestampAuthority.CrlRequests">counted</see> CRL that actually covers it, while the
    /// intermediate's remains structurally absent, so the chain is proven to stay unconfirmed because of the
    /// missing source, not because its one existing source was never actually requested.
    /// </summary>
    [Test]
    public async Task RunAsync_ChainWithStructurallyPartialSourceCoverage_DoesNotKeepOtherChainsFromBeingConfirmed()
    {
        using var stuck = new ChainedTimestampAuthority(signerCrlLocations: [new Uri("http://chained-tsa.test/signer.crl")]);
        var fixture = await CreateFixtureAsync();
        fixture.Verification.Authorities[stuck.Name] = new TimestampAuthorityTrust
        {
            TrustedRoots =
            {
                stuck.Root
            }
        };
        var healthyTestAuthority = new TestTimestampAuthority();
        _testAuthorities.Add(healthyTestAuthority);
        var healthy = healthyTestAuthority.CreateAuthority(name: "healthy", timeProvider: fixture.Time);
        fixture.Verification.Authorities["healthy"] = new TimestampAuthorityTrust
        {
            TrustedRoots =
            {
                healthyTestAuthority.Root
            }
        };
        var anchors = new List<AnchorRecord>();
        for (var anchor = 0; anchor < 4; anchor++)
        {
            anchors.Add(await fixture.AnchorAsync(authorities: [stuck, healthy]));
        }

        var latest = anchors.SelectMany(anchor => anchor.Timestamps).Max(timestamp => timestamp.TimestampUtc);
        healthyTestAuthority.ServedCrl = healthyTestAuthority.BuildCrl(latest + GracePeriod + TimeSpan.FromMinutes(1));
        fixture.Time.SetUtcNow(latest + GracePeriod + TimeSpan.FromMinutes(2));
        var run = fixture.CreateRun(stuck, healthy);
        run.MaximumConfirmationAnchorsPerRun = 1;

        // Each run reads one anchor: on the first, the structurally partial chain settles as unconfirmed and the
        // healthy one is confirmed; on the later ones, the stuck chain's deferred entries wait, so every run reaches
        // the next anchor.
        for (var runs = 1; runs <= anchors.Count; runs++)
        {
            await run.RunAsync();
            (await ReadStatusesAsync("healthy")).Count(status => status == TimestampRevocationStatus.Confirmed).Should().Be(runs);
        }

        stuck.CrlRequests.Should().Be(1, "the signer's genuinely covering source is requested once, covering all four due entries together, " +
                                         "rather than never being checked at all");
        var stuckEntries = await ReadStuckEntriesAsync();
        stuckEntries.Should().OnlyContain(timestamp => timestamp.RevocationStatus == TimestampRevocationStatus.Unconfirmed);
        stuckEntries.Should().OnlyContain(timestamp => timestamp.ConfirmationFailure == null, "a structurally partial chain is recorded as unconfirmed, not a failure");
        stuckEntries.Should().OnlyContain(timestamp => timestamp.ConfirmationNotBeforeUtc == stuckEntries[0].ConfirmationNotBeforeUtc);
        var deferredUntil = stuckEntries[0].ConfirmationNotBeforeUtc!.Value;
        deferredUntil.Should().BeCloseTo(fixture.Time.GetUtcNow().UtcDateTime + TimestampRevocationConfirmer.MaximumRetryDelay, TimeSpan.FromMilliseconds(1));

        // The deferred chain is not asked again before its deferral elapses, even though its entries are still due
        // by grace period, and is asked exactly once more once it does: the signer's source is genuinely
        // re-evaluated on the retry schedule, not silently skipped.
        fixture.Time.SetUtcNow(deferredUntil - TimeSpan.FromMilliseconds(1));
        await run.RunAsync();
        stuck.CrlRequests.Should().Be(1, "the deferral has not elapsed yet");
        fixture.Time.SetUtcNow(deferredUntil + TimeSpan.FromMilliseconds(1));
        await run.RunAsync();
        stuck.CrlRequests.Should().Be(2, "the deferral elapsed, so the chain is genuinely re-evaluated");
        (await ReadStuckEntriesAsync()).Should().OnlyContain(timestamp => timestamp.RevocationStatus == TimestampRevocationStatus.Unconfirmed,
                                                             "the intermediate's missing source still prevents confirmation even though the signer's source is fully covered");

        async Task<TimestampRevocationStatus[]> ReadStatusesAsync(String authority)
            => await Task.WhenAll(anchors.Select(async anchor => (await fixture.ReadAnchorAsync(anchor)).Timestamps
                                                                                                        .Single(timestamp => timestamp.Authority == authority)
                                                                                                        .RevocationStatus));

        Task<AnchorTimestampReference[]> ReadStuckEntriesAsync()
            => Task.WhenAll(anchors.Select(async anchor => (await fixture.ReadAnchorAsync(anchor)).Timestamps.Single(timestamp => timestamp.Authority == stuck.Name)));
    }

    /// <summary>
    /// Covers the structurally partial case of the three-way decision in
    /// <see cref="TimestampRevocationConfirmer.RecordStatusesAsync"/>:
    /// the signer names a CRL distribution point and the intermediate does not (<see cref="ChainedTimestampAuthority"/>'s
    /// genuine intermediate makes this representable at all; <see cref="TestTimestampAuthority"/>'s two-certificate
    /// chain cannot be). This can never settle as <see cref="TimestampRevocationStatus.Confirmed"/>, unconditionally,
    /// even though the signer's own source is genuinely requested, counted (<see cref="ChainedTimestampAuthority.CrlRequests"/>)
    /// and found to cover it every time, because the intermediate's own revocation status is never checked at all.
    /// </summary>
    [Test]
    public async Task RunAsync_ChainWithStructurallyPartialSourceCoverage_RecordsUnconfirmedUnconditionally()
    {
        using var chained = new ChainedTimestampAuthority(signerCrlLocations: [new Uri("http://chained-tsa.test/signer.crl")]);
        var fixture = await CreateFixtureAsync();
        fixture.Verification.Authorities[chained.Name] = new TimestampAuthorityTrust
        {
            TrustedRoots =
            {
                chained.Root
            }
        };
        var anchor = await fixture.AnchorAsync(authorities: [chained]);
        var timestamp = anchor.Timestamps[0];
        fixture.Time.SetUtcNow(timestamp.TimestampUtc + GracePeriod + TimeSpan.FromMinutes(1));

        await fixture.CreateRun(chained).RunAsync();

        var unconfirmed = await fixture.ReadTimestampAsync(anchor);
        unconfirmed.RevocationStatus.Should().Be(TimestampRevocationStatus.Unconfirmed);
        unconfirmed.ConfirmingRevocationLists.Should().BeEmpty();
        unconfirmed.ConfirmationFailure.Should().BeNull("a structurally partial chain is recorded as unconfirmed, not a failure");
        unconfirmed.ConfirmationNotBeforeUtc.Should().BeCloseTo(fixture.Time.GetUtcNow().UtcDateTime + TimestampRevocationConfirmer.MaximumRetryDelay,
                                                                TimeSpan.FromMilliseconds(1));
        chained.CrlRequests.Should().Be(1, "the signer's genuinely covering source is requested, not merely absent like the intermediate's");

        // It stays unconfirmed, not eventually confirmed, however many times it is retried, and the signer's source
        // is genuinely requested again each time, not silently skipped because the chain is known to be stuck.
        fixture.Time.Advance(TimestampRevocationConfirmer.MaximumRetryDelay + TimeSpan.FromMinutes(1));
        await fixture.CreateRun(chained).RunAsync();
        (await fixture.ReadTimestampAsync(anchor)).RevocationStatus.Should().Be(TimestampRevocationStatus.Unconfirmed,
                                                                                "a chain where at least one certificate has no source can never become confirmed");
        chained.CrlRequests.Should().Be(2, "the retried attempt also genuinely requests and evaluates the signer's source");
    }

    [Test]
    public async Task RunAsync_ChainWithoutDistributionPoints_RecordsNoRevocationSourceWithoutCrls()
    {
        var fixture = await CreateFixtureAsync(new TestTimestampAuthority([]));
        var anchor = await fixture.AnchorAsync();
        fixture.Time.SetUtcNow(anchor.Timestamps[0].TimestampUtc + GracePeriod + TimeSpan.FromMinutes(1));

        await fixture.CreateRun().RunAsync();

        var confirmed = await fixture.ReadTimestampAsync(anchor);
        confirmed.RevocationStatus.Should().Be(TimestampRevocationStatus.NoRevocationSource);
        confirmed.ConfirmingRevocationLists.Should().BeEmpty();
        fixture.TestAuthority.CrlRequests.Should().Be(0);
    }

    [Test]
    public async Task RunAsync_CrlIssuedAfterGracePeriod_ConfirmsTimestampWithIt()
    {
        var fixture = await CreateFixtureAsync();
        var anchor = await fixture.AnchorAsync();
        var timestamp = anchor.Timestamps[0];
        var crl = fixture.TestAuthority.BuildCrl(timestamp.TimestampUtc + GracePeriod + TimeSpan.FromMinutes(30));
        fixture.TestAuthority.ServedCrl = crl;
        fixture.Time.SetUtcNow(timestamp.TimestampUtc + GracePeriod + TimeSpan.FromHours(1));

        await fixture.CreateRun().RunAsync();

        var confirmed = await fixture.ReadTimestampAsync(anchor);
        confirmed.RevocationStatus.Should().Be(TimestampRevocationStatus.Confirmed);
        confirmed.ConfirmingRevocationLists.Should().ContainSingle().Which.Should().Equal(crl);
        (await fixture.ReadAnchorAsync(anchor)).Timestamps.Single().ConfirmingRevocationListHashes
                                               .Should().ContainSingle().Which.Should().Equal(SHA256.HashData(crl), "the entry references the CRL in the evidence store");
        confirmed.CertificateRevocationLists.Should().ContainSingle().Which.Should().Equal(fixture.TestAuthority.Crl);
        confirmed.RevocationStatusUtc.Should().BeCloseTo(fixture.Time.GetUtcNow().UtcDateTime, TimeSpan.FromMilliseconds(1));
        confirmed.ConfirmationFailure.Should().BeNull();
        (await fixture.Store.FindOldestUnconfirmedTimestampUtcAsync(CancellationToken.None)).Should().BeNull();
    }

    [Test]
    public async Task RunAsync_CrlIssuedTooEarlyWithEarlierNextUpdate_RetriesAtTheNextUpdate()
    {
        var fixture = await CreateFixtureAsync();
        var anchor = await fixture.AnchorAsync();
        var timestamp = anchor.Timestamps[0];
        var nextUpdate = timestamp.TimestampUtc + GracePeriod + TimeSpan.FromMinutes(20);
        fixture.TestAuthority.ServedCrl = fixture.TestAuthority.BuildCrl(timestamp.TimestampUtc, nextUpdate);
        fixture.Time.SetUtcNow(timestamp.TimestampUtc + GracePeriod + TimeSpan.FromMinutes(1));

        await fixture.CreateRun().RunAsync();

        // CRL times have a precision of seconds.
        (await fixture.ReadTimestampAsync(anchor)).ConfirmationNotBeforeUtc.Should().BeCloseTo(nextUpdate, TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task RunAsync_CrlIssuedTooEarly_PacesTheChainWithoutFailureUntilALaterCrl()
    {
        var fixture = await CreateFixtureAsync();
        var first = await fixture.AnchorAsync();
        var second = await fixture.AnchorAsync();
        var firstTime = first.Timestamps[0].TimestampUtc;
        var secondTime = second.Timestamps[0].TimestampUtc;
        secondTime.Should().BeAfter(firstTime);
        var run = fixture.CreateRun();
        var early = fixture.TestAuthority.BuildCrl(firstTime + GracePeriod - TimeSpan.FromHours(1), firstTime + GracePeriod + TimeSpan.FromDays(6));
        fixture.TestAuthority.ServedCrl = early;

        // Only the first token is due; the CRL available was issued too early for it.
        fixture.Time.SetUtcNow(secondTime + GracePeriod - TimeSpan.FromMilliseconds(1));
        await run.RunAsync();
        var deferred = await fixture.ReadTimestampAsync(first);
        deferred.RevocationStatus.Should().Be(TimestampRevocationStatus.Unconfirmed);
        deferred.ConfirmationFailure.Should().BeNull();
        deferred.ConfirmationNotBeforeUtc.Should().BeCloseTo(fixture.Time.GetUtcNow().UtcDateTime + TimestampRevocationConfirmer.MaximumRetryDelay,
                                                             TimeSpan.FromMilliseconds(1));
        fixture.Logger.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Warning);

        // The second token became due meanwhile, but its chain waits.
        var requests = fixture.TestAuthority.CrlRequests;
        fixture.Time.Advance(TimeSpan.FromMinutes(30));
        await run.RunAsync();
        fixture.TestAuthority.CrlRequests.Should().Be(requests);
        (await fixture.ReadTimestampAsync(second)).ConfirmationNotBeforeUtc.Should().Be(deferred.ConfirmationNotBeforeUtc, "it waits with its chain");

        var later = fixture.TestAuthority.BuildCrl(secondTime + GracePeriod + TimeSpan.FromMinutes(1));
        fixture.TestAuthority.ServedCrl = later;
        fixture.Time.Advance(TimeSpan.FromMinutes(31));
        await run.RunAsync();

        foreach (var anchor in new[]
                 {
                     first,
                     second
                 })
        {
            var confirmed = await fixture.ReadTimestampAsync(anchor);
            confirmed.RevocationStatus.Should().Be(TimestampRevocationStatus.Confirmed);
            confirmed.ConfirmingRevocationLists.Should().ContainSingle().Which.Should().Equal(later);
        }
    }

    [Test]
    public async Task RunAsync_CrlUnavailable_CountsTheFailureAndRetriesAfterTheBackoff()
    {
        var fixture = await CreateFixtureAsync();
        var anchor = await fixture.AnchorAsync();
        fixture.TestAuthority.Mode = TestTimestampAuthorityMode.CrlUnavailable;
        fixture.Time.SetUtcNow(anchor.Timestamps[0].TimestampUtc + GracePeriod + TimeSpan.FromMinutes(1));
        var run = fixture.CreateRun();

        await run.RunAsync();
        var failedOnce = await fixture.ReadTimestampAsync(anchor);
        failedOnce.ConfirmationFailure.Should().NotBeNull();
        failedOnce.ConfirmationFailure.FailureCount.Should().Be(1);
        failedOnce.ConfirmationNotBeforeUtc.Should().BeCloseTo(fixture.Time.GetUtcNow().UtcDateTime + TimestampRevocationConfirmer.MaximumRetryDelay,
                                                               TimeSpan.FromMilliseconds(1));

        var requests = fixture.TestAuthority.CrlRequests;
        fixture.Time.Advance(TimeSpan.FromMinutes(30));
        await run.RunAsync();
        fixture.TestAuthority.CrlRequests.Should().Be(requests, "the chain waits after a failure");

        fixture.Time.Advance(TimeSpan.FromMinutes(31));
        await run.RunAsync();

        var failed = await fixture.ReadTimestampAsync(anchor);
        failed.RevocationStatus.Should().Be(TimestampRevocationStatus.Unconfirmed);
        failed.ConfirmationFailure.Should().NotBeNull();
        failed.ConfirmationFailure.FailureCount.Should().Be(2);
        failed.ConfirmationFailure.LastFailure.Should().Contain("could not be downloaded");
        failed.ConfirmationFailure.LastFailureUtc.Should().BeCloseTo(fixture.Time.GetUtcNow().UtcDateTime, TimeSpan.FromMilliseconds(1));
        fixture.Logger.Entries.Should().Contain(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("revocation status"));
    }

    [TestCase("broken signature")]
    [TestCase("delta CRL")]
    public async Task RunAsync_CustomAuthorityReturnsCrlThatDoesNotCoverTheChain_RecordsUnconfirmedWithoutFailure(String kind)
    {
        var fixture = await CreateFixtureAsync();
        var anchor = await fixture.AnchorAsync();
        var timestamp = anchor.Timestamps[0];
        var crl = kind == "broken signature"
            ? BreakSignature(fixture.TestAuthority.BuildCrl(timestamp.TimestampUtc + GracePeriod + TimeSpan.FromMinutes(1)))
            : fixture.TestAuthority.BuildCrlWithExtensions(timestamp.TimestampUtc + GracePeriod + TimeSpan.FromMinutes(1), ("2.5.29.27", true, [0x02, 0x01, 0x01]));
        var authority = new FixedRevocationListsAuthority(fixture.Authority, RevocationLists.Issued([crl]));
        fixture.Time.SetUtcNow(timestamp.TimestampUtc + GracePeriod + TimeSpan.FromMinutes(2));

        await fixture.CreateRun(authority).RunAsync();

        var unconfirmed = await fixture.ReadTimestampAsync(anchor);
        unconfirmed.RevocationStatus.Should().Be(TimestampRevocationStatus.Unconfirmed);
        unconfirmed.ConfirmationFailure.Should().BeNull("the chain still has no counting CRL, but that no longer throws; it is recorded and retried");
        unconfirmed.ConfirmationNotBeforeUtc.Should().BeCloseTo(fixture.Time.GetUtcNow().UtcDateTime + TimestampRevocationConfirmer.MaximumRetryDelay,
                                                                TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task RunAsync_CustomAuthorityReturnsNonUtcNextUpdate_RecordsConfirmationFailure()
    {
        var fixture = await CreateFixtureAsync();
        var anchor = await fixture.AnchorAsync();
        var authority = new NonUtcNextUpdateAuthority(fixture.Authority);
        fixture.Time.SetUtcNow(anchor.Timestamps[0].TimestampUtc + GracePeriod + TimeSpan.FromMinutes(1));

        await fixture.CreateRun(authority).RunAsync();

        var failed = await fixture.ReadTimestampAsync(anchor);
        failed.RevocationStatus.Should().Be(TimestampRevocationStatus.Unconfirmed);
        failed.ConfirmationFailure.Should().NotBeNull();
        failed.ConfirmationFailure.LastFailure.Should().Contain("Utc kind");
    }

    [TestCase("malformed", "not a single DER-encoded CRL")]
    [TestCase("issued too early", "before the required")]
    [TestCase("without next update", "states no next update")]
    public async Task RunAsync_CustomAuthorityReturnsUnusableCrl_CountsTheFailure(String kind, String reason)
    {
        var fixture = await CreateFixtureAsync();
        var anchor = await fixture.AnchorAsync();
        var timestamp = anchor.Timestamps[0];
        var crl = kind switch
        {
            "malformed" => "not a CRL"u8.ToArray(),
            "issued too early" => fixture.TestAuthority.BuildCrl(timestamp.TimestampUtc + GracePeriod - TimeSpan.FromMinutes(1)),
            _ => fixture.TestAuthority.BuildCrlWithoutNextUpdate(timestamp.TimestampUtc + GracePeriod + TimeSpan.FromMinutes(1))
        };
        var authority = new FixedRevocationListsAuthority(fixture.Authority, RevocationLists.Issued([crl]));
        fixture.Time.SetUtcNow(timestamp.TimestampUtc + GracePeriod + TimeSpan.FromMinutes(2));

        await fixture.CreateRun(authority).RunAsync();

        var failed = await fixture.ReadTimestampAsync(anchor);
        failed.RevocationStatus.Should().Be(TimestampRevocationStatus.Unconfirmed);
        failed.ConfirmationFailure.Should().NotBeNull();
        failed.ConfirmationFailure.LastFailure.Should().Contain(reason);
    }

    [Test]
    public async Task RunAsync_CustomAuthorityReturnsUtcNextUpdate_IsAcceptedAndUsedUnchanged()
    {
        var fixture = await CreateFixtureAsync();
        var anchor = await fixture.AnchorAsync();
        var timestamp = anchor.Timestamps[0];
        var nextUpdateUtc = timestamp.TimestampUtc + GracePeriod + TimeSpan.FromMinutes(20);
        var authority = new FixedRevocationListsAuthority(fixture.Authority, RevocationLists.IssuedTooEarly(nextUpdateUtc));
        fixture.Time.SetUtcNow(timestamp.TimestampUtc + GracePeriod + TimeSpan.FromMinutes(1));

        await fixture.CreateRun(authority).RunAsync();

        (await fixture.ReadTimestampAsync(anchor)).ConfirmationNotBeforeUtc.Should().Be(nextUpdateUtc);
    }

    [Test]
    public async Task RunAsync_GracePeriodNotPassed_LeavesTimestampUnconfirmedWithoutDownloadingCrls()
    {
        var fixture = await CreateFixtureAsync();
        var anchor = await fixture.AnchorAsync();
        var requests = fixture.TestAuthority.CrlRequests;
        fixture.Time.SetUtcNow(anchor.Timestamps[0].TimestampUtc + GracePeriod - TimeSpan.FromMinutes(1));

        await fixture.CreateRun().RunAsync();

        fixture.TestAuthority.CrlRequests.Should().Be(requests);
        var unconfirmed = await fixture.ReadTimestampAsync(anchor);
        unconfirmed.RevocationStatus.Should().Be(TimestampRevocationStatus.Unconfirmed);
        unconfirmed.RevocationStatusUtc.Should().BeNull();
        unconfirmed.ConfirmingRevocationLists.Should().BeEmpty();
    }

    [Test]
    public async Task RunAsync_NoTrustConfiguredForAuthority_DoesNotRequestCrlsAndRetriesLikeOtherFailures()
    {
        var fixture = await CreateFixtureAsync();
        var anchor = await fixture.AnchorAsync();
        var timestamp = anchor.Timestamps[0];
        fixture.Verification.Authorities.Remove(fixture.Authority.Name);
        fixture.Time.SetUtcNow(timestamp.TimestampUtc + GracePeriod + TimeSpan.FromMinutes(1));

        await fixture.CreateRun().RunAsync();

        var failed = await fixture.ReadTimestampAsync(anchor);
        failed.RevocationStatus.Should().Be(TimestampRevocationStatus.Unconfirmed);
        failed.ConfirmationFailure.Should().NotBeNull();
        failed.ConfirmationFailure.LastFailure.Should().Contain("no trust settings");
        fixture.TestAuthority.CrlRequests.Should().Be(1, "only stamping downloaded a CRL; confirmation never asked without configured trust");
    }

    [Test]
    public async Task RunAsync_PendingAnchorCannotComplete_StillConfirmsEarlierTimestamps()
    {
        var fixture = await CreateFixtureAsync();
        var anchor = await fixture.AnchorAsync();
        var timestamp = anchor.Timestamps[0];
        fixture.TestAuthority.Mode = TestTimestampAuthorityMode.HttpFailure;
        (await fixture.AnchorAsync(AnchoringRunOutcome.AwaitingTimestamps)).Status.Should().Be(AnchorStatus.AwaitingTimestamp);
        fixture.TestAuthority.ServedCrl = fixture.TestAuthority.BuildCrl(timestamp.TimestampUtc + GracePeriod + TimeSpan.FromMinutes(1));
        fixture.Time.SetUtcNow(timestamp.TimestampUtc + GracePeriod + TimeSpan.FromMinutes(2));

        var result = await fixture.CreateRun().RunAsync();

        result.Outcome.Should().Be(AnchoringRunOutcome.AwaitingTimestamps);
        (await fixture.ReadTimestampAsync(anchor)).RevocationStatus.Should().Be(TimestampRevocationStatus.Confirmed);
    }

    [TestCase(-1, true)]
    [TestCase(0, true)]
    [TestCase(60, false)]
    public async Task RunAsync_SigningCertificateRevoked_RecordsTheStatusAtTheTokenTime(Int32 revokedAfterTokenMinutes, Boolean revoked)
    {
        using var metrics = new AnchoringMetricsCollector();
        var fixture = await CreateFixtureAsync();
        var anchor = await fixture.AnchorAsync();
        var timestamp = anchor.Timestamps[0];
        var crl = fixture.TestAuthority.BuildCrl(timestamp.TimestampUtc + GracePeriod + TimeSpan.FromMinutes(1),
                                                 revoked: (fixture.TestAuthority.SigningCertificate.SerialNumberBytes.ToArray(),
                                                           timestamp.TimestampUtc + TimeSpan.FromMinutes(revokedAfterTokenMinutes)));
        fixture.TestAuthority.ServedCrl = crl;
        fixture.Time.SetUtcNow(timestamp.TimestampUtc + GracePeriod + TimeSpan.FromMinutes(2));

        await fixture.CreateRun().RunAsync();

        var recorded = await fixture.ReadTimestampAsync(anchor);
        recorded.RevocationStatus.Should().Be(revoked ? TimestampRevocationStatus.Revoked : TimestampRevocationStatus.Confirmed);
        recorded.ConfirmingRevocationLists.Should().ContainSingle().Which.Should().Equal(crl);
        metrics.Measurements.Count(measurement => measurement.Instrument == IntegrityMetrics.Instruments.RevokedTimestamps)
               .Should().Be(revoked ? 1 : 0);
        fixture.Logger.Entries.Any(entry => entry.Level == LogLevel.Error && entry.Message.Contains("as revoked")).Should().Be(revoked);
        (await fixture.ReadAnchorAsync(anchor)).Status.Should().Be(AnchorStatus.Completed);
    }

    [Test]
    public async Task RunAsync_StoredChainWithExtraCertificateNotOnTheTrustedPath_DoesNotRequestCrls()
    {
        var fixture = await CreateFixtureAsync();
        var anchor = await fixture.AnchorAsync();
        var timestamp = anchor.Timestamps[0];

        // The extra certificate names its own CRL distribution point, at a location distinct from the real chain's,
        // so a request to it is separately observable; it is a non-root authority certificate, which is exactly what
        // GetCrlSources reads a CRL location from, rather than the root TestTimestampAuthority itself carries no
        // distribution point extension on.
        var unrelatedCrlUri = new Uri("http://tsa.test/unrelated.crl");
        var unrelatedAuthority = new TestTimestampAuthority([unrelatedCrlUri]);
        _testAuthorities.Add(unrelatedAuthority);
        fixture.TestAuthority.AdditionalCrlUris.Add(unrelatedCrlUri);
        var evidence = new AnchorEvidenceStore(fixture.Context.MongoHelper, fixture.Options);
        var extraHash = await evidence.StoreAsync(unrelatedAuthority.SigningCertificate.RawData, CancellationToken.None);

        // The real chain still builds to the trusted root for the token's signer. The extra certificate is spliced in
        // before the stored root, exactly where GetCrlSources reads non-root certificates from (it excludes only the
        // final, root entry), so skipping the rebuild-and-compare guard would make the confirmer fetch its unrelated
        // CRL location; it must never be trusted for its URL.
        await fixture.Store.Anchors.UpdateOneAsync(new BsonDocument("_id", new BsonBinaryData(anchor.Id, GuidRepresentation.Standard)),
                                                   new BsonDocument("$push", new BsonDocument("Timestamps.0.CertificateChainHashes",
                                                                                              new BsonDocument
                                                                                              {
                                                                                                  {
                                                                                                      "$each", new BsonArray
                                                                                                      {
                                                                                                          new BsonBinaryData(extraHash)
                                                                                                      }
                                                                                                  },
                                                                                                  { "$position", 1 }
                                                                                              })));
        fixture.Time.SetUtcNow(timestamp.TimestampUtc + GracePeriod + TimeSpan.FromMinutes(1));

        await fixture.CreateRun().RunAsync();

        var failed = await fixture.ReadTimestampAsync(anchor);
        failed.RevocationStatus.Should().Be(TimestampRevocationStatus.Unconfirmed);
        failed.ConfirmationFailure.Should().NotBeNull();
        failed.ConfirmationFailure.LastFailure.Should().Contain("differs from the chain built");
        fixture.TestAuthority.CrlRequests.Should().Be(1, "only stamping downloaded a CRL; the untrusted stored chain's extra certificate's CRL location " +
                                                         "must never be fetched for confirmation");
    }

    private static Byte[] BreakSignature(Byte[] crl)
    {
        crl[^1] ^= 0x01;
        return crl;
    }

    private async Task<Fixture> CreateFixtureAsync(TestTimestampAuthority? testAuthority = null)
    {
        var context = await LedgerStoreContext.CreateAsync(_container);
        _contexts.Add(context);
        testAuthority ??= new TestTimestampAuthority();
        _testAuthorities.Add(testAuthority);
        var options = new IntegrityAnchoringOptions();
        await new IntegrityAnchoringConfigurator(options).ConfigureAsync(context.MongoHelper);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var authority = testAuthority.CreateAuthority(timeProvider: time);
        var verification = new IntegrityVerificationOptions();
        verification.Authorities[authority.Name] = new TimestampAuthorityTrust
        {
            TrustedRoots =
            {
                testAuthority.Root
            }
        };
        return new Fixture(context, options, testAuthority, authority, time, new CapturingLogger<AnchoringRun>(), verification);
    }

    /// <summary>
    /// An authority that stamps through another one and returns fixed CRLs, like a custom authority that does not check
    /// them.
    /// </summary>
    private sealed class FixedRevocationListsAuthority : ITimestampAuthority
    {
        private readonly ITimestampAuthority _inner;
        private readonly RevocationLists _lists;

        public FixedRevocationListsAuthority(ITimestampAuthority inner, RevocationLists lists)
        {
            _inner = inner;
            _lists = lists;
        }

        public String Name => _inner.Name;

        public Task<RevocationLists> GetRevocationListsAsync(IReadOnlyList<Byte[]> certificateChain,
                                                             DateTime issuedNotBeforeUtc,
                                                             CancellationToken cancellationToken)
            => Task.FromResult(_lists);

        public Task<TimestampToken> TimestampAsync(ReadOnlyMemory<Byte> digest, HashAlgorithmName algorithm, CancellationToken cancellationToken)
            => _inner.TimestampAsync(digest, algorithm, cancellationToken);
    }

    private sealed record Fixture(
        LedgerStoreContext Context,
        IntegrityAnchoringOptions Options,
        TestTimestampAuthority TestAuthority,
        ITimestampAuthority Authority,
        FakeTimeProvider Time,
        CapturingLogger<AnchoringRun> Logger,
        IntegrityVerificationOptions Verification)
    {
        public AnchorStore Store => new(Context.MongoHelper, Options);

        /// <summary>
        /// Anchors one new candidate in a run of its own and returns the stored anchor.
        /// </summary>
        public async Task<AnchorRecord> AnchorAsync(AnchoringRunOutcome expected = AnchoringRunOutcome.Completed, params ITimestampAuthority[] authorities)
        {
            var source = new InMemoryAnchorCandidateSource(InMemoryAnchorCandidateSource.CreateCandidates(1), Context.MongoHelper.Database);
            var result = await CreateRun(source, authorities.Length > 0 ? authorities : [Authority]).RunAsync();
            result.Outcome.Should().Be(expected);
            result.Anchor.Should().NotBeNull();
            return result.Anchor;
        }

        public AnchoringRun CreateRun(params ITimestampAuthority[] authorities)
            => CreateRun(new InMemoryAnchorCandidateSource([], Context.MongoHelper.Database), authorities.Length > 0 ? authorities : [Authority]);

        public AnchoringRun CreateRun(IAnchorCandidateSource source, params ITimestampAuthority[] authorities)
            => new(Context.MongoHelper, source, authorities, Options, Time, Logger, verification: Verification);

        public async Task<AnchorRecord> ReadAnchorAsync(AnchorRecord anchor)
            => await Store.FindAsync(anchor.Id, CancellationToken.None) ?? throw new InvalidOperationException("The anchor is gone.");

        /// <summary>
        /// Reads the only timestamp of a completed anchor with its evidence resolved.
        /// </summary>
        public async Task<AnchorTimestamp> ReadTimestampAsync(AnchorRecord anchor)
            => (await new AnchorEvidenceResolver(Context.MongoHelper, Options).ResolveAsync(await ReadAnchorAsync(anchor))).Timestamps.Single();
    }

    /// <summary>
    /// An authority that stamps through another one and reports its CRLs as issued too early for a non-UTC time, like a
    /// faulty custom authority that passes the caller-supplied time across the <see cref="ITimestampAuthority"/> boundary
    /// without preserving its UTC kind.
    /// </summary>
    private sealed class NonUtcNextUpdateAuthority : ITimestampAuthority
    {
        private readonly ITimestampAuthority _inner;

        public NonUtcNextUpdateAuthority(ITimestampAuthority inner)
        {
            _inner = inner;
        }

        public String Name => _inner.Name;

        public Task<RevocationLists> GetRevocationListsAsync(IReadOnlyList<Byte[]> certificateChain,
                                                             DateTime issuedNotBeforeUtc,
                                                             CancellationToken cancellationToken)
            => Task.FromResult(RevocationLists.IssuedTooEarly(DateTime.SpecifyKind(issuedNotBeforeUtc, DateTimeKind.Unspecified)));

        public Task<TimestampToken> TimestampAsync(ReadOnlyMemory<Byte> digest, HashAlgorithmName algorithm, CancellationToken cancellationToken)
            => _inner.TimestampAsync(digest, algorithm, cancellationToken);
    }
}
