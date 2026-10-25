// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Chaos.Mongo.EventStore.Integrity;
using Chaos.Mongo.EventStore.Tests.Integrity;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Testcontainers.MongoDb;

/// <summary>
/// Uses the feature through its public services, as a consumer would. Only the sealing pass and the anchoring run are
/// driven through internal services, so the test does not wait for their background loops.
/// </summary>
public class PublicIntegrityApiIntegrationTests
{
    private MongoDbContainer _container;

    [OneTimeSetUp]
    public async Task GetMongoDbContainer() => _container = await MongoDbTestContainer.StartContainerAsync();

    [Test]
    public async Task PublicServices_ProtectedStream_AreAnchoredVerifiedAndErased()
    {
        using var testAuthority = new TestTimestampAuthority();
        var root = testAuthority.Root;
        var authority = testAuthority.CreateAuthority();
        await using var context = await LedgerStoreContext.CreateAsync(
            _container,
            personalData: true,
            configureMongo: mongo => mongo.WithIntegrityAnchoring(anchoring => anchoring.WithTimestampAuthority(_ => authority))
                                          .WithIntegrityVerification(verification => verification.WithTimestampAuthorityTrust(
                                                                         "test-tsa",
                                                                         trust => trust.TrustedRoots.Add(root))));
        var integrity = context.ServiceProvider.GetRequiredService<IEventStoreIntegrity<LedgerAggregate>>();
        var anchors = context.ServiceProvider.GetRequiredService<IAnchorVerifier>();
        var aggregateId = await context.AppendStreamAsync(2);
        var statusBeforePass = await integrity.GetSealingStatusAsync();
        await context.Sweep.RunPassAsync();
        var run = context.ServiceProvider.GetRequiredService<AnchoringRun>();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while ((await integrity.VerifyEventAnchorAsync(aggregateId, 2)).Status != EventAnchorStatus.Anchored)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The stream was not anchored within 15 seconds.");
            }

            await run.RunAsync();
        }

        using (var scope = context.ServiceProvider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPersonalDataEraser<LedgerAggregate>>()
                       .ErasePersonalDataAsync(aggregateId, new ErasureRequest("TICKET-1", [1], ["Memo"]));
        }

        var stream = await integrity.VerifyStreamAsync(aggregateId);
        var derived = await integrity.VerifyDerivedDataAsync(aggregateId);
        var findings = new List<CompletenessFinding>();
        await foreach (var finding in integrity.CheckCompletenessAsync())
        {
            findings.Add(finding);
        }

        statusBeforePass.Should().BeNull();
        (await integrity.GetSealingStatusAsync()).Should()
                                                 .Match<IntegritySealingStatus?>(s => s != null && !s.IsPassInProgress && s.IsCompleteWithoutFailures && s.StreamsChecked == 1);
        stream.IsIntact.Should().BeTrue();
        stream.Events.Single(e => e.Version == 1).PersonalData.Single(p => p.Path == "Memo").Status.Should().Be(PersonalDataStatus.ErasedWithRecord);
        derived.IsConsistent.Should().BeTrue();
        findings.Should().ContainSingle().Which.Kind.Should().Be(CompletenessFindingKind.Unanchored, "the erasure event is not anchored yet");
        (await anchors.VerifyAnchorChainAsync()).IsIntact.Should().BeTrue();
    }
}
