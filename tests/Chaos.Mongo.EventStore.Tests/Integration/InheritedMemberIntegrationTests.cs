// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Chaos.Mongo.Configuration;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using NUnit.Framework;
using Testcontainers.MongoDb;

/// <summary>
/// Covers members that aggregates and events inherit from intermediate base classes.
/// </summary>
public class InheritedMemberIntegrationTests
{
    private IAggregateRepository<InheritedLedgerAggregate> _aggregateRepository;
    private MongoDbContainer _container;
    private IEventStore<InheritedLedgerAggregate> _eventStore;
    private IMongoHelper _mongoHelper;
    private MongoEventStoreOptions<InheritedLedgerAggregate> _options;
    private ServiceProvider _serviceProvider;

    [Test]
    public async Task AppendEventsAsync_InheritedAggregateMember_IsStoredInReadModelAndCheckpoint()
    {
        var aggregateId = Guid.NewGuid();

        await _eventStore.AppendEventsAsync([Assigned(aggregateId, 1, "alice"), Assigned(aggregateId, 2, "bob")]);

        var readModel = await _aggregateRepository.GetAsync(aggregateId);
        readModel.Should().NotBeNull();
        readModel.Owner.Should().Be("bob");
        readModel.Assignments.Should().Be(2);
        var checkpoint = await _mongoHelper.Database
                                           .GetCollection<CheckpointDocument<InheritedLedgerAggregate>>(_options.CheckpointCollectionName)
                                           .Find(Builders<CheckpointDocument<InheritedLedgerAggregate>>.Filter.Eq(c => c.Id.AggregateId, aggregateId))
                                           .SingleAsync();
        checkpoint.State.Owner.Should().Be("bob");
        var atCheckpoint = await _aggregateRepository.GetAtVersionAsync(aggregateId, 2);
        atCheckpoint.Should().NotBeNull();
        atCheckpoint.Owner.Should().Be("bob");
    }

    [Test]
    public async Task AppendEventsAsync_InheritedEventMember_IsStoredAndReplayed()
    {
        var aggregateId = Guid.NewGuid();

        await _eventStore.AppendEventsAsync([Assigned(aggregateId, 1, "alice")]);

        var events = await ReadEventsAsync(aggregateId);
        events.Should().ContainSingle().Which.Should().BeOfType<InheritedOwnerAssignedEvent>().Which.Owner.Should().Be("alice");
        var replayed = await _aggregateRepository.GetAtVersionAsync(aggregateId, 1);
        replayed.Should().NotBeNull();
        replayed.Owner.Should().Be("alice");
    }

    [Test]
    public async Task GetAtVersionAsync_ObservationalEventWithIntermediateBase_IsExcludedOnTheServer()
    {
        var aggregateId = Guid.NewGuid();
        await _eventStore.AppendEventsAsync([Assigned(aggregateId, 1, "alice")]);
        await _eventStore.AppendEventsAsync([
            new InheritedLedgerInspectedEvent
            {
                AggregateId = aggregateId,
                Version = 2,
                Inspector = "auditor"
            }
        ]);
        var events = await ReadEventsAsync(aggregateId);

        // The stored observational event can no longer be deserialized, so replay only succeeds if the server
        // never returns it.
        await _mongoHelper.Database
                          .GetCollection<BsonDocument>(_options.EventsCollectionName)
                          .UpdateOneAsync(
                              Builders<BsonDocument>.Filter.Eq(nameof(Event<InheritedLedgerAggregate>.AggregateId),
                                                               new BsonBinaryData(aggregateId, GuidRepresentation.Standard)) &
                              Builders<BsonDocument>.Filter.Eq(nameof(Event<InheritedLedgerAggregate>.Version), 2),
                              Builders<BsonDocument>.Update.Set(nameof(InheritedLedgerInspectionEvent.Inspector), 42));
        var replayed = await _aggregateRepository.GetAtVersionAsync(aggregateId, 2);

        events[1].Should().BeOfType<InheritedLedgerInspectedEvent>().Which.Inspector.Should().Be("auditor");
        replayed.Should().NotBeNull();
        replayed.Owner.Should().Be("alice");
    }

    [OneTimeSetUp]
    public async Task GetMongoDbContainer() => _container = await MongoDbTestContainer.StartContainerAsync();

    [SetUp]
    public async Task Setup()
    {
        var url = MongoUrl.Create(_container.GetConnectionString());
        _serviceProvider = new ServiceCollection()
                           .AddMongo(url, configure: options =>
                           {
                               options.DefaultDatabase = $"InheritedMemberTestDb_{Guid.NewGuid():N}";
                               options.RunConfiguratorsOnStartup = false;
                           })
                           .WithEventStore<InheritedLedgerAggregate>(es => es.WithEvent<InheritedOwnerAssignedEvent>("OwnerAssigned")
                                                                             .WithEvent<InheritedLedgerInspectedEvent>("LedgerInspected")
                                                                             .WithCollectionPrefix("InheritedLedgers")
                                                                             .WithCheckpoints(2))
                           .Services
                           .BuildServiceProvider();

        _eventStore = _serviceProvider.GetRequiredService<IEventStore<InheritedLedgerAggregate>>();
        _aggregateRepository = _serviceProvider.GetRequiredService<IAggregateRepository<InheritedLedgerAggregate>>();
        _mongoHelper = _serviceProvider.GetRequiredService<IMongoHelper>();
        _options = _serviceProvider.GetRequiredService<MongoEventStoreOptions<InheritedLedgerAggregate>>();

        foreach (var configurator in _serviceProvider.GetServices<IMongoConfigurator>())
        {
            await configurator.ConfigureAsync(_mongoHelper);
        }
    }

    [TearDown]
    public async Task TearDown() => await _serviceProvider.DisposeAsync();

    private static InheritedOwnerAssignedEvent Assigned(Guid aggregateId, Int64 version, String owner)
        => new()
        {
            AggregateId = aggregateId,
            Version = version,
            Owner = owner
        };

    private async Task<List<Event<InheritedLedgerAggregate>>> ReadEventsAsync(Guid aggregateId)
    {
        var events = new List<Event<InheritedLedgerAggregate>>();
        await foreach (var @event in _eventStore.GetEventStreamAsync(aggregateId))
        {
            events.Add(@event);
        }

        return events;
    }
}

public abstract class InheritedOwnedAggregate : Aggregate
{
    public String? Owner { get; set; }
}

public sealed class InheritedLedgerAggregate : InheritedOwnedAggregate
{
    public Int32 Assignments { get; set; }
}

public abstract class InheritedOwnershipEvent : Event<InheritedLedgerAggregate>
{
    public String? Owner { get; set; }
}

public sealed class InheritedOwnerAssignedEvent : InheritedOwnershipEvent
{
    public override void Execute(InheritedLedgerAggregate aggregate)
    {
        aggregate.Owner = Owner;
        aggregate.Assignments++;
    }
}

public abstract class InheritedLedgerInspectionEvent : ObservationalEvent<InheritedLedgerAggregate>
{
    public String? Inspector { get; set; }
}

public sealed class InheritedLedgerInspectedEvent : InheritedLedgerInspectionEvent;
