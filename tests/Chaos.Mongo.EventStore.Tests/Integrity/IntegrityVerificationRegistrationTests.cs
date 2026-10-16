// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity;

using Chaos.Mongo.EventStore.Integrity;
using Chaos.Mongo.EventStore.Tests.Integration;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using NUnit.Framework;

public class IntegrityVerificationRegistrationTests
{
    private static readonly Uri AuthorityUri = new("http://tsa.test/timestamp");

    [Test]
    public void WithEventStore_IntegrityProtection_RegistersPublicServicesOnlyForProtectedTypes()
    {
        var services = new ServiceCollection();
        var builder = new MongoBuilder(services);

        builder.WithEventStore<UnprotectedRegistrationAggregate>(es => es.WithEvent<UnprotectedRegistrationEvent>())
               .WithEventStore<ProtectedRegistrationAggregate>(es => es.WithEvent<ProtectedRegistrationEvent>()
                                                                       .WithIntegrityProtection(protection => protection.WithSealingChunkSize(10)
                                                                                                    .WithSealingSweepInterval(TimeSpan.FromHours(2))
                                                                                                    .WithSealingSweepRetryDelay(TimeSpan.FromSeconds(30))));
        var withoutAnchors = services.Any(s => s.ServiceType == typeof(IAnchorVerifier));
        builder.WithIntegrityVerification();

        services.Should().Contain(s => s.ServiceType == typeof(IEventStoreIntegrity<ProtectedRegistrationAggregate>));
        services.Should().Contain(s => s.ServiceType == typeof(IPersonalDataEraser<ProtectedRegistrationAggregate>));
        services.Should().NotContain(s => s.ServiceType == typeof(IEventStoreIntegrity<UnprotectedRegistrationAggregate>));
        services.Should().NotContain(s => s.ServiceType == typeof(IPersonalDataEraser<UnprotectedRegistrationAggregate>));
        withoutAnchors.Should().BeFalse("anchor verification is registered at module level");
        services.Should().ContainSingle(s => s.ServiceType == typeof(IAnchorVerifier));
        var options = services.Single(s => s.ServiceType == typeof(MongoEventStoreOptions<ProtectedRegistrationAggregate>))
                              .ImplementationInstance.Should().BeOfType<MongoEventStoreOptions<ProtectedRegistrationAggregate>>().Subject;
        options.IntegrityProtectionEnabled.Should().BeTrue();
        options.SealingChunkSize.Should().Be(10);
        options.SealingSweepInterval.Should().Be(TimeSpan.FromHours(2));
        options.SealingSweepRetryDelay.Should().Be(TimeSpan.FromSeconds(30));
        typeof(IEventStore<>).GetMethods().Select(m => m.Name).Should().NotContain(name => name.Contains("Erase") || name.Contains("Verify"));
    }

    [Test]
    public void WithIntegrityAnchoring_AfterVerificationWithNamedCollections_RequiresTheSameNames()
    {
        var builder = CreateBuilder().WithIntegrityVerification(v => v.WithAnchorCollections("A", "L", "E", "C"));

        var act = () => builder.WithIntegrityAnchoring(a => a.WithRfc3161TimestampAuthority("tsa", AuthorityUri));

        act.Should().Throw<InvalidOperationException>().WithMessage("*anchor collections*");
    }

    [Test]
    public void WithIntegrityAnchoring_Rfc3161Authority_ContributesTrustThatExplicitTrustOverrides()
    {
        using var testAuthority = new TestTimestampAuthority();
        var root = testAuthority.Root;
        using var services = CreateBuilder().WithIntegrityAnchoring(anchoring => anchoring
                                                                                 .WithRfc3161TimestampAuthority("contributed", AuthorityUri, options =>
                                                                                 {
                                                                                     options.PolicyId = "1.2.3";
                                                                                     options.TrustedRoots.Add(root);
                                                                                 })
                                                                                 .WithRfc3161TimestampAuthority("overridden", AuthorityUri))
                                            .WithIntegrityVerification(verification => verification.WithTimestampAuthorityTrust("overridden", trust => trust.PolicyId = "4.5.6"))
                                            .Services.BuildServiceProvider();

        var options = services.GetRequiredService<IntegrityVerificationOptions>();

        var contributed = options.FindTrust("contributed");
        var overridden = options.FindTrust("overridden");
        contributed.Should().NotBeNull();
        contributed.PolicyId.Should().Be("1.2.3");
        contributed.TrustedRoots.Should().ContainSingle();
        overridden.Should().NotBeNull();
        overridden.PolicyId.Should().Be("4.5.6");
        options.FindTrust("unknown").Should().BeNull();
        services.GetRequiredService<AnchorVerifier>().Should().NotBeNull();
    }

