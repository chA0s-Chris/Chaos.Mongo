// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity;

using Chaos.Mongo.Configuration;
using Chaos.Mongo.EventStore.Integrity;
using Chaos.Mongo.EventStore.Tests.Integration;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using NUnit.Framework;
using System.Runtime.CompilerServices;

public class IntegrityAnchoringRegistrationTests
{
    private static readonly Uri AuthorityUri = new("http://tsa.test/timestamp");

    [Test]
    public void WithIntegrityAnchoring_CalledTwice_ThrowsInvalidOperationException()
    {
        var builder = CreateBuilder().WithIntegrityAnchoring(anchoring => anchoring.WithRfc3161TimestampAuthority("tsa", AuthorityUri));

        var act = () => builder.WithIntegrityAnchoring(anchoring => anchoring.WithRfc3161TimestampAuthority("tsa", AuthorityUri));

        act.Should().Throw<InvalidOperationException>().WithMessage("*configure it once at module level*");
    }

    [Test]
    public void WithIntegrityAnchoring_CustomSettings_FillAnchoringOptions()
    {
        using var services = CreateBuilder().WithIntegrityAnchoring(anchoring => anchoring.WithRfc3161TimestampAuthority("first", AuthorityUri)
                                                                                          .WithRfc3161TimestampAuthority("second", AuthorityUri)
                                                                                          .WithInterval(TimeSpan.FromMinutes(1))
                                                                                          .WithDelayThreshold(TimeSpan.FromMinutes(30))
                                                                                          .WithMinimumTimestampTokens(2)
                                                                                          .WithMaxLeavesPerAnchor(10))
                                            .Services.BuildServiceProvider();

        var options = services.GetRequiredService<IntegrityAnchoringOptions>();

        options.Interval.Should().Be(TimeSpan.FromMinutes(1));
        options.DelayThreshold.Should().Be(TimeSpan.FromMinutes(30));
        options.MinimumTimestampTokens.Should().Be(2);
        options.MaxLeavesPerAnchor.Should().Be(10);
        services.GetRequiredService<AnchoringRun>().Should().NotBeNull();
    }

    [Test]
    public void WithIntegrityAnchoring_CustomTimestampAuthority_IsCreatedWithServiceProvider()
    {
        using var testAuthority = new TestTimestampAuthority();
        var authority = testAuthority.CreateAuthority();
        var received = new StrongBox<IServiceProvider?>();
        using var services = CreateBuilder().WithIntegrityAnchoring(anchoring => anchoring.WithTimestampAuthority(provider =>
                                            {
                                                received.Value = provider;
                                                return authority;
                                            }))
                                            .Services.BuildServiceProvider();

        services.GetRequiredService<AnchoringRun>();

        received.Value.Should().NotBeNull();
        received.Value!.GetRequiredService<IntegrityAnchoringOptions>().Should().BeSameAs(services.GetRequiredService<IntegrityAnchoringOptions>());
    }

    [Test]
    public void WithIntegrityAnchoring_Defaults_RegisterPipelineWithFiveMinuteIntervalAndOneHourThreshold()
    {
        using var services = CreateBuilder().WithIntegrityAnchoring(anchoring => anchoring.WithRfc3161TimestampAuthority("tsa", AuthorityUri))
                                            .Services.BuildServiceProvider();

        var options = services.GetRequiredService<IntegrityAnchoringOptions>();

        options.Interval.Should().Be(TimeSpan.FromMinutes(5));
        options.DelayThreshold.Should().Be(TimeSpan.FromHours(1));
        options.MinimumTimestampTokens.Should().Be(1);
        options.MaxLeavesPerAnchor.Should().Be(100_000);
        services.GetRequiredService<IAnchorCandidateSource>().Should().BeOfType<ChangeStreamAnchorCandidateSource>();
        services.GetServices<IHostedService>().Should().ContainSingle(service => service is IntegrityAnchoringHostedService);
        services.GetServices<IMongoConfigurator>().Should().ContainSingle(configurator => configurator is IntegrityAnchoringConfigurator);
        services.GetRequiredService<AnchoringRun>().Should().NotBeNull();
        services.GetRequiredService<AnchorLeafReader>().Should().NotBeNull();
        services.GetRequiredService<SealingAnchorLookup>().Should().NotBeNull();
    }

    [Test]
    public void WithIntegrityAnchoring_DuplicateAuthorityName_ThrowsArgumentException()
    {
        var act = () => CreateBuilder().WithIntegrityAnchoring(anchoring => anchoring.WithRfc3161TimestampAuthority("tsa", AuthorityUri)
                                                                                     .WithRfc3161TimestampAuthority("tsa", AuthorityUri));

        act.Should().Throw<ArgumentException>().WithMessage("*'tsa' is used more than once*");
    }

    [Test]
    public void WithIntegrityAnchoring_MinimumAboveAuthorityCount_ThrowsInvalidOperationException()
    {
        var act = () => CreateBuilder().WithIntegrityAnchoring(anchoring => anchoring.WithRfc3161TimestampAuthority("tsa", AuthorityUri)
                                                                                     .WithMinimumTimestampTokens(2));

        act.Should().Throw<InvalidOperationException>().WithMessage("*between 1 and the number of authorities (1)*");
    }

    [Test]
    public void WithIntegrityAnchoring_NoTimestampAuthority_ThrowsWithClearError()
    {
        var act = () => CreateBuilder().WithIntegrityAnchoring(anchoring => anchoring.WithInterval(TimeSpan.FromMinutes(1)));

        act.Should().Throw<InvalidOperationException>().WithMessage("*at least one timestamp authority*no default authority*");
    }

    [Test]
    public void WithIntegrityAnchoring_NonPositiveInterval_ThrowsInvalidOperationException()
    {
        var act = () => CreateBuilder().WithIntegrityAnchoring(anchoring => anchoring.WithRfc3161TimestampAuthority("tsa", AuthorityUri)
                                                                                     .WithInterval(TimeSpan.Zero));

        act.Should().Throw<InvalidOperationException>().WithMessage("*must be positive*");
    }

    private static MongoBuilder CreateBuilder()
        => new ServiceCollection().AddLogging().AddMongo(MongoUrl.Create("mongodb://localhost:27017"), configure: options => options.DefaultDatabase = "test")
                                  .WithEventStore<LedgerAggregate>(es => es.WithEvent<LedgerEntryRecordedEvent>("LedgerEntryRecorded")
                                                                           .WithCollectionPrefix("Ledgers")
                                                                           .WithIntegrityProtection());
}
