// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity;

using Chaos.Mongo.EventStore.Integrity;
using Chaos.Mongo.EventStore.Tests.Integration;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using NUnit.Framework;

public class AnchorCandidateRegistrationTests
{
    [Test]
    public void AddMongo_ProtectedEventStore_RegistersProtectedStreamType()
    {
        using var services = CreateServices(_ => { });

        var registry = services.GetRequiredService<ProtectedStreamRegistry>();

        var type = registry.Types.Should().ContainSingle().Subject;
        type.AggregateTypeName.Should().Be(nameof(LedgerAggregate));
        type.EventsCollectionName.Should().Be("Ledgers_Events");
        type.AggregateIdElement.Should().Be(nameof(Event<LedgerAggregate>.AggregateId));
        type.VersionElement.Should().Be(nameof(Event<LedgerAggregate>.Version));
        type.NextStream(null).Should().BeEquivalentTo(SealingSweepQueries<LedgerAggregate>.NextStream(null));
        var aggregateId = Guid.NewGuid();
        type.StreamHead(aggregateId).Should().BeEquivalentTo(SealingSweepQueries<LedgerAggregate>.StreamHead(aggregateId));
    }

    [Test]
    public void AddMongo_ProtectedTypesWithSameEventsCollection_Throws()
    {
        var builder = new ServiceCollection().AddMongo(MongoUrl.Create("mongodb://localhost:27017"), configure: options => options.DefaultDatabase = "test")
                                             .WithEventStore<LedgerAggregate>(ConfigureLedger);

        var act = () => builder.WithEventStore<ShipmentAggregate>(es => es.WithEvent<ShipmentDispatchedEvent>("ShipmentDispatched")
                                                                          .WithCollectionPrefix("Ledgers")
                                                                          .WithIntegrityProtection());

        act.Should().Throw<InvalidOperationException>().WithMessage("*share the events collection 'Ledgers_Events'*");
    }

    [Test]
    public void AddMongo_ProtectedTypesWithSameName_Throws()
    {
        var builder = new ServiceCollection().AddMongo(MongoUrl.Create("mongodb://localhost:27017"), configure: options => options.DefaultDatabase = "test")
                                             .WithEventStore<LedgerAggregate>(ConfigureLedger);

        var act = () => builder.WithEventStore<DuplicateNames.LedgerAggregate>(es => es.WithEvent<DuplicateNames.DuplicateLedgerEntryRecordedEvent>("DuplicateLedgerEntryRecorded")
                                                                                       .WithCollectionPrefix("OtherLedgers")
                                                                                       .WithIntegrityProtection());

        act.Should().Throw<InvalidOperationException>().WithMessage("*share the name 'LedgerAggregate'*");
    }

    [Test]
    public void AddMongo_UnprotectedEventStore_RegistersNoProtectedStreamType()
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddMongo(MongoUrl.Create("mongodb://localhost:27017"), configure: options => options.DefaultDatabase = "test")
                         .WithEventStore<LedgerAggregate>(es => es.WithEvent<LedgerEntryRecordedEvent>("LedgerEntryRecorded")
                                                                  .WithEvent<LedgerAuditedEvent>("LedgerAudited")
                                                                  .WithCollectionPrefix("Ledgers"));
        using var services = serviceCollection.BuildServiceProvider();

        services.GetService<ProtectedStreamRegistry>().Should().BeNull();
    }

    [Test]
    public void Reduce_SeveralVersionsPerStream_KeepsHighestVersionPerStream()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var reduced = AnchorCandidateConsumer.Reduce(
        [
            new AnchorCandidate("A", first, 1, [1]),
            new AnchorCandidate("A", first, 3, [3]),
            new AnchorCandidate("A", first, 2, [2]),
            new AnchorCandidate("A", second, 1, [4]),
            new AnchorCandidate("B", first, 5, [5])
        ]);

        reduced.Select(candidate => (candidate.AggregateType, candidate.AggregateId, candidate.Version))
               .Should().BeEquivalentTo([("A", first, 3L), ("A", second, 1L), ("B", first, 5L)]);
    }

    [Test]
    [TestCase(true)]
    [TestCase(false)]
    public void WithAnchorCandidates_BeforeOrAfterEventStore_ResolvesSourceOverProtectedTypes(Boolean enableFirst)
    {
        var serviceCollection = new ServiceCollection();
        var builder = serviceCollection.AddMongo(MongoUrl.Create("mongodb://localhost:27017"), configure: options => options.DefaultDatabase = "test");
        if (enableFirst)
        {
            builder.WithAnchorCandidates();
        }

        builder.WithEventStore<LedgerAggregate>(ConfigureLedger);
        builder.WithAnchorCandidates();
        using var services = serviceCollection.BuildServiceProvider();

        services.GetRequiredService<IAnchorCandidateSource>().Should().BeOfType<ChangeStreamAnchorCandidateSource>();
        services.GetRequiredService<ProtectedStreamRegistry>().Types.Should().ContainSingle();
        serviceCollection.Count(s => s.ServiceType == typeof(IAnchorCandidateSource)).Should().Be(1);
        serviceCollection.Count(s => s.ServiceType == typeof(IntegrityAnchoringOptions)).Should().Be(1);
    }

    [Test]
    public void WithAnchorCandidates_NotCalled_RegistersNoSource()
    {
        using var services = CreateServices(_ => { });

        services.GetService<IAnchorCandidateSource>().Should().BeNull();
        services.GetService<IntegrityAnchoringOptions>().Should().BeNull();
    }

    [Test]
    public void WithAnchorCandidates_WithoutProtectedEventStore_ResolvesSource()
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddMongo(MongoUrl.Create("mongodb://localhost:27017"), configure: options => options.DefaultDatabase = "test")
                         .WithAnchorCandidates();
        using var services = serviceCollection.BuildServiceProvider();

        services.GetRequiredService<IAnchorCandidateSource>().Should().BeOfType<ChangeStreamAnchorCandidateSource>();
    }

    private static void ConfigureLedger(MongoEventStoreBuilder<LedgerAggregate> es)
        => es.WithEvent<LedgerEntryRecordedEvent>("LedgerEntryRecorded")
             .WithEvent<LedgerAuditedEvent>("LedgerAudited")
             .WithCollectionPrefix("Ledgers")
             .WithIntegrityProtection();

    private static ServiceProvider CreateServices(Action<MongoBuilder> configure)
    {
        var serviceCollection = new ServiceCollection();
        var builder = serviceCollection.AddMongo(MongoUrl.Create("mongodb://localhost:27017"), configure: options => options.DefaultDatabase = "test")
                                       .WithEventStore<LedgerAggregate>(ConfigureLedger);
        configure(builder);
        return serviceCollection.BuildServiceProvider();
    }
}
