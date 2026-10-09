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
/// Covers <see cref="IAggregateRepository{TAggregate}.GetAtRevisionAsync"/> and
/// <see cref="IAggregateRepository{TAggregate}.GetAtVersionAsync"/> with and without checkpoints.
/// </summary>
[TestFixture(0)]
[TestFixture(2)]
public class ReconstructionIntegrationTests
{
    private readonly Int32 _checkpointInterval;
    private IAggregateRepository<OrderAggregate> _aggregateRepository;
    private MongoDbContainer _container;
    private IEventStore<OrderAggregate> _eventStore;
    private IMongoHelper _mongoHelper;
    private MongoEventStoreOptions<OrderAggregate> _options;
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
                               options.DefaultDatabase = $"ReconstructionTestDb_{Guid.NewGuid():N}";
                               options.RunConfiguratorsOnStartup = false;
                           })
                           .WithEventStore<OrderAggregate>(es =>
                           {
                               es.WithEvent<OrderCreatedEvent>("OrderCreated")
                                 .WithEvent<OrderShippedEvent>("OrderShipped")
                                 .WithEvent<OrderCompletedEvent>("OrderCompleted")
                                 .WithEvent<OrderViewedEvent>("OrderViewed")
                                 .WithCollectionPrefix("Orders");

                               if (_checkpointInterval > 0)
                               {
                                   es.WithCheckpoints(_checkpointInterval);
                               }
                           })
                           .Services
                           .BuildServiceProvider();

        _eventStore = _serviceProvider.GetRequiredService<IEventStore<OrderAggregate>>();
        _aggregateRepository = _serviceProvider.GetRequiredService<IAggregateRepository<OrderAggregate>>();
        _mongoHelper = _serviceProvider.GetRequiredService<IMongoHelper>();
        _options = _serviceProvider.GetRequiredService<MongoEventStoreOptions<OrderAggregate>>();

        foreach (var configurator in _serviceProvider.GetServices<IMongoConfigurator>())
        {
            await configurator.ConfigureAsync(_mongoHelper);
        }
    }

    [TearDown]
    public async Task TearDown() => await _serviceProvider.DisposeAsync();

    public ReconstructionIntegrationTests(Int32 checkpointInterval)
    {
        _checkpointInterval = checkpointInterval;
    }

    [Test]
    public async Task GetAtRevisionAsync_InterleavedObservationalEvents_ReconstructsEachRevision()
    {
        var aggregateId = await CreateInterleavedStreamAsync();

        var atRevision1 = await _aggregateRepository.GetAtRevisionAsync(aggregateId, 1);
        var atRevision2 = await _aggregateRepository.GetAtRevisionAsync(aggregateId, 2);
        var atRevision3 = await _aggregateRepository.GetAtRevisionAsync(aggregateId, 3);
        var beyondHead = await _aggregateRepository.GetAtRevisionAsync(aggregateId, 4);

        AssertState(atRevision1, 1, 1, "Created");
        AssertState(atRevision2, 3, 2, "Shipped");
        AssertState(atRevision3, 5, 3, "Completed");
        AssertState(beyondHead, 5, 3, "Completed");
        (await _aggregateRepository.GetAtRevisionAsync(aggregateId, 0)).Should().BeNull();
    }

    [Test]
    public async Task GetAtRevisionAsync_MixedLegacyAndNewStream_ReconstructsEachRevision()
    {
        var aggregateId = Guid.NewGuid();
        await AppendAsync(Created(aggregateId, 1));
        await AppendAsync(Shipped(aggregateId, 2));
        await StripRevisionsAsync(aggregateId);
        await AppendAsync(Viewed(aggregateId, 3));
        await AppendAsync(Completed(aggregateId, 4));
        await AppendAsync(Viewed(aggregateId, 5));

        AssertState(await _aggregateRepository.GetAtRevisionAsync(aggregateId, 1), 1, 1, "Created");
        AssertState(await _aggregateRepository.GetAtRevisionAsync(aggregateId, 2), 2, 2, "Shipped");
        AssertState(await _aggregateRepository.GetAtRevisionAsync(aggregateId, 3), 4, 3, "Completed");
        AssertState(await _aggregateRepository.GetAtVersionAsync(aggregateId, 3), 2, 2, "Shipped");
        AssertState(await _aggregateRepository.GetAtVersionAsync(aggregateId, 5), 4, 3, "Completed");
    }

    [Test]
    public async Task GetAtRevisionAsync_UnknownAggregate_ReturnsNull()
        => (await _aggregateRepository.GetAtRevisionAsync(Guid.NewGuid(), 1)).Should().BeNull();

    [Test]
    public async Task GetAtVersionAsync_HistoricalPositions_ReturnLastStateChangeAtOrBelow()
    {
        var aggregateId = await CreateInterleavedStreamAsync();

        AssertState(await _aggregateRepository.GetAtVersionAsync(aggregateId, 2), 1, 1, "Created");
        AssertState(await _aggregateRepository.GetAtVersionAsync(aggregateId, 4), 3, 2, "Shipped");

        var readModel = await _aggregateRepository.GetAsync(aggregateId);
        readModel.Should().NotBeNull();
        AssertState(await _aggregateRepository.GetAtVersionAsync(aggregateId, 6), readModel.Version, readModel.Revision, readModel.Status);
    }

    [Test]
    public async Task GetAtVersionAsync_ObservationalEvents_AreExcludedOnTheServer([Values] Boolean scalarDiscriminator)
    {
        var aggregateId = Guid.NewGuid();
        await AppendAsync(Created(aggregateId, 1));
        await AppendAsync(Viewed(aggregateId, 2));

        // The stored observational event can no longer be deserialized, so replay only succeeds if the
        // server never returns it.
        var update = Builders<BsonDocument>.Update.Set(nameof(OrderViewedEvent.ViewedBy), 42);
        if (scalarDiscriminator)
        {
            update = update.Set("_t", "OrderViewed");
        }

        await Events.UpdateOneAsync(ByPosition(aggregateId, 2), update);
        await AppendAsync(Shipped(aggregateId, 3));

        AssertState(await _aggregateRepository.GetAtVersionAsync(aggregateId, 3), 3, 2, "Shipped");
        AssertState(await _aggregateRepository.GetAtRevisionAsync(aggregateId, 2), 3, 2, "Shipped");
    }

    [Test]
    public async Task GetAtVersionAsync_UnknownDescendantOfObservationalType_FailsDuringDeserialization()
    {
        var aggregateId = Guid.NewGuid();
        await AppendAsync(Created(aggregateId, 1));
        await AppendAsync(Viewed(aggregateId, 2));
        var stored = await Events.Find(ByPosition(aggregateId, 2)).SingleAsync();
        var discriminators = stored["_t"].AsBsonArray.DeepClone().AsBsonArray;
        discriminators.Add("OrderViewedByUnknownClient");
        await Events.UpdateOneAsync(ByPosition(aggregateId, 2), Builders<BsonDocument>.Update.Set("_t", discriminators));

        var act = () => _aggregateRepository.GetAtVersionAsync(aggregateId, 2);

        await act.Should().ThrowAsync<BsonSerializationException>().WithMessage("*OrderViewedByUnknownClient*");
    }

    [Test]
    public async Task Repository_CreatedBeforeFirstAppend_KeepsHierarchicalDiscriminators()
    {
        // The repository resolves the discriminator element name before any event of this fixture is stored.
        var aggregateId = Guid.NewGuid();
        await AppendAsync(Created(aggregateId, 1));
        await AppendAsync(Viewed(aggregateId, 2));

        var stored = await Events.Find(ByAggregate(nameof(Event<OrderAggregate>.AggregateId), aggregateId))
                                 .Sort(Builders<BsonDocument>.Sort.Ascending(nameof(Event<OrderAggregate>.Version)))
                                 .ToListAsync();

        stored.Select(e => e["_t"].AsBsonArray[^1].AsString).Should().Equal("OrderCreated", "OrderViewed");
    }

    private IMongoCollection<BsonDocument> Events
        => _mongoHelper.Database.GetCollection<BsonDocument>(_options.EventsCollectionName);

    private static void AssertState(OrderAggregate? aggregate, Int64 version, Int64 revision, String status)
    {
        aggregate.Should().NotBeNull();
        aggregate.Version.Should().Be(version);
        aggregate.Revision.Should().Be(revision);
        aggregate.Status.Should().Be(status);
    }

    private static FilterDefinition<BsonDocument> ByAggregate(String element, Guid aggregateId)
        => Builders<BsonDocument>.Filter.Eq(element, new BsonBinaryData(aggregateId, GuidRepresentation.Standard));

    private static FilterDefinition<BsonDocument> ByPosition(Guid aggregateId, Int64 version)
        => ByAggregate(nameof(Event<OrderAggregate>.AggregateId), aggregateId) &
           Builders<BsonDocument>.Filter.Eq(nameof(Event<OrderAggregate>.Version), version);

    private static OrderCompletedEvent Completed(Guid aggregateId, Int64 version)
        => new()
        {
            AggregateId = aggregateId,
            Version = version
        };

    private static OrderCreatedEvent Created(Guid aggregateId, Int64 version)
        => new()
        {
            AggregateId = aggregateId,
            Version = version,
            CustomerName = "Reconstructed",
            TotalAmount = 10.00m
        };

    private static OrderShippedEvent Shipped(Guid aggregateId, Int64 version)
        => new()
        {
            AggregateId = aggregateId,
            Version = version
        };

    private static OrderViewedEvent Viewed(Guid aggregateId, Int64 version)
        => new()
        {
            AggregateId = aggregateId,
            Version = version,
            ViewedBy = "auditor"
        };

    private Task AppendAsync(Event<OrderAggregate> @event) => _eventStore.AppendEventsAsync([@event]);

    /// <summary>
    /// Appends one event per append, so every checkpoint interval is crossed by its own append:
    /// created (1), viewed (2), shipped (3), viewed (4), completed (5), viewed (6).
    /// </summary>
    private async Task<Guid> CreateInterleavedStreamAsync()
    {
        var aggregateId = Guid.NewGuid();
        await AppendAsync(Created(aggregateId, 1));
        await AppendAsync(Viewed(aggregateId, 2));
        await AppendAsync(Shipped(aggregateId, 3));
        await AppendAsync(Viewed(aggregateId, 4));
        await AppendAsync(Completed(aggregateId, 5));
        await AppendAsync(Viewed(aggregateId, 6));
        return aggregateId;
    }

    /// <summary>
    /// Removes every <c>Revision</c> element of the stream, so it looks as if it had been written before
    /// revisions existed.
    /// </summary>
    private async Task StripRevisionsAsync(Guid aggregateId)
    {
        const String revision = nameof(Event<OrderAggregate>.Revision);
        var database = _mongoHelper.Database;
        await Events.UpdateManyAsync(ByAggregate(nameof(Event<OrderAggregate>.AggregateId), aggregateId),
                                     Builders<BsonDocument>.Update.Unset(revision));
        await database.GetCollection<BsonDocument>(_options.ReadModelCollectionName)
                      .UpdateOneAsync(ByAggregate("_id", aggregateId), Builders<BsonDocument>.Update.Unset(revision));
        await database.GetCollection<BsonDocument>(_options.CheckpointCollectionName)
                      .UpdateManyAsync(ByAggregate("_id.AggregateId", aggregateId),
                                       Builders<BsonDocument>.Update.Unset(revision).Unset($"State.{revision}"));
    }
}
