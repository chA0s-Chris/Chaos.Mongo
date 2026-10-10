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
/// Covers streams written before revisions existed: their documents lack every <c>Revision</c> element.
/// </summary>
[TestFixture(false)]
[TestFixture(true)]
public class LegacyRevisionIntegrationTests
{
    private const String RevisionElement = nameof(Event<OrderAggregate>.Revision);

    private readonly Boolean _bulkWrite;
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
                               options.DefaultDatabase = $"LegacyRevisionTestDb_{Guid.NewGuid():N}";
                               options.RunConfiguratorsOnStartup = false;
                           })
                           .WithEventStore<OrderAggregate>(es =>
                           {
                               es.WithEvent<OrderCreatedEvent>("OrderCreated")
                                 .WithEvent<OrderShippedEvent>("OrderShipped")
                                 .WithEvent<OrderCompletedEvent>("OrderCompleted")
                                 .WithEvent<OrderViewedEvent>("OrderViewed")
                                 .WithCollectionPrefix("Orders")
                                 .WithCheckpoints(2);

                               if (_bulkWrite)
                               {
                                   es.WithBulkWriteOptimization();
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

    public LegacyRevisionIntegrationTests(Boolean bulkWrite)
    {
        _bulkWrite = bulkWrite;
    }

    [Test]
    public async Task AppendEventsAsync_ObservationalEventOnLegacyStream_RecordsLegacyRevisionAndRewritesNothing()
    {
        var aggregateId = await CreateLegacyStreamAsync();

        var aggregate = await _eventStore.AppendEventsAsync(
        [
            new OrderViewedEvent
            {
                AggregateId = aggregateId,
                Version = 4
            }
        ]);

        aggregate.Version.Should().Be(3);
        aggregate.Revision.Should().Be(3);
        var events = await ReadRawEventsAsync(aggregateId);
        events[3][RevisionElement].AsInt64.Should().Be(3);
        events.Take(3).Should().OnlyContain(e => !e.Contains(RevisionElement));
        (await ReadRawReadModelAsync(aggregateId)).Contains(RevisionElement).Should().BeFalse();
    }

    [Test]
    public async Task AppendEventsAsync_StateChangeOnLegacyStream_ContinuesRevisionWithoutRewritingEvents()
    {
        var aggregateId = await CreateLegacyStreamAsync();

        var aggregate = await _eventStore.AppendEventsAsync(
        [
            new OrderShippedEvent
            {
                AggregateId = aggregateId,
                Version = 4
            }
        ]);

        aggregate.Version.Should().Be(4);
        aggregate.Revision.Should().Be(4);
        var events = await ReadRawEventsAsync(aggregateId);
        events[3][RevisionElement].AsInt64.Should().Be(4);
        events.Take(3).Should().OnlyContain(e => !e.Contains(RevisionElement));
        (await ReadRawReadModelAsync(aggregateId))[RevisionElement].AsInt64.Should().Be(4);
        var checkpoints = await ReadRawCheckpointsAsync(aggregateId);
        checkpoints.Should().HaveCount(2);
        checkpoints[0].Contains(RevisionElement).Should().BeFalse();
        checkpoints[1][RevisionElement].AsInt64.Should().Be(4);
    }

    [Test]
    public async Task GetAsync_LegacyReadModel_ReturnsRevisionEqualToVersion()
    {
        var aggregateId = await CreateLegacyStreamAsync();

        var aggregate = await _aggregateRepository.GetAsync(aggregateId);

        aggregate.Should().NotBeNull();
        aggregate.Version.Should().Be(3);
        aggregate.Revision.Should().Be(3);
    }

    [Test]
    public async Task GetAtVersionAsync_LegacyCheckpointAndEvents_ReturnsRevisionEqualToVersion()
    {
        var aggregateId = await CreateLegacyStreamAsync();

        var atCheckpoint = await _aggregateRepository.GetAtVersionAsync(aggregateId, 2);
        var afterCheckpoint = await _aggregateRepository.GetAtVersionAsync(aggregateId, 3);

        atCheckpoint.Should().NotBeNull();
        atCheckpoint.Version.Should().Be(2);
        atCheckpoint.Revision.Should().Be(2);
        afterCheckpoint.Should().NotBeNull();
        afterCheckpoint.Version.Should().Be(3);
        afterCheckpoint.Revision.Should().Be(3);
        afterCheckpoint.Status.Should().Be("Completed");
    }

    [Test]
    public async Task GetAtVersionAsync_MixedLegacyAndNewEvents_MatchesReadModel()
    {
        var aggregateId = await CreateLegacyStreamAsync();
        await _eventStore.AppendEventsAsync(
        [
            new OrderViewedEvent
            {
                AggregateId = aggregateId,
                Version = 4
            },
            new OrderShippedEvent
            {
                AggregateId = aggregateId,
                Version = 5
            }
        ]);

        var reconstructed = await _aggregateRepository.GetAtVersionAsync(aggregateId, 5);

        var readModel = await _aggregateRepository.GetAsync(aggregateId);
        readModel.Should().NotBeNull();
        reconstructed.Should().NotBeNull();
        reconstructed.Version.Should().Be(readModel.Version).And.Be(5);
        reconstructed.Revision.Should().Be(readModel.Revision).And.Be(4);
    }

    [Test]
    public async Task GetEventStream_LegacyEvents_ReturnRevisionEqualToVersion()
    {
        var aggregateId = await CreateLegacyStreamAsync();

        var events = new List<Event<OrderAggregate>>();
        await foreach (var @event in _eventStore.GetEventStream(aggregateId))
        {
            events.Add(@event);
        }

        events.Select(e => e.Revision).Should().Equal(1, 2, 3);
    }

    private static FilterDefinition<BsonDocument> ById(String element, Guid aggregateId)
        => Builders<BsonDocument>.Filter.Eq(element, new BsonBinaryData(aggregateId, GuidRepresentation.Standard));

    /// <summary>
    /// Appends three events with a checkpoint at version 2, then removes every <c>Revision</c> element so the
    /// stream looks as if it had been written before revisions existed.
    /// </summary>
    private async Task<Guid> CreateLegacyStreamAsync()
    {
        var aggregateId = Guid.NewGuid();
        await _eventStore.AppendEventsAsync(
        [
            new OrderCreatedEvent
            {
                AggregateId = aggregateId,
                Version = 1,
                CustomerName = "Legacy",
                TotalAmount = 10.00m
            }
        ]);
        await _eventStore.AppendEventsAsync(
        [
            new OrderShippedEvent
            {
                AggregateId = aggregateId,
                Version = 2
            }
        ]);
        await _eventStore.AppendEventsAsync(
        [
            new OrderCompletedEvent
            {
                AggregateId = aggregateId,
                Version = 3
            }
        ]);

        var database = _mongoHelper.Database;
        await database.GetCollection<BsonDocument>(_options.EventsCollectionName)
                      .UpdateManyAsync(ById(nameof(Event<OrderAggregate>.AggregateId), aggregateId),
                                       Builders<BsonDocument>.Update.Unset(RevisionElement));
        await database.GetCollection<BsonDocument>(_options.ReadModelCollectionName)
                      .UpdateOneAsync(ById("_id", aggregateId), Builders<BsonDocument>.Update.Unset(RevisionElement));
        await database.GetCollection<BsonDocument>(_options.CheckpointCollectionName)
                      .UpdateManyAsync(ById("_id.AggregateId", aggregateId),
                                       Builders<BsonDocument>.Update.Unset(RevisionElement).Unset($"State.{RevisionElement}"));

        return aggregateId;
    }

    private Task<List<BsonDocument>> ReadRawCheckpointsAsync(Guid aggregateId)
        => _mongoHelper.Database
                       .GetCollection<BsonDocument>(_options.CheckpointCollectionName)
                       .Find(ById("_id.AggregateId", aggregateId))
                       .Sort(Builders<BsonDocument>.Sort.Ascending("_id.Version"))
                       .ToListAsync();

    private Task<List<BsonDocument>> ReadRawEventsAsync(Guid aggregateId)
        => _mongoHelper.Database
                       .GetCollection<BsonDocument>(_options.EventsCollectionName)
                       .Find(ById(nameof(Event<OrderAggregate>.AggregateId), aggregateId))
                       .Sort(Builders<BsonDocument>.Sort.Ascending(nameof(Event<OrderAggregate>.Version)))
                       .ToListAsync();

    private Task<BsonDocument> ReadRawReadModelAsync(Guid aggregateId)
        => _mongoHelper.Database
                       .GetCollection<BsonDocument>(_options.ReadModelCollectionName)
                       .Find(ById("_id", aggregateId))
                       .SingleAsync();
}
