// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Chaos.Mongo.Configuration;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using NUnit.Framework;
using Testcontainers.MongoDb;

[TestFixture(false)]
[TestFixture(true)]
public class BatchMetadataIntegrationTests
{
    private readonly Boolean _bulkWrite;
    private IAggregateRepository<BatchMetadataAggregate> _aggregateRepository;
    private MongoDbContainer _container;
    private IEventStore<BatchMetadataAggregate> _eventStore;
    private ServiceProvider _serviceProvider;

    [OneTimeSetUp]
    public async Task GetMongoDbContainer() => _container = await MongoDbTestContainer.StartContainerAsync();

    [SetUp]
    public async Task Setup()
    {
        _serviceProvider = new ServiceCollection()
                           .AddMongo(MongoUrl.Create(_container.GetConnectionString()), configure: options =>
                           {
                               options.DefaultDatabase = $"BatchMetadataTestDb_{Guid.NewGuid():N}";
                               options.RunConfiguratorsOnStartup = false;
                           })
                           .WithEventStore<BatchMetadataAggregate>(builder =>
                           {
                               builder.WithEvent<BatchMetadataRecordedEvent>("BatchMetadataRecorded")
                                      .WithEvent<BatchMetadataOverwritingEvent>("BatchMetadataOverwriting")
                                      .WithEvent<BatchMetadataOverwritingObservation>("BatchMetadataOverwritingObservation")
                                      .WithCollectionPrefix("BatchMetadata");

                               if (_bulkWrite)
                               {
                                   builder.WithBulkWriteOptimization();
                               }
                           })
                           .Services
                           .BuildServiceProvider();

        _eventStore = _serviceProvider.GetRequiredService<IEventStore<BatchMetadataAggregate>>();
        _aggregateRepository = _serviceProvider.GetRequiredService<IAggregateRepository<BatchMetadataAggregate>>();
        var helper = _serviceProvider.GetRequiredService<IMongoHelper>();
        foreach (var configurator in _serviceProvider.GetServices<IMongoConfigurator>())
        {
            await configurator.ConfigureAsync(helper);
        }
    }

    [TearDown]
    public async Task TearDown() => await _serviceProvider.DisposeAsync();

    public BatchMetadataIntegrationTests(Boolean bulkWrite)
    {
        _bulkWrite = bulkWrite;
    }

    [Test]
    public async Task AppendEventsAsync_StateChangingBatch_ExposesPrecedingEventMetadata([Values] Boolean existingStream)
    {
        var aggregateId = Guid.NewGuid();
        if (existingStream)
        {
            await _eventStore.AppendEventsAsync([Recorded(aggregateId, 1)]);
        }

        var firstVersion = existingStream ? 2 : 1;
        var aggregate = await _eventStore.AppendEventsAsync(
            [Recorded(aggregateId, firstVersion), Recorded(aggregateId, firstVersion + 1)]);

        Int64[] expectedMetadata = existingStream ? [0, 1, 2] : [0, 1];
        aggregate.VersionsSeen.Should().Equal(expectedMetadata);
        aggregate.RevisionsSeen.Should().Equal(expectedMetadata);
        aggregate.Version.Should().Be(firstVersion + 1);
        aggregate.Revision.Should().Be(firstVersion + 1);
        (await _aggregateRepository.GetAsync(aggregateId)).Should().BeEquivalentTo(aggregate, options => options.Excluding(a => a.CreatedUtc));
        (await _aggregateRepository.GetAtVersionAsync(aggregateId, firstVersion + 1))
            .Should().BeEquivalentTo(aggregate, options => options.Excluding(a => a.CreatedUtc));
    }

    [Test]
    public async Task AppendEventsAsync_HandlersOverwritingMetadata_KeepStoreAssignedMetadata()
    {
        var aggregateId = Guid.NewGuid();
        await _eventStore.AppendEventsAsync([Overwriting(aggregateId, 1)]);

        var aggregate = await _eventStore.AppendEventsAsync(
        [
            Overwriting(aggregateId, 2),
            new BatchMetadataOverwritingObservation
            {
                AggregateId = aggregateId,
                Version = 3
            },
            Overwriting(aggregateId, 4)
        ]);

        aggregate.Version.Should().Be(4);
        aggregate.Revision.Should().Be(3);
        aggregate.OverwritesApplied.Should().Be(3);
        (await _aggregateRepository.GetAsync(aggregateId)).Should().BeEquivalentTo(aggregate, options => options.Excluding(a => a.CreatedUtc));

        var revisions = new List<Int64>();
        await foreach (var @event in _eventStore.GetEventStream(aggregateId))
        {
            revisions.Add(@event.Revision);
        }

        revisions.Should().Equal(1, 2, 2, 3);
    }

    private static BatchMetadataOverwritingEvent Overwriting(Guid aggregateId, Int64 version)
        => new()
        {
            AggregateId = aggregateId,
            Version = version
        };

    private static BatchMetadataRecordedEvent Recorded(Guid aggregateId, Int64 version)
        => new()
        {
            AggregateId = aggregateId,
            Version = version
        };
}

public class BatchMetadataAggregate : Aggregate
{
    public Int32 OverwritesApplied { get; set; }
    public List<Int64> RevisionsSeen { get; set; } = [];
    public List<Int64> VersionsSeen { get; set; } = [];
}

public class BatchMetadataRecordedEvent : Event<BatchMetadataAggregate>
{
    public override void Execute(BatchMetadataAggregate aggregate)
    {
        aggregate.VersionsSeen.Add(aggregate.Version);
        aggregate.RevisionsSeen.Add(aggregate.Revision);
    }
}

/// <summary>
/// Mimics an object mapper that copies every same-named member of the event onto the aggregate.
/// </summary>
public class BatchMetadataOverwritingEvent : Event<BatchMetadataAggregate>
{
    public override void Execute(BatchMetadataAggregate aggregate)
    {
        aggregate.Version = Version;
        aggregate.Revision = Revision;
        aggregate.OverwritesApplied++;
    }
}

/// <summary>
/// Writes the metadata during validation, which observational events must not do.
/// </summary>
public class BatchMetadataOverwritingObservation : ObservationalEvent<BatchMetadataAggregate>
{
    protected override void Validate(BatchMetadataAggregate aggregate)
    {
        aggregate.Version = 0;
        aggregate.Revision = 0;
    }
}
