// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Chaos.Mongo.Configuration;
using Chaos.Mongo.EventStore.Errors;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using NUnit.Framework;
using Testcontainers.MongoDb;

[TestFixture(false)]
[TestFixture(true)]
public class StoreAssignedAppendIntegrationTests
{
    private readonly List<LedgerStoreContext> _contexts = [];
    private readonly Boolean _integrityProtection;
    private MongoDbContainer _container;

    [OneTimeSetUp]
    public async Task GetMongoDbContainer() => _container = await MongoDbTestContainer.StartContainerAsync();

    [TearDown]
    public async Task TearDown()
    {
        foreach (var context in _contexts)
        {
            await context.DisposeAsync();
        }

        _contexts.Clear();
    }

    public StoreAssignedAppendIntegrationTests(Boolean integrityProtection)
    {
        _integrityProtection = integrityProtection;
    }

    [Test]
    public async Task AppendEventsAsync_ConcurrentObservationalAndStateChangingWriters_AllSucceed()
    {
        var context = await CreateContextAsync(options => options.MaxAppendRetries = 100);
        var aggregateId = await context.AppendStreamAsync(1);

        var observers = Enumerable.Range(0, 6)
                                  .Select(_ => Task.Run(() => context.Store.AppendEventsAsync([Audit(aggregateId)])));
        var writers = Enumerable.Range(0, 3)
                                .Select(_ => Task.Run(() => context.Store.AppendEventsAsync([Entry(aggregateId)])));
        await Task.WhenAll(observers.Concat(writers));

        (await context.CountEventsAsync(aggregateId)).Should().Be(10);
        var readModel = await ReadAggregateAsync(context, aggregateId);
        readModel.Revision.Should().Be(4);
        readModel.EntryCount.Should().Be(4);
        await AssertIntactAsync(context, aggregateId);
    }

    [Test]
    public async Task AppendEventsAsync_ConcurrentObservationalEvents_StateChangeWithMatchingExpectedRevisionSucceeds()
    {
        var context = await CreateContextAsync(options => options.MaxAppendRetries = 100);
        var aggregateId = await context.AppendStreamAsync(2);

        var observers = Enumerable.Range(0, 6)
                                  .Select(_ => Task.Run(() => context.Store.AppendEventsAsync([Audit(aggregateId)])));
        var writer = Task.Run(() => context.Store.AppendEventsAsync([Entry(aggregateId)], 2));
        await Task.WhenAll(observers.Append(writer));

        var aggregate = await writer;
        aggregate.Revision.Should().Be(3);
        (await context.CountEventsAsync(aggregateId)).Should().Be(9);
        await AssertIntactAsync(context, aggregateId);
    }

    [Test]
    public async Task AppendEventsAsync_DuplicateEventId_ThrowsMongoDuplicateEventExceptionWithoutRetry()
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(1);
        var stored = await context.FindEventAsync(aggregateId, 1);
        var attempts = 0;
        var store = (MongoEventStore<LedgerAggregate>)context.Store;
        store.AfterStreamHeadRead = _ =>
        {
            attempts++;
            return Task.CompletedTask;
        };

        var act = () => store.AppendEventsAsync([Entry(aggregateId, stored["_id"].AsGuid)]);

