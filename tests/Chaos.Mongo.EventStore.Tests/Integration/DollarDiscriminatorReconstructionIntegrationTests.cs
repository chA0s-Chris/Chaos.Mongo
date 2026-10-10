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

[TestFixture(false)]
[TestFixture(true)]
public class DollarDiscriminatorReconstructionIntegrationTests
{
    private readonly Boolean _variableDiscriminator;
    private IAggregateRepository<DollarDiscriminatorAggregate> _aggregateRepository;
    private MongoDbContainer _container;
    private IEventStore<DollarDiscriminatorAggregate> _eventStore;
    private IMongoHelper _mongoHelper;
    private MongoEventStoreOptions<DollarDiscriminatorAggregate> _options;
    private ServiceProvider _serviceProvider;

    [OneTimeSetUp]
    public async Task GetMongoDbContainer() => _container = await MongoDbTestContainer.StartContainerAsync();

    [SetUp]
    public async Task Setup()
    {
        _serviceProvider = new ServiceCollection()
                           .AddMongo(MongoUrl.Create(_container.GetConnectionString()), configure: options =>
                           {
                               options.DefaultDatabase = $"DollarDiscriminatorTestDb_{Guid.NewGuid():N}";
                               options.RunConfiguratorsOnStartup = false;
                           })
                           .WithEventStore<DollarDiscriminatorAggregate>(builder =>
                           {
                               builder.WithEvent<DollarDiscriminatorIncrementedEvent>("DollarDiscriminatorIncremented")
                                      .WithCollectionPrefix("DollarDiscriminator");

                               if (_variableDiscriminator)
                               {
                                   builder.WithEvent<DollarVariableObservedEvent>("$$UnboundReviewVariable");
                               }
                               else
                               {
                                   builder.WithEvent<DollarFieldObservedEvent>("$Viewed");
                               }
                           })
                           .Services
                           .BuildServiceProvider();

        _eventStore = _serviceProvider.GetRequiredService<IEventStore<DollarDiscriminatorAggregate>>();
        _aggregateRepository = _serviceProvider.GetRequiredService<IAggregateRepository<DollarDiscriminatorAggregate>>();
        _mongoHelper = _serviceProvider.GetRequiredService<IMongoHelper>();
        _options = _serviceProvider.GetRequiredService<MongoEventStoreOptions<DollarDiscriminatorAggregate>>();
        foreach (var configurator in _serviceProvider.GetServices<IMongoConfigurator>())
        {
            await configurator.ConfigureAsync(_mongoHelper);
        }
    }

    [TearDown]
    public async Task TearDown() => await _serviceProvider.DisposeAsync();

    public DollarDiscriminatorReconstructionIntegrationTests(Boolean variableDiscriminator)
    {
        _variableDiscriminator = variableDiscriminator;
    }

    [Test]
    public async Task GetAtRevisionAsync_DollarPrefixedObservationalDiscriminator_ExcludesUnreadableObservation(
        [Values] Boolean scalarDiscriminator)
    {
        var aggregateId = await AppendUnreadableObservationAsync(scalarDiscriminator);

        var aggregate = await _aggregateRepository.GetAtRevisionAsync(aggregateId, 2);

        aggregate.Should().NotBeNull();
        aggregate.Count.Should().Be(2);
        aggregate.Version.Should().Be(3);
        aggregate.Revision.Should().Be(2);
    }

    [Test]
    public async Task GetAtVersionAsync_DollarPrefixedObservationalDiscriminator_ExcludesUnreadableObservation(
        [Values] Boolean scalarDiscriminator)
    {
        var aggregateId = await AppendUnreadableObservationAsync(scalarDiscriminator);

        var aggregate = await _aggregateRepository.GetAtVersionAsync(aggregateId, 3);

        aggregate.Should().NotBeNull();
        aggregate.Count.Should().Be(2);
        aggregate.Version.Should().Be(3);
        aggregate.Revision.Should().Be(2);
    }

    private async Task<Guid> AppendUnreadableObservationAsync(Boolean scalarDiscriminator)
    {
        var aggregateId = Guid.NewGuid();
        Event<DollarDiscriminatorAggregate> observation = _variableDiscriminator
            ? new DollarVariableObservedEvent
            {
                AggregateId = aggregateId,
                Version = 2,
                ObservedBy = "auditor"
            }
            : new DollarFieldObservedEvent
            {
                AggregateId = aggregateId,
                Version = 2,
                ObservedBy = "auditor"
            };
        await _eventStore.AppendEventsAsync(
        [
            new DollarDiscriminatorIncrementedEvent
            {
                AggregateId = aggregateId,
                Version = 1
            },
            observation,
            new DollarDiscriminatorIncrementedEvent
            {
                AggregateId = aggregateId,
                Version = 3
            }
        ]);

        var collection = _mongoHelper.Database.GetCollection<BsonDocument>(_options.EventsCollectionName);
        var filter = Builders<BsonDocument>.Filter.Eq(nameof(Event<DollarDiscriminatorAggregate>.AggregateId),
                                                      new BsonBinaryData(aggregateId, GuidRepresentation.Standard)) &
                     Builders<BsonDocument>.Filter.Eq(nameof(Event<DollarDiscriminatorAggregate>.Version), 2L);
        var document = await collection.Find(filter).SingleAsync();
        var discriminator = _variableDiscriminator ? "$$UnboundReviewVariable" : "$Viewed";
        document["_t"].AsBsonArray.Last().AsString.Should().Be(discriminator);

        // An unreadable payload proves that replay excludes the document before typed deserialization.
        var update = Builders<BsonDocument>.Update.Set(nameof(DollarFieldObservedEvent.ObservedBy), 42);
        if (scalarDiscriminator)
        {
            update = update.Set("_t", discriminator);
        }

        (await collection.UpdateOneAsync(filter, update)).ModifiedCount.Should().Be(1);
        document = await collection.Find(filter).SingleAsync();
        document["_t"].IsString.Should().Be(scalarDiscriminator);
        return aggregateId;
    }
}

public class DollarDiscriminatorAggregate : Aggregate
{
    public Int32 Count { get; set; }
}

public class DollarDiscriminatorIncrementedEvent : Event<DollarDiscriminatorAggregate>
{
    public override void Execute(DollarDiscriminatorAggregate aggregate) => aggregate.Count++;
}

public class DollarFieldObservedEvent : ObservationalEvent<DollarDiscriminatorAggregate>
{
    public String ObservedBy { get; set; } = String.Empty;
}

public class DollarVariableObservedEvent : ObservationalEvent<DollarDiscriminatorAggregate>
{
    public String ObservedBy { get; set; } = String.Empty;
}
