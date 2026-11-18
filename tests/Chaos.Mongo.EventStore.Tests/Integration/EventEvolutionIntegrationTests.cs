// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Chaos.Mongo.Configuration;
using Chaos.Mongo.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using NUnit.Framework;
using Testcontainers.MongoDb;

/// <summary>
/// Pins what the event store does with stored events whose shape no longer matches the current event class, as
/// documented under "Event Evolution" in <c>docs/event-store.md</c>. Each test appends an event, rewrites the stored
/// document to the shape an earlier or later class version would have written, and reads it back through the event
/// store. An element the class no longer declares is covered by
/// <c>ExtraElementToleranceTests.EventClassMap_DocumentWithExtraElement_StillDeserializes</c>.
/// </summary>
[Category("Integration")]
public class EventEvolutionIntegrationTests
{
    private IAggregateRepository<EvolvingStockAggregate> _aggregateRepository;
    private MongoDbContainer _container;
    private IEventStore<EvolvingStockAggregate> _eventStore;
    private IMongoHelper _mongoHelper;
    private MongoEventStoreOptions<EvolvingStockAggregate> _options;
    private ServiceProvider _serviceProvider;

    [OneTimeSetUp]
    public async Task GetMongoDbContainer() => _container = await MongoDbTestContainer.StartContainerAsync();

    [SetUp]
    public async Task Setup()
    {
        var url = MongoUrl.Create(_container.GetConnectionString());
        _serviceProvider = new ServiceCollection()
                           .AddMongo(url, configure: options =>
                           {
                               options.DefaultDatabase = TestDatabases.NewName("EventEvolutionTestDb");
                               options.RunConfiguratorsOnStartup = false;
                           })
                           .WithEventStore<EvolvingStockAggregate>(es => es.WithEvent<EvolvingStockReceivedEvent>("StockReceived")
                                                                           .WithEvent<EvolvingStockAuditedEvent>("StockAudited")
                                                                           .WithCollectionPrefix("EvolvingStock"))
                           .Services
                           .BuildServiceProvider();

        _eventStore = _serviceProvider.GetRequiredService<IEventStore<EvolvingStockAggregate>>();
        _aggregateRepository = _serviceProvider.GetRequiredService<IAggregateRepository<EvolvingStockAggregate>>();
        _mongoHelper = _serviceProvider.GetRequiredService<IMongoHelper>();
        _options = _serviceProvider.GetRequiredService<MongoEventStoreOptions<EvolvingStockAggregate>>();

        foreach (var configurator in _serviceProvider.GetServices<IMongoConfigurator>())
        {
            await configurator.ConfigureAsync(_mongoHelper);
        }
    }

    [TearDown]
    public async Task TearDown() => await _serviceProvider.DisposeAsync();

    [Test]
    public async Task GetAtVersionAsync_StoredEventLacksDeclaredElements_ReplaysWithMemberDefaults()
    {
        var aggregateId = Guid.NewGuid();
        await _eventStore.AppendEventsAsync([Received(aggregateId, 1)]);
        await UpdateStoredEventAsync(aggregateId, 1, Builders<BsonDocument>.Update
                                                                           .Unset(nameof(EvolvingStockReceivedEvent.BinNumber))
                                                                           .Unset(nameof(EvolvingStockReceivedEvent.Quantity))
                                                                           .Unset(nameof(EvolvingStockReceivedEvent.Warehouse)));

        var events = await ReadEventsAsync(aggregateId);
        var replayed = await _aggregateRepository.GetAtVersionAsync(aggregateId, 1);

        var received = events.Should().ContainSingle().Which.Should().BeOfType<EvolvingStockReceivedEvent>().Subject;
        received.BinNumber.Should().Be(42);
        received.Quantity.Should().Be(0);
        received.Warehouse.Should().Be(EvolvingStockReceivedEvent.DefaultWarehouse);
        replayed.Should().NotBeNull();
        replayed.BinNumber.Should().Be(42);
        replayed.Quantity.Should().Be(0);
        replayed.Warehouse.Should().Be(EvolvingStockReceivedEvent.DefaultWarehouse);
    }

    [Test]
    public async Task GetEventStreamAsync_StoredDiscriminatorNoLongerRegistered_Throws()
    {
        var aggregateId = Guid.NewGuid();
        await _eventStore.AppendEventsAsync([Received(aggregateId, 1)]);
        await UpdateStoredEventAsync(aggregateId, 1, Builders<BsonDocument>.Update.Set("_t", "StockArrived"));

        var act = () => ReadEventsAsync(aggregateId);

        await act.Should().ThrowAsync<BsonSerializationException>().WithMessage("Unknown discriminator value 'StockArrived'.");
    }