        await act.Should().ThrowAsync<MongoDuplicateEventException>();
        attempts.Should().Be(1);
        (await context.CountEventsAsync(aggregateId)).Should().Be(1);
    }

    [Test]
    public async Task AppendEventsAsync_ExpectedRevisionMatches_Appends([Values] Boolean storeAssigned)
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(2);
        await context.Store.AppendEventsAsync([Audit(aggregateId)]);

        var entry = storeAssigned ? Entry(aggregateId) : LedgerStoreContext.CreateEntry(aggregateId, 4);
        var aggregate = await context.Store.AppendEventsAsync([entry], 2);

        aggregate.Version.Should().Be(4);
        aggregate.Revision.Should().Be(3);
        entry.Version.Should().Be(4);
    }

    [Test]
    public async Task AppendEventsAsync_ExpectedRevisionMismatch_ThrowsAndPersistsNothing([Values] Boolean storeAssigned)
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(2);

        var entry = storeAssigned ? Entry(aggregateId) : LedgerStoreContext.CreateEntry(aggregateId, 3);
        var act = () => context.Store.AppendEventsAsync([entry], 1);

        await act.Should().ThrowAsync<MongoConcurrencyException>().WithMessage("*expected*revision 1*is at revision 2*");
        (await context.CountEventsAsync(aggregateId)).Should().Be(2);
        (await ReadAggregateAsync(context, aggregateId)).Revision.Should().Be(2);
    }

    [Test]
    public async Task AppendEventsAsync_ExplicitVersionsLosingThePositionRace_ThrowWithoutRetry()
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(1);
        var competitor = CreateCompetitor(context);
        var attempts = 0;
        var store = (MongoEventStore<LedgerAggregate>)context.Store;
        store.AfterStreamHeadRead = async ct =>
        {
            attempts++;
            await competitor.AppendEventsAsync([Audit(aggregateId)], cancellationToken: ct);
        };

        var act = () => store.AppendEventsAsync([LedgerStoreContext.CreateEntry(aggregateId, 2)]);

        await act.Should().ThrowAsync<MongoConcurrencyException>().WithMessage("*another process inserted*");
        attempts.Should().Be(1);
    }

    [Test]
    public async Task AppendEventsAsync_MixedUnsetAndExplicitVersions_ThrowsArgumentException()
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(1);

        var act = () => context.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(aggregateId, 2), Entry(aggregateId)]);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*all leave their version unset*");
        (await context.CountEventsAsync(aggregateId)).Should().Be(1);
    }

    [Test]
    public async Task AppendEventsAsync_ObservationalEventCommittedBeforeTransaction_RetriesAtNextPosition()
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(1);
        var competitor = CreateCompetitor(context);
        var store = (MongoEventStore<LedgerAggregate>)context.Store;
        store.AfterStreamHeadRead = async ct =>
        {
            store.AfterStreamHeadRead = null;
            await competitor.AppendEventsAsync([Audit(aggregateId)], cancellationToken: ct);
        };

        var entry = Entry(aggregateId);
        var aggregate = await store.AppendEventsAsync([entry], 1);

        entry.Version.Should().Be(3);
        aggregate.Version.Should().Be(3);
        aggregate.Revision.Should().Be(2);
        (await context.CountEventsAsync(aggregateId)).Should().Be(3);
        await AssertIntactAsync(context, aggregateId);
    }

    [Test]
    public async Task AppendEventsAsync_RetryAfterConcurrentCommits_ExecutesWithCurrentAttemptRevision()
    {
        await using var serviceProvider = await CreateBatchMetadataProviderAsync();
        var store = (MongoEventStore<BatchMetadataAggregate>)serviceProvider.GetRequiredService<IEventStore<BatchMetadataAggregate>>();
        var competitor = new MongoEventStore<BatchMetadataAggregate>(
            serviceProvider.GetRequiredService<IMongoHelper>(),
            serviceProvider.GetRequiredService<MongoEventStoreOptions<BatchMetadataAggregate>>());
        var aggregateId = Guid.NewGuid();
        await store.AppendEventsAsync([Recorded(aggregateId)]);

        var attempts = 0;
        store.AfterStreamHeadRead = async ct =>
        {
            attempts++;
            switch (attempts)
            {
                // The first attempt executes the event at revision 2, then loses its position to an observation.
                case 1:
                    await competitor.AppendEventsAsync(
                        [
                            new BatchMetadataOverwritingObservation
                            {
                                AggregateId = aggregateId
                            }
                        ],
                        cancellationToken: ct);
                    break;

                // The second attempt meets a state change between its reads; the third executes at revision 3.
                case 2:
                    await competitor.AppendEventsAsync([Recorded(aggregateId)], cancellationToken: ct);
                    break;
            }
        };

        var aggregate = await store.AppendEventsAsync([Recorded(aggregateId)]);

        attempts.Should().Be(3);
        aggregate.Revision.Should().Be(3);
        aggregate.EventRevisionsSeen.Should().Equal(1, 2, 3);
    }

    [Test]
    public async Task AppendEventsAsync_RetriesExhausted_ThrowsAndResetsVersions([Values] Boolean competingStateChange)
    {
        var context = await CreateContextAsync(options => options.MaxAppendRetries = 2);
        var aggregateId = await context.AppendStreamAsync(1);
        var competitor = CreateCompetitor(context);
        var attempts = 0;
        var store = (MongoEventStore<LedgerAggregate>)context.Store;
        store.AfterStreamHeadRead = async ct =>
        {
            attempts++;
            Event<LedgerAggregate> competing = competingStateChange ? Entry(aggregateId) : Audit(aggregateId);
            await competitor.AppendEventsAsync([competing], cancellationToken: ct);
        };

        var events = new Event<LedgerAggregate>[]
        {
            Entry(aggregateId),
            Audit(aggregateId)
        };
        var act = () => store.AppendEventsAsync(events);

        await act.Should().ThrowAsync<MongoConcurrencyException>().WithMessage("*after 2 retries*");
        attempts.Should().Be(3);
        events.Should().OnlyContain(e => e.Version == 0);
        (await context.CountEventsAsync(aggregateId)).Should().Be(4);
        await AssertIntactAsync(context, aggregateId);
    }

    [Test]
    public async Task AppendEventsAsync_StateChangeCommittedBetweenReads_ReexecutesAgainstFreshState()
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(1);
        var competitor = CreateCompetitor(context);
        var store = (MongoEventStore<LedgerAggregate>)context.Store;
        store.AfterStreamHeadRead = async ct =>
        {
            store.AfterStreamHeadRead = null;
            await competitor.AppendEventsAsync([Entry(aggregateId)], cancellationToken: ct);
        };

        var aggregate = await store.AppendEventsAsync([Entry(aggregateId), Audit(aggregateId)]);

        aggregate.Version.Should().Be(3);
        aggregate.Revision.Should().Be(3);
        aggregate.EntryCount.Should().Be(3);
        (await ReadAggregateAsync(context, aggregateId)).EntryCount.Should().Be(3);
        (await context.CountEventsAsync(aggregateId)).Should().Be(4);
        await AssertIntactAsync(context, aggregateId);
    }

    [Test]
    public async Task AppendEventsAsync_StateChangeCommittedBetweenReads_RechecksExpectedRevisionAgainstFreshState()
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(1);
        var competitor = CreateCompetitor(context);
        var store = (MongoEventStore<LedgerAggregate>)context.Store;
        store.AfterStreamHeadRead = async ct =>
        {
            store.AfterStreamHeadRead = null;
            await competitor.AppendEventsAsync([Entry(aggregateId)], cancellationToken: ct);
        };

        var entry = Entry(aggregateId);
        var act = () => store.AppendEventsAsync([entry], 1);

        await act.Should().ThrowAsync<MongoConcurrencyException>().WithMessage("*expected*revision 1*is at revision 2*");
        entry.Version.Should().Be(0);
        (await context.CountEventsAsync(aggregateId)).Should().Be(2);
        (await ReadAggregateAsync(context, aggregateId)).Revision.Should().Be(2);
    }

    [Test]
    public async Task AppendEventsAsync_UnsetVersions_AssignsSequentialPositions()
    {
        var context = await CreateContextAsync();
        var aggregateId = Guid.NewGuid();
        var events = new Event<LedgerAggregate>[]
        {
            Entry(aggregateId),
            Entry(aggregateId),
            Audit(aggregateId)
        };

        var aggregate = await context.Store.AppendEventsAsync(events);
        await context.Store.AppendEventsAsync([Entry(aggregateId)]);

        events.Select(e => e.Version).Should().Equal(1, 2, 3);
        events.Select(e => e.Revision).Should().Equal(1, 2, 2);
        aggregate.Version.Should().Be(2);
        (await context.FindEventAsync(aggregateId, 4))["Revision"].AsInt64.Should().Be(3);
        await AssertIntactAsync(context, aggregateId);
    }

    private static LedgerAuditedEvent Audit(Guid aggregateId) => LedgerStoreContext.CreateAudit(aggregateId, 0);

    private static MongoEventStore<LedgerAggregate> CreateCompetitor(LedgerStoreContext context)
        => new(context.MongoHelper, context.Options);

    private static BatchMetadataRecordedEvent Recorded(Guid aggregateId)
        => new()
        {
            AggregateId = aggregateId
        };

    private static LedgerEntryRecordedEvent Entry(Guid aggregateId, Guid? id = null)
        => LedgerStoreContext.CreateEntry(aggregateId, 0, id);

    private static Task<LedgerAggregate> ReadAggregateAsync(LedgerStoreContext context, Guid aggregateId)
        => context.MongoHelper.Database
                  .GetCollection<LedgerAggregate>(context.Options.ReadModelCollectionName)
                  .Find(a => a.Id == aggregateId)
                  .SingleAsync();

    private async Task AssertIntactAsync(LedgerStoreContext context, Guid aggregateId)
    {
        if (_integrityProtection)
        {
            (await context.CreateVerifier().VerifyStreamAsync(aggregateId)).IsIntact.Should().BeTrue();
        }
    }

    private async Task<ServiceProvider> CreateBatchMetadataProviderAsync()
    {
        var serviceProvider = new ServiceCollection()
                              .AddMongo(MongoUrl.Create(_container.GetConnectionString()), configure: options =>
                              {
                                  options.DefaultDatabase = $"StoreAssignedRetryTestDb_{Guid.NewGuid():N}";
                                  options.RunConfiguratorsOnStartup = false;
                              })
                              .WithEventStore<BatchMetadataAggregate>(builder =>
                              {
                                  builder.WithEvent<BatchMetadataRecordedEvent>("BatchMetadataRecorded")
                                         .WithEvent<BatchMetadataOverwritingObservation>("BatchMetadataOverwritingObservation")
                                         .WithCollectionPrefix("BatchMetadata");

                                  if (_integrityProtection)
                                  {
                                      builder.WithIntegrityProtection();
                                  }
                              })
                              .Services
                              .BuildServiceProvider();

        var helper = serviceProvider.GetRequiredService<IMongoHelper>();
        foreach (var configurator in serviceProvider.GetServices<IMongoConfigurator>())
        {
            await configurator.ConfigureAsync(helper);
        }

        return serviceProvider;
    }

    private async Task<LedgerStoreContext> CreateContextAsync(Action<MongoEventStoreOptions<LedgerAggregate>>? configureOptions = null)
    {
        var context = await LedgerStoreContext.CreateAsync(_container, _integrityProtection, configureOptions: configureOptions);
        _contexts.Add(context);
        return context;
    }
}