    [Test]
    public void WithIntegrityProtection_InvalidSealingSettings_Throw()
    {
        var builder = new MongoBuilder(new ServiceCollection());

        var chunkSize = () => builder.WithEventStore<InvalidSettingsAggregate>(es => es.WithIntegrityProtection(p => p.WithSealingChunkSize(0)));
        var interval = () => builder.WithEventStore<InvalidSettingsAggregate>(es => es.WithIntegrityProtection(p => p.WithSealingSweepInterval(TimeSpan.Zero)));
        var retryDelay = () => builder.WithEventStore<InvalidSettingsAggregate>(es => es.WithIntegrityProtection(p => p.WithSealingSweepRetryDelay(TimeSpan.Zero)));

        chunkSize.Should().Throw<ArgumentOutOfRangeException>();
        interval.Should().Throw<ArgumentOutOfRangeException>();
        retryDelay.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    [TestCase(false)]
    [TestCase(true)]
    public void WithIntegrityVerification_AnchorCollectionsOfAnchoring_AreAccepted(Boolean verificationFirst)
    {
        var builder = CreateBuilder();
        Action<IntegrityVerificationBuilder> verification = v => v.WithAnchorCollections("A", "L", "E", "C");
        Action<IntegrityAnchoringBuilder> anchoring = a =>
        {
            a.WithRfc3161TimestampAuthority("tsa", AuthorityUri);
            a.Options.AnchorsCollectionName = "A";
            a.Options.AnchorLeavesCollectionName = "L";
            a.Options.AnchorEvidenceCollectionName = "E";
            a.Options.AnchorEvidenceChunksCollectionName = "C";
        };

        var act = () => (verificationFirst
            ? builder.WithIntegrityVerification(verification).WithIntegrityAnchoring(anchoring)
            : builder.WithIntegrityAnchoring(anchoring).WithIntegrityVerification(verification)).Services.BuildServiceProvider().Dispose();

        act.Should().NotThrow();
    }

    [Test]
    public void WithIntegrityVerification_CalledTwice_ThrowsInvalidOperationException()
    {
        var builder = CreateBuilder().WithIntegrityVerification();

        var act = () => builder.WithIntegrityVerification();

        act.Should().Throw<InvalidOperationException>().WithMessage("*configure it once at module level*");
    }

    [Test]
    public void WithIntegrityVerification_DuplicateTrust_ThrowsArgumentException()
    {
        var act = () => CreateBuilder().WithIntegrityVerification(verification => verification.WithTimestampAuthorityTrust("tsa")
                                                                                              .WithTimestampAuthorityTrust("tsa"));

        act.Should().Throw<ArgumentException>().WithMessage("*more than once*");
    }

    [Test]
    public void WithIntegrityVerification_InvalidSettings_Throw()
    {
        var gracePeriod = () => CreateBuilder().WithIntegrityVerification(verification => verification.WithRevocationGracePeriod(TimeSpan.Zero));
        var collections = () => CreateBuilder().WithIntegrityVerification(verification => verification.WithAnchorCollections("a", " ", "c", "d"));

        gracePeriod.Should().Throw<ArgumentOutOfRangeException>();
        collections.Should().Throw<ArgumentException>();
    }

    [Test]
    [TestCase(false)]
    [TestCase(true)]
    public void WithIntegrityVerification_OtherAnchorCollectionsThanAnchoring_ThrowsInvalidOperationException(Boolean verificationFirst)
    {
        var builder = CreateBuilder();
        Action<IntegrityVerificationBuilder> verification = v => v.WithAnchorCollections("A", "L", "E", "C");
        Action<IntegrityAnchoringBuilder> anchoring = a => a.WithRfc3161TimestampAuthority("tsa", AuthorityUri);
        if (verificationFirst)
        {
            builder.WithIntegrityVerification(verification);
            anchoring += a => a.Options.AnchorsCollectionName = "Other";
        }
        else
        {
            builder.WithIntegrityAnchoring(anchoring);
        }

        var act = () => _ = verificationFirst ? builder.WithIntegrityAnchoring(anchoring) : builder.WithIntegrityVerification(verification);

        act.Should().Throw<InvalidOperationException>().WithMessage("*anchor collections*");
    }

    [Test]
    public void WithIntegrityVerification_Settings_FillOptionsWithoutAnchoring()
    {
        using var services = CreateBuilder().WithIntegrityVerification(verification => verification.WithAnchorCollections("A", "L", "E", "C")
                                                                                                   .WithRevocationGracePeriod(TimeSpan.FromHours(2)))
                                            .Services.BuildServiceProvider();

        var anchoring = services.GetRequiredService<IntegrityAnchoringOptions>();

        anchoring.AnchorsCollectionName.Should().Be("A");
        anchoring.AnchorLeavesCollectionName.Should().Be("L");
        anchoring.AnchorEvidenceCollectionName.Should().Be("E");
        anchoring.AnchorEvidenceChunksCollectionName.Should().Be("C");
        services.GetRequiredService<IntegrityVerificationOptions>().RevocationGracePeriod.Should().Be(TimeSpan.FromHours(2));
        services.GetService<AnchoringRun>().Should().BeNull();
        services.GetRequiredService<EventStreamVerifier<LedgerAggregate>>().Should().NotBeNull();
    }

    private static MongoBuilder CreateBuilder()
        => new ServiceCollection().AddLogging().AddMongo(MongoUrl.Create("mongodb://localhost:27017"), configure: options => options.DefaultDatabase = "test")
                                  .WithEventStore<LedgerAggregate>(es => es.WithEvent<LedgerEntryRecordedEvent>("LedgerEntryRecorded")
                                                                           .WithCollectionPrefix("Ledgers")
                                                                           .WithIntegrityProtection());
}

public sealed class ProtectedRegistrationAggregate : Aggregate;

public sealed class ProtectedRegistrationEvent : Event<ProtectedRegistrationAggregate>
{
    public override void Execute(ProtectedRegistrationAggregate aggregate) { }
}

public sealed class UnprotectedRegistrationAggregate : Aggregate;

public sealed class UnprotectedRegistrationEvent : Event<UnprotectedRegistrationAggregate>
{
    public override void Execute(UnprotectedRegistrationAggregate aggregate) { }
}

public sealed class InvalidSettingsAggregate : Aggregate;
