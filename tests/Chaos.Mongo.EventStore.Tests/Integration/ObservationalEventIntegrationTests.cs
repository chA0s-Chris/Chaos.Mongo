// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Chaos.Mongo.Configuration;
using Chaos.Mongo.EventStore.Errors;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using NUnit.Framework;
using Testcontainers.MongoDb;

[TestFixture(false)]
[TestFixture(true)]
public class ObservationalEventIntegrationTests
{
    private readonly Boolean _bulkWrite;
    private IAggregateRepository<OrderAggregate> _aggregateRepository;
    private MongoDbContainer _container;
    private MongoEventStore<OrderAggregate> _eventStore;
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
                               options.DefaultDatabase = $"ObservationalTestDb_{Guid.NewGuid():N}";
                               options.RunConfiguratorsOnStartup = false;
                           })
                           .WithEventStore<OrderAggregate>(es =>
                           {
                               es.WithEvent<OrderCreatedEvent>("OrderCreated")
                                 .WithEvent<OrderShippedEvent>("OrderShipped")
                                 .WithEvent<OrderCancelledEvent>("OrderCancelled")
                                 .WithEvent<OrderViewedEvent>("OrderViewed")
                                 .WithEvent<OrderPrintedEvent>("OrderPrinted")
                                 .WithCollectionPrefix("Orders")
                                 .WithCheckpoints(1);

                               if (_bulkWrite)
                               {
                                   es.WithBulkWriteOptimization();
                               }
                           })
                           .Services
                           .BuildServiceProvider();

        _eventStore = (MongoEventStore<OrderAggregate>)_serviceProvider.GetRequiredService<IEventStore<OrderAggregate>>();
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

    public ObservationalEventIntegrationTests(Boolean bulkWrite)
    {
        _bulkWrite = bulkWrite;
    }

    [Test]
    public async Task AppendEventsAsync_CallerSuppliedRevision_IsOverwritten()
    {
        var aggregateId = Guid.NewGuid();
        var created = Created(aggregateId, 1);
        created.Revision = 42;
        var viewed = Viewed(aggregateId, 2);
        viewed.Revision = 42;

        await _eventStore.AppendEventsAsync([created, viewed]);

        created.Revision.Should().Be(1);
        viewed.Revision.Should().Be(1);
        var stored = await ReadRawEventsAsync(aggregateId);
        stored.Select(e => e[nameof(Event<OrderAggregate>.Revision)].AsInt64).Should().Equal(1, 1);
    }

    [Test]
    public async Task AppendEventsAsync_IdempotentRetryOfObservationalEvent_ThrowsMongoDuplicateEventException()
    {
        var aggregateId = await CreateOrderAsync();
        var eventId = Guid.NewGuid();
        await _eventStore.AppendEventsAsync([Viewed(aggregateId, 2, eventId)]);

        var act = () => _eventStore.AppendEventsAsync([Viewed(aggregateId, 2, eventId)]);

        await act.Should().ThrowAsync<MongoDuplicateEventException>();
        (await ReadRawEventsAsync(aggregateId)).Should().HaveCount(2);
    }

    [Test]
    public async Task AppendEventsAsync_MixedBatchEndingWithObservationalEvent_KeepsVersionAtLastStateChange()
    {
        var aggregateId = Guid.NewGuid();

        var aggregate = await _eventStore.AppendEventsAsync(
        [
            Created(aggregateId, 1),
            Viewed(aggregateId, 2),
            Shipped(aggregateId, 3),
            Viewed(aggregateId, 4)
        ]);

        aggregate.Version.Should().Be(3);
        aggregate.Revision.Should().Be(2);
        aggregate.Status.Should().Be("Shipped");

        var readModel = await _aggregateRepository.GetAsync(aggregateId);
        readModel.Should().NotBeNull();
        readModel.Version.Should().Be(3);
        readModel.Revision.Should().Be(2);
        readModel.Status.Should().Be("Shipped");

        var events = await ReadEventStreamAsync(aggregateId);
        events.Select(e => e.Version).Should().Equal(1, 2, 3, 4);
        events.Select(e => e.Revision).Should().Equal(1, 1, 2, 2);
        events[1].Should().BeOfType<OrderViewedEvent>();
    }

    [Test]
    public async Task AppendEventsAsync_MissingReadModel_ThrowsMongoEventStoreExceptionAndPersistsNothing(
        [Values] Boolean observational)
    {
        var aggregateId = await CreateOrderAsync();
        await _mongoHelper.Database
                          .GetCollection<OrderAggregate>(_options.ReadModelCollectionName)
                          .DeleteOneAsync(a => a.Id == aggregateId);

        Event<OrderAggregate> next = observational ? Viewed(aggregateId, 2) : Shipped(aggregateId, 2);
        var act = () => _eventStore.AppendEventsAsync([next]);

        var exception = await act.Should().ThrowAsync<MongoEventStoreException>().WithMessage("*version 1*read model is missing*");
        exception.Which.Should().BeOfType<MongoEventStoreException>();
        (await ReadRawEventsAsync(aggregateId)).Should().HaveCount(1);
    }

    [Test]
    public async Task AppendEventsAsync_ObservationalAfterStateChangeInSameBatch_ObservesBatchRevision()
    {
        var aggregateId = Guid.NewGuid();

        var aggregate = await _eventStore.AppendEventsAsync([Created(aggregateId, 1), Viewed(aggregateId, 2)]);

        aggregate.Version.Should().Be(1);
        aggregate.Revision.Should().Be(1);
        (await ReadEventStreamAsync(aggregateId)).Select(e => e.Revision).Should().Equal(1, 1);
    }

    [Test]
    public async Task AppendEventsAsync_ObservationalBeforeFirstStateChangeInBatch_ThrowsAndPersistsNothing()
    {
        var aggregateId = Guid.NewGuid();

        var act = () => _eventStore.AppendEventsAsync([Viewed(aggregateId, 1), Created(aggregateId, 2)]);

        await act.Should().ThrowAsync<MongoEventValidationException>().WithMessage("*version 1*preceding state-changing event*");
        (await ReadRawEventsAsync(aggregateId)).Should().BeEmpty();
        (await _aggregateRepository.GetAsync(aggregateId)).Should().BeNull();
    }

    [Test]
    public async Task AppendEventsAsync_ObservationalEventOnEmptyStream_ThrowsAndPersistsNothing()
    {
        var aggregateId = Guid.NewGuid();

        var act = () => _eventStore.AppendEventsAsync([Viewed(aggregateId, 1)]);

        await act.Should().ThrowAsync<MongoEventValidationException>();
        (await ReadRawEventsAsync(aggregateId)).Should().BeEmpty();
        (await _aggregateRepository.GetAsync(aggregateId)).Should().BeNull();
    }

    [Test]
    public async Task AppendEventsAsync_ObservationalEventWithDefaultValidation_IsAppended()
    {
        var aggregateId = await CreateOrderAsync();

        await _eventStore.AppendEventsAsync(
        [
            new OrderPrintedEvent
            {
                AggregateId = aggregateId,
                Version = 2
            }
        ]);

        var events = await ReadEventStreamAsync(aggregateId);
        events[1].Should().BeOfType<OrderPrintedEvent>().Which.Revision.Should().Be(1);
    }

    [Test]
    public async Task AppendEventsAsync_ObservationalEvent_InsertsOnlyTheEvent()
    {
        var aggregateId = await CreateOrderAsync();
        var readModelBefore = await ReadRawReadModelAsync(aggregateId);

        var aggregate = await _eventStore.AppendEventsAsync([Viewed(aggregateId, 2)]);

        aggregate.Version.Should().Be(1);
        aggregate.Revision.Should().Be(1);
        aggregate.Status.Should().Be("Created");
        (await ReadRawReadModelAsync(aggregateId)).Equals(readModelBefore).Should().BeTrue();
        (await ReadRawCheckpointsAsync(aggregateId)).Select(c => c["_id"]["Version"].AsInt64).Should().Equal(1);

        var stored = await ReadRawEventsAsync(aggregateId);
        stored.Should().HaveCount(2);
        stored[1]["_t"].AsBsonArray.Should().Contain("OrderViewed");
        stored[1][nameof(Event<OrderAggregate>.Revision)].AsInt64.Should().Be(1);
        stored[1][nameof(OrderViewedEvent.ViewedBy)].AsString.Should().Be("auditor");
    }

    [Test]
    public async Task AppendEventsAsync_ObservationalValidationFails_ThrowsAndPersistsNothing()
    {
        var aggregateId = await CreateOrderAsync();
        await _eventStore.AppendEventsAsync(
        [
            new OrderCancelledEvent
            {
                AggregateId = aggregateId,
                Version = 2
            }
        ]);

        var act = () => _eventStore.AppendEventsAsync([Viewed(aggregateId, 3)]);

        await act.Should().ThrowAsync<MongoEventValidationException>().WithMessage("*cancelled order*");
        (await ReadRawEventsAsync(aggregateId)).Should().HaveCount(2);
    }

    [Test]
    public async Task AppendEventsAsync_PositionGapAboveStreamHead_ThrowsArgumentException()
    {
        var aggregateId = await CreateOrderAsync();
        await _eventStore.AppendEventsAsync([Viewed(aggregateId, 2)]);

        var act = () => _eventStore.AppendEventsAsync([Shipped(aggregateId, 4)]);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*sequential*Expected version 3*");
    }

    [Test]
    public async Task AppendEventsAsync_PositionTakenByObservationalEvent_ThrowsMongoConcurrencyException()
    {
        var aggregateId = await CreateOrderAsync();
        await _eventStore.AppendEventsAsync([Viewed(aggregateId, 2)]);

        var act = () => _eventStore.AppendEventsAsync([Shipped(aggregateId, 2)]);

        await act.Should().ThrowAsync<MongoConcurrencyException>().WithMessage("*version 2*already committed*stream is at version 2*");
    }

    [Test]
    public async Task AppendEventsAsync_StateChangeAfterObservationalEvents_MovesVersionToOwnPosition()
    {
        var aggregateId = await CreateOrderAsync();
        await _eventStore.AppendEventsAsync([Viewed(aggregateId, 2)]);
        await _eventStore.AppendEventsAsync([Viewed(aggregateId, 3)]);

        var aggregate = await _eventStore.AppendEventsAsync([Shipped(aggregateId, 4)]);

        aggregate.Version.Should().Be(4);
        aggregate.Revision.Should().Be(2);
        var readModel = await _aggregateRepository.GetAsync(aggregateId);
        readModel.Should().NotBeNull();
        readModel.Version.Should().Be(4);
        readModel.Revision.Should().Be(2);
        (await ReadRawEventsAsync(aggregateId)).Select(e => e[nameof(Event<OrderAggregate>.Revision)].AsInt64)
                                               .Should().Equal(1, 1, 1, 2);
    }

    [Test]
    public async Task AppendEventsAsync_StateChangeCommittedBetweenHeadAndReadModelReads_ThrowsAndKeepsCommittedState(
        [Values] Boolean observational)
    {
        var aggregateId = await CreateOrderAsync();
        var competitor = new MongoEventStore<OrderAggregate>(_mongoHelper, _options);
        _eventStore.AfterStreamHeadRead = async ct =>
        {
            _eventStore.AfterStreamHeadRead = null;
            await competitor.AppendEventsAsync([Shipped(aggregateId, 2)], cancellationToken: ct);
        };

        Event<OrderAggregate> next = observational
            ? Viewed(aggregateId, 2)
            : new OrderCancelledEvent
            {
                AggregateId = aggregateId,
                Version = 2
            };
        var act = () => _eventStore.AppendEventsAsync([next]);

        await act.Should().ThrowAsync<MongoConcurrencyException>().WithMessage("*revision 2*stream was read at revision 1*");
        var events = await ReadEventStreamAsync(aggregateId);
        events.Should().HaveCount(2);
        events[1].Should().BeOfType<OrderShippedEvent>();
        var readModel = await _aggregateRepository.GetAsync(aggregateId);
        readModel.Should().NotBeNull();
        readModel.Status.Should().Be("Shipped");
        readModel.Version.Should().Be(2);
        readModel.Revision.Should().Be(2);
    }

    [Test]
    public async Task AppendEventsAsync_StateChangingEvents_AssignRevisionPerEvent()
    {
        var aggregateId = Guid.NewGuid();

        var aggregate = await _eventStore.AppendEventsAsync([Created(aggregateId, 1), Shipped(aggregateId, 2)]);

        aggregate.Version.Should().Be(2);
        aggregate.Revision.Should().Be(2);
        (await ReadRawReadModelAsync(aggregateId))[nameof(IAggregate.Revision)].AsInt64.Should().Be(2);
        (await ReadRawEventsAsync(aggregateId)).Select(e => e[nameof(Event<OrderAggregate>.Revision)].AsInt64)
                                               .Should().Equal(1, 2);
    }

    [Test]
    public async Task GetAtVersionAsync_InterleavedObservationalEvents_MatchesReadModel()
    {
        var aggregateId = Guid.NewGuid();
        await _eventStore.AppendEventsAsync([Created(aggregateId, 1), Viewed(aggregateId, 2)]);
        await _eventStore.AppendEventsAsync([Shipped(aggregateId, 3), Viewed(aggregateId, 4)]);

        var current = await _aggregateRepository.GetAtVersionAsync(aggregateId, 4);
        var atObservation = await _aggregateRepository.GetAtVersionAsync(aggregateId, 2);

        var readModel = await _aggregateRepository.GetAsync(aggregateId);
        readModel.Should().NotBeNull();
        current.Should().NotBeNull();
        current.Version.Should().Be(readModel.Version).And.Be(3);
        current.Revision.Should().Be(readModel.Revision).And.Be(2);
        current.Status.Should().Be("Shipped");
        atObservation.Should().NotBeNull();
        atObservation.Version.Should().Be(1);
        atObservation.Revision.Should().Be(1);
        atObservation.Status.Should().Be("Created");
    }

    private static OrderCreatedEvent Created(Guid aggregateId, Int64 version)
        => new()
        {
            AggregateId = aggregateId,
            Version = version,
            CustomerName = "Observed",
            TotalAmount = 10.00m
        };

    private static OrderShippedEvent Shipped(Guid aggregateId, Int64 version)
        => new()
        {
            AggregateId = aggregateId,
            Version = version
        };

    private static OrderViewedEvent Viewed(Guid aggregateId, Int64 version, Guid? id = null)
        => new()
        {
            Id = id ?? Guid.Empty,
            AggregateId = aggregateId,
            Version = version,
            ViewedBy = "auditor"
        };

    private async Task<Guid> CreateOrderAsync()
    {
        var aggregateId = Guid.NewGuid();
        await _eventStore.AppendEventsAsync([Created(aggregateId, 1)]);
        return aggregateId;
    }

    private async Task<List<Event<OrderAggregate>>> ReadEventStreamAsync(Guid aggregateId)
    {
        var events = new List<Event<OrderAggregate>>();
        await foreach (var @event in _eventStore.GetEventStream(aggregateId))
        {
            events.Add(@event);
        }

        return events;
    }

    private Task<List<BsonDocument>> ReadRawCheckpointsAsync(Guid aggregateId)
        => _mongoHelper.Database
                       .GetCollection<BsonDocument>(_options.CheckpointCollectionName)
                       .Find(Builders<BsonDocument>.Filter.Eq("_id.AggregateId", new BsonBinaryData(aggregateId, GuidRepresentation.Standard)))
                       .Sort(Builders<BsonDocument>.Sort.Ascending("_id.Version"))
                       .ToListAsync();

    private Task<List<BsonDocument>> ReadRawEventsAsync(Guid aggregateId)
        => _mongoHelper.Database
                       .GetCollection<BsonDocument>(_options.EventsCollectionName)
                       .Find(Builders<BsonDocument>.Filter.Eq(nameof(Event<OrderAggregate>.AggregateId),
                                                              new BsonBinaryData(aggregateId, GuidRepresentation.Standard)))
                       .Sort(Builders<BsonDocument>.Sort.Ascending(nameof(Event<OrderAggregate>.Version)))
                       .ToListAsync();

    private Task<BsonDocument> ReadRawReadModelAsync(Guid aggregateId)
        => _mongoHelper.Database
                       .GetCollection<BsonDocument>(_options.ReadModelCollectionName)
                       .Find(Builders<BsonDocument>.Filter.Eq("_id", new BsonBinaryData(aggregateId, GuidRepresentation.Standard)))
                       .SingleAsync();
}