    [Test]
    public async Task GetEventStreamAsync_StoredElementOfChangedType_Throws()
    {
        var aggregateId = Guid.NewGuid();
        await _eventStore.AppendEventsAsync([Received(aggregateId, 1)]);
        await UpdateStoredEventAsync(aggregateId, 1, Builders<BsonDocument>.Update.Set(nameof(EvolvingStockReceivedEvent.Quantity), "three"));

        var readStream = () => ReadEventsAsync(aggregateId);
        var replay = () => _aggregateRepository.GetAtVersionAsync(aggregateId, 1);

        await readStream.Should().ThrowAsync<FormatException>().WithMessage($"*{nameof(EvolvingStockReceivedEvent.Quantity)}*");
        await replay.Should().ThrowAsync<FormatException>().WithMessage($"*{nameof(EvolvingStockReceivedEvent.Quantity)}*");
    }

    [Test]
    public async Task GetEventStreamAsync_StoredEventLacksRequiredElement_Throws()
    {
        var aggregateId = Guid.NewGuid();
        await _eventStore.AppendEventsAsync([
            Received(aggregateId, 1),
            new EvolvingStockAuditedEvent
            {
                AggregateId = aggregateId,
                Version = 2,
                Auditor = "auditor"
            }
        ]);
        await UpdateStoredEventAsync(aggregateId, 2, Builders<BsonDocument>.Update.Unset(nameof(EvolvingStockAuditedEvent.Auditor)));

        var act = () => ReadEventsAsync(aggregateId);

        await act.Should().ThrowAsync<FormatException>().WithMessage($"*{nameof(EvolvingStockAuditedEvent.Auditor)}*");
    }

    [Test]
    public async Task GetAsync_StoredEventChangedAfterAppend_ReadModelIsUnaffected()
    {
        var aggregateId = Guid.NewGuid();
        await _eventStore.AppendEventsAsync([Received(aggregateId, 1)]);
        await UpdateStoredEventAsync(aggregateId, 1, Builders<BsonDocument>.Update.Set(nameof(EvolvingStockReceivedEvent.Quantity), "three"));

        var readModel = await _aggregateRepository.GetAsync(aggregateId);

        readModel.Should().NotBeNull();
        readModel.Quantity.Should().Be(3);
    }

    private static EvolvingStockReceivedEvent Received(Guid aggregateId, Int64 version)
        => new()
        {
            AggregateId = aggregateId,
            Version = version,
            Quantity = 3,
            Warehouse = "north"
        };

    private async Task<List<Event<EvolvingStockAggregate>>> ReadEventsAsync(Guid aggregateId)
    {
        var events = new List<Event<EvolvingStockAggregate>>();
        await foreach (var @event in _eventStore.GetEventStreamAsync(aggregateId))
        {
            events.Add(@event);
        }

        return events;
    }

    private async Task UpdateStoredEventAsync(Guid aggregateId, Int64 version, UpdateDefinition<BsonDocument> update)
    {
        var result = await _mongoHelper.Database
                                       .GetCollection<BsonDocument>(_options.EventsCollectionName)
                                       .UpdateOneAsync(
                                           Builders<BsonDocument>.Filter.Eq(nameof(Event<EvolvingStockAggregate>.AggregateId),
                                                                            new BsonBinaryData(aggregateId, GuidRepresentation.Standard)) &
                                           Builders<BsonDocument>.Filter.Eq(nameof(Event<EvolvingStockAggregate>.Version), version),
                                           update);
        result.ModifiedCount.Should().Be(1);
    }
}

public sealed class EvolvingStockAggregate : Aggregate
{
    public Int32 BinNumber { get; set; }
    public Int32 Quantity { get; set; }
    public String? Warehouse { get; set; }
}

public sealed class EvolvingStockReceivedEvent : Event<EvolvingStockAggregate>
{
    public const String DefaultWarehouse = "main";

    [BsonDefaultValue(42)]
    public Int32 BinNumber { get; set; } = 7;

    public Int32 Quantity { get; set; }
    public String Warehouse { get; set; } = DefaultWarehouse;

    public override void Execute(EvolvingStockAggregate aggregate)
    {
        aggregate.BinNumber = BinNumber;
        aggregate.Quantity += Quantity;
        aggregate.Warehouse = Warehouse;
    }
}

public sealed class EvolvingStockAuditedEvent : Event<EvolvingStockAggregate>
{
    [BsonRequired]
    public String Auditor { get; set; } = String.Empty;

    public override void Execute(EvolvingStockAggregate aggregate) { }
}
