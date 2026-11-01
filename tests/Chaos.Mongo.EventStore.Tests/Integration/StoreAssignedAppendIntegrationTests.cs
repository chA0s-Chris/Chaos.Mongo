// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Chaos.Mongo.Configuration;
using Chaos.Mongo.EventStore.Errors;
using Chaos.Mongo.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
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
    public async Task AppendEventsAsync_OnBeforeCommitDuplicateKeyInLookAlikeNamedCollection_PropagatesOriginalExceptionAndStoresNothing()
    {
        var context = await CreateContextAsync();

        // A collection whose name contains the full events-collection name as a substring, so a namespace
        // check could also be fooled by it.
        var outboxCollection = context.MongoHelper.Database.GetCollection<BsonDocument>($"{context.Options.EventsCollectionName}Outbox");
        var duplicateId = Guid.NewGuid();
        await outboxCollection.InsertOneAsync(new BsonDocument("_id", new BsonBinaryData(duplicateId, GuidRepresentation.Standard)));

        var aggregateId = Guid.NewGuid();
        var act = () => context.Store.AppendEventsAsync(
            [Entry(aggregateId)],
            onBeforeCommit: async (session, _, _, ct) =>
                await outboxCollection.InsertOneAsync(
                    session,
                    new BsonDocument("_id", new BsonBinaryData(duplicateId, GuidRepresentation.Standard)),
                    cancellationToken: ct));

        // MongoDuplicateEventException and MongoConcurrencyException are never MongoExceptions (the driver's
        // base type), so this alone confirms the original exception propagated unclassified.
        await act.Should().ThrowAsync<MongoException>();
        (await context.CountEventsAsync(aggregateId)).Should().Be(0);
    }

    [Test]
    public async Task AppendEventsAsync_OnBeforeCommitDuplicateKeyInLookAlikeNamedCollection_PropagatesOriginalExceptionAndStoresNothing_UserDefinedIndex()
    {
        // A custom unique index whose name merely contains IndexNames.AggregateIdWithVersionUnique as a
        // substring, rather than matching it exactly, must not be misclassified as the concurrency index.
        var context = await CreateContextAsync();
        await context.Events.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("Reference"),
                new CreateIndexOptions
                {
                    Unique = true,
                    Name = $"Ledger_{IndexNames.AggregateIdWithVersionUnique}_External"
                }));

        // CreateEntry always sets a fixed Reference value, so appending a second entry under this index
        // collides on it, regardless of aggregate or version.
        await context.AppendStreamAsync(1);

        var aggregateId = Guid.NewGuid();
        var act = () => context.Store.AppendEventsAsync([Entry(aggregateId)]);

        // MongoDuplicateEventException and MongoConcurrencyException are never MongoExceptions (the driver's
        // base type), so this alone confirms the original exception propagated unclassified.
        await act.Should().ThrowAsync<MongoException>();
        (await context.CountEventsAsync(aggregateId)).Should().Be(0);
    }

    [Test]
    public async Task AppendEventsAsync_OnBeforeCommitClientBulkWriteDuplicateKeyAtEventWriteIndexInLookAlikeNamedCollection_PropagatesOriginalExceptionAndStoresNothing()
    {
        if (!MongoDbTestImages.SupportsBulkWriteOptimization)
        {
            Assert.Ignore("The client bulk write API requires MongoDB 8.0 or later; the connected test server does not support it.");
        }

        var context = await CreateContextAsync(options => options.CheckpointInterval = 1);

        // A collection whose name contains the full events-collection name as a substring, so a namespace
        // check could also be fooled by it.
        var outboxCollection = context.MongoHelper.Database.GetCollection<BsonDocument>($"{context.Options.EventsCollectionName}Outbox");
        var duplicateId = Guid.NewGuid();
        await outboxCollection.InsertOneAsync(new BsonDocument("_id", new BsonBinaryData(duplicateId, GuidRepresentation.Standard)));

        var aggregateId = Guid.NewGuid();
        var act = () => context.Store.AppendEventsAsync(
            [Entry(aggregateId)],
            onBeforeCommit: async (session, _, helper, ct) =>
                // A client bulk write at index 0 — the same index an events-collection conflict would use
                // — into the look-alike collection, raising the same exception type a bulk-write-optimized
                // events-collection conflict would (ClientBulkWriteException).
                await helper.Client.BulkWriteAsync(
                    session,
                    [
                        new BulkWriteInsertOneModel<BsonDocument>(
                            outboxCollection.CollectionNamespace,
                            new BsonDocument("_id", new BsonBinaryData(duplicateId, GuidRepresentation.Standard)))
                    ],
                    cancellationToken: ct));

        // Asserting the concrete ClientBulkWriteException and its duplicate-key write error at the
        // callback's own request index 0 confirms the original exception propagated unclassified,
        // rather than only that it is not one of this store's two internal exception types.
        var exception = await act.Should().ThrowAsync<ClientBulkWriteException>();
        exception.Which.WriteErrors.Should().ContainKey(0);
        exception.Which.WriteErrors[0].Category.Should().Be(ServerErrorCategory.DuplicateKey);
        (await context.CountEventsAsync(aggregateId)).Should().Be(0);
        (await context.ReadModelExistsAsync(aggregateId)).Should().BeFalse();
        (await context.ReadRawCheckpointsAsync(aggregateId)).Should().BeEmpty();
    }

    [Test]
    public async Task AppendEventsAsync_CheckpointDuplicateKey_PropagatesOriginalExceptionAndStoresNothing()
    {
        var context = await CreateContextAsync(options => options.CheckpointInterval = 1);
        var aggregateId = Guid.NewGuid();

        // A checkpoint already stored at the exact (AggregateId, Version) this append's checkpoint will
        // use, so the checkpoint insert collides on the checkpoint collection's own "_id_" index — named
        // identically to the events collection's — even though this append never touches an event
        // document's _id.
        var checkpointCollection = context.MongoHelper.Database.GetCollection<CheckpointDocument<LedgerAggregate>>(context.Options.CheckpointCollectionName);
        await checkpointCollection.InsertOneAsync(new CheckpointDocument<LedgerAggregate>
        {
            Id = new CheckpointId(aggregateId, 1),
            Revision = 1,
            State = new LedgerAggregate
            {
                Id = aggregateId
            }
        });
        var seededCheckpoint = (await context.ReadRawCheckpointsAsync(aggregateId)).Single();

        var act = () => context.Store.AppendEventsAsync([Entry(aggregateId)]);

        // Asserting the concrete MongoWriteException and its duplicate-key category confirms the
        // original exception propagated unclassified, rather than only that it is not one of this
        // store's two internal exception types. The checkpoint collection must still contain exactly
        // the document seeded above, unchanged by the aborted insert.
        var exception = await act.Should().ThrowAsync<MongoWriteException>();
        exception.Which.WriteError.Category.Should().Be(ServerErrorCategory.DuplicateKey);
        (await context.CountEventsAsync(aggregateId)).Should().Be(0);
        (await context.ReadModelExistsAsync(aggregateId)).Should().BeFalse();
        (await context.ReadRawCheckpointsAsync(aggregateId)).Should().ContainSingle(c => c.Equals(seededCheckpoint));
    }

    [Test]
    public async Task AppendEventsAsync_CheckpointDuplicateKeyWithBulkWriteOptimization_PropagatesOriginalExceptionAndStoresNothing()
    {
        var context = await LedgerStoreContext.CreateAsync(
            _container,
            _integrityProtection,
            true,
            configureOptions: options => options.CheckpointInterval = 1);
        _contexts.Add(context);
        var aggregateId = Guid.NewGuid();

        // Same collision as the non-bulk checkpoint case above, but under bulk-write optimization the
        // checkpoint model occupies an index in `models` after every event-document entry, so its
        // duplicate key must be filtered out by the events-only index range rather than misclassified
        // as an events-collection conflict.
        var checkpointCollection = context.MongoHelper.Database.GetCollection<CheckpointDocument<LedgerAggregate>>(context.Options.CheckpointCollectionName);
        await checkpointCollection.InsertOneAsync(new CheckpointDocument<LedgerAggregate>
        {
            Id = new CheckpointId(aggregateId, 1),
            Revision = 1,
            State = new LedgerAggregate
            {
                Id = aggregateId
            }
        });
        var seededCheckpoint = (await context.ReadRawCheckpointsAsync(aggregateId)).Single();

        var act = () => context.Store.AppendEventsAsync([Entry(aggregateId)]);

        // This single-event batch's models are [event, read-model replace, checkpoint insert] (in that
        // order, since the entry is state-changing), so the checkpoint's duplicate key is reported at
        // write-error index 2 — past the events-only index range the production catch filters on — and
        // must propagate as the original, unclassified ClientBulkWriteException.
        var exception = await act.Should().ThrowAsync<ClientBulkWriteException>();
        exception.Which.WriteErrors.Should().ContainKey(2);
        exception.Which.WriteErrors[2].Category.Should().Be(ServerErrorCategory.DuplicateKey);
        (await context.CountEventsAsync(aggregateId)).Should().Be(0);
        (await context.ReadModelExistsAsync(aggregateId)).Should().BeFalse();
        (await context.ReadRawCheckpointsAsync(aggregateId)).Should().ContainSingle(c => c.Equals(seededCheckpoint));
    }

    [Test]
    public async Task AppendEventsAsync_DuplicateEventIdAtFreshExplicitPosition_ThrowsMongoDuplicateEventException()
    {
        var context = await CreateContextAsync();
        var firstAggregateId = await context.AppendStreamAsync(1);
        var stored = await context.FindEventAsync(firstAggregateId, 1);
        var duplicateId = stored["_id"].AsGuid;

        // A fresh explicit position for a different aggregate is never stale (ValidatePositionsAsync
        // only looks up stored IDs when firstVersion <= head.Version), so this exercises the genuine
        // events-collection duplicate-key catch at the insert itself — covering both the typed insert
        // path (integrity disabled) and the serialized-document insert path (integrity enabled), the two
        // cases this fixture is parameterized over.
        var otherAggregateId = Guid.NewGuid();
        var act = () => context.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(otherAggregateId, 1, duplicateId)]);

        await act.Should().ThrowAsync<MongoDuplicateEventException>();
        (await context.CountEventsAsync(otherAggregateId)).Should().Be(0);
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
    public async Task AppendEventsAsync_StoreAssignedRetryWithSameEventIdAndStaleExpectedRevision_ThrowsMongoDuplicateEventException()
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(1);
        var eventId = Guid.NewGuid();

        // The caller read revision 1 before the first attempt, which committed and advanced it to 2.
        await context.Store.AppendEventsAsync([Entry(aggregateId, eventId)], 1);

        // A retry resubmits the same event ID in a fresh instance with Version left unset, carrying the
        // same (now stale) expectedRevision the caller read before the first attempt.
        var retry = Entry(aggregateId, eventId);
        var act = () => context.Store.AppendEventsAsync([retry], 1);

        await act.Should().ThrowAsync<MongoDuplicateEventException>();
        (await context.CountEventsAsync(aggregateId)).Should().Be(2);
    }

    [Test]
    public async Task AppendEventsAsync_StoreAssignedRetryWithSameEventIdWithoutExpectedRevision_ThrowsMongoDuplicateEventException()
    {
        var context = await CreateContextAsync();
        var aggregateId = await context.AppendStreamAsync(1);
        var eventId = Guid.NewGuid();

        await context.Store.AppendEventsAsync([Entry(aggregateId, eventId)]);

        // A retry resubmits the same event ID in a fresh instance with Version left unset, without an
        // expectedRevision to recheck.
        var retry = Entry(aggregateId, eventId);
        var act = () => context.Store.AppendEventsAsync([retry]);

        await act.Should().ThrowAsync<MongoDuplicateEventException>();
        (await context.CountEventsAsync(aggregateId)).Should().Be(2);
    }

    [Test]
    public async Task AppendEventsAsync_StoreAssignedRetryWithSameEventIdWithoutExpectedRevision_DoesNotReapplyEventsAgainstUpdatedState()
    {
        await using var serviceProvider = await CreateBatchMetadataProviderAsync();
        var store = serviceProvider.GetRequiredService<IEventStore<BatchMetadataAggregate>>();
        var aggregateId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        // BatchMetadataAppliedOnceEvent's Execute rejects an aggregate it was already applied to. If the
        // store-assigned idempotency preflight were absent or moved after ApplyEvents, this retry would
        // re-run Execute against the read model the first attempt already updated and surface
        // MongoEventValidationException instead of the duplicate the retry actually is.
        await store.AppendEventsAsync([
            new BatchMetadataAppliedOnceEvent
            {
                Id = eventId,
                AggregateId = aggregateId
            }
        ]);

        var retry = new BatchMetadataAppliedOnceEvent
        {
            Id = eventId,
            AggregateId = aggregateId
        };
        var act = () => store.AppendEventsAsync([retry]);

        await act.Should().ThrowAsync<MongoDuplicateEventException>();
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
                                         .WithEvent<BatchMetadataAppliedOnceEvent>("BatchMetadataAppliedOnce")
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
