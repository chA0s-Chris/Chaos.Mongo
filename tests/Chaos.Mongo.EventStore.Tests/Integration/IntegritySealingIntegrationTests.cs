// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Chaos.Mongo.EventStore.Errors;
using Chaos.Mongo.EventStore.Integrity;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;
using NUnit.Framework;
using Testcontainers.MongoDb;

public class IntegritySealingIntegrationTests
{
    private readonly List<LedgerStoreContext> _contexts = [];
    private MongoDbContainer _container;

    [Test]
    public async Task AppendEventsAsync_MoreUnsealedPredecessorsThanOneChunk_ThrowsAndPersistsNothing()
    {
        var (unprotected, protectedContext) = await CreateSharedContextsAsync(options => options.SealingChunkSize = 2);
        var aggregateId = await unprotected.AppendStreamAsync(3);

        var act = () => protectedContext.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(aggregateId, 4)]);

        await act.Should().ThrowAsync<MongoEventStoreException>().WithMessage("*more than 2*not yet sealed*");
        (await protectedContext.CountEventsAsync(aggregateId)).Should().Be(3);
        (await protectedContext.CreateVerifier().VerifyStreamAsync(aggregateId))
            .Should().Be(StreamVerificationResult.Broken(1, StreamVerificationFailure.NotSealed));
    }

    [Test]
    public async Task AppendEventsAsync_UnsealedPredecessorsWithinOneChunk_SealsThemBeforeAppending()
    {
        var (unprotected, protectedContext) = await CreateSharedContextsAsync();
        var aggregateId = await unprotected.AppendStreamAsync(3);

        await protectedContext.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(aggregateId, 4)]);

        (await protectedContext.CreateVerifier().VerifyStreamAsync(aggregateId)).IsIntact.Should().BeTrue();
        for (var version = 1; version <= 3; version++)
        {
            LedgerStoreContext.ReadIntegrity(await protectedContext.FindEventAsync(aggregateId, version))
                              .SealMode.Should().Be(IntegritySealMode.Retroactive);
        }

        LedgerStoreContext.ReadIntegrity(await protectedContext.FindEventAsync(aggregateId, 4))
                          .SealMode.Should().Be(IntegritySealMode.Append);
    }

    [Test]
    public async Task AppendSealingWindow_LongUnsealedStream_ReadsAtMostOneChunk()
    {
        var (unprotected, protectedContext) = await CreateSharedContextsAsync(options => options.SealingChunkSize = 2);
        var aggregateId = await unprotected.AppendStreamAsync(50);

        var window = await ExplainAsync(
            protectedContext,
            SealingSweepQueries<LedgerAggregate>.AppendSealingWindow(aggregateId, 50),
            protectedContext.Options.SealingChunkSize + 1);

        window["nReturned"].ToInt64().Should().Be(3);
        window["totalKeysExamined"].ToInt64().Should().BeLessThanOrEqualTo(4);
        window["totalDocsExamined"].ToInt64().Should().BeLessThanOrEqualTo(3);
    }

    [Test]
    public async Task ConcurrentSweepAndAppends_ProduceConsistentChains()
    {
        var (unprotected, protectedContext) = await CreateSharedContextsAsync();
        var aggregateIds = new List<Guid>();
        for (var stream = 0; stream < 10; stream++)
        {
            aggregateIds.Add(await unprotected.AppendStreamAsync(2));
        }

        var appends = aggregateIds.Select(id => AppendNextWithRetryAsync(protectedContext, id, 3));
        await Task.WhenAll(appends.Append(protectedContext.Sweep.RunPassAsync()));

        foreach (var aggregateId in aggregateIds)
        {
            (await protectedContext.CreateVerifier().VerifyStreamAsync(aggregateId)).IsIntact.Should().BeTrue();
            (await protectedContext.CountEventsAsync(aggregateId)).Should().Be(3);
        }
    }

    [OneTimeSetUp]
    public async Task GetMongoDbContainer() => _container = await MongoDbTestContainer.StartContainerAsync();

    [Test]
    public async Task HostedService_AfterStart_RunsPassImmediatelyAndAfterEachInterval()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var databaseName = $"IntegrityTestDb_{Guid.NewGuid():N}";
        var unprotected = await CreateContextAsync(false, databaseName);
        var protectedContext = await CreateContextAsync(true, databaseName, timeProvider: timeProvider);
        var firstStream = await unprotected.AppendStreamAsync(2);
        var hostedService = new EventStreamSealingHostedService<LedgerAggregate>(protectedContext.Sweep);
        await hostedService.StartingAsync(CancellationToken.None);
        await hostedService.StartAsync(CancellationToken.None);

        await hostedService.StartedAsync(CancellationToken.None);
        await WaitUntilAsync(async () => (await protectedContext.CreateVerifier().VerifyStreamAsync(firstStream)).IsIntact);

        var secondStream = await unprotected.AppendStreamAsync(2);
        await WaitUntilAsync(
            async () => (await protectedContext.CreateVerifier().VerifyStreamAsync(secondStream)).IsIntact,
            () => timeProvider.Advance(protectedContext.Options.SealingSweepInterval));

        await hostedService.StoppingAsync(CancellationToken.None);
        await hostedService.StopAsync(CancellationToken.None);
        await hostedService.StoppedAsync(CancellationToken.None);
    }

    [Test]
    public async Task HostedService_FailedPass_RetriesAfterRetryDelay()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (unprotected, protectedContext) = await CreateSharedContextsAsync();
        var aggregateId = await unprotected.AppendStreamAsync(2);
        var stateCollection = protectedContext.MongoHelper.Database
                                              .GetCollection<BsonDocument>(protectedContext.Options.IntegrityStateCollectionName);
        await stateCollection.InsertOneAsync(new BsonDocument
        {
            { "_id", IntegritySealingState.DocumentId },
            { "StreamsChecked", "unreadable" }
        });
        var logger = new CapturingLogger<EventStreamSealingSweep<LedgerAggregate>>();
        var sweep = new EventStreamSealingSweep<LedgerAggregate>(protectedContext.MongoHelper, protectedContext.Options, timeProvider, logger);
        var hostedService = new EventStreamSealingHostedService<LedgerAggregate>(sweep);

        await hostedService.StartedAsync(CancellationToken.None);
        await WaitUntilAsync(() => Task.FromResult(logger.Entries.Any(e => e.Level == LogLevel.Error && e.Message.Contains("is retried"))));
        await stateCollection.DeleteManyAsync(FilterDefinition<BsonDocument>.Empty);

        // Advancing by the retry delay alone, far less than the 24-hour interval, must trigger the next pass.
        await WaitUntilAsync(
            async () => (await protectedContext.CreateVerifier().VerifyStreamAsync(aggregateId)).IsIntact,
            () => timeProvider.Advance(protectedContext.Options.SealingSweepRetryDelay));
        await hostedService.StoppingAsync(CancellationToken.None);
    }

    [Test]
    public async Task RunPassAsync_InterruptedPass_ResumesAfterPersistedCursor()
    {
        var (unprotected, protectedContext) = await CreateSharedContextsAsync();
        for (var stream = 0; stream < 3; stream++)
        {
            await unprotected.AppendStreamAsync(1);
        }

        var streamOrder = await ReadStreamOrderAsync(protectedContext);
        var passStartedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await protectedContext.MongoHelper.Database
                              .GetCollection<IntegritySealingState>(protectedContext.Options.IntegrityStateCollectionName)
                              .InsertOneAsync(new IntegritySealingState
                              {
                                  Cursor = streamOrder[1],
                                  PassStartedUtc = passStartedUtc,
                                  StreamsChecked = 2
                              });

        var completed = await protectedContext.Sweep.RunPassAsync();

        completed.Should().BeTrue();
        var verifier = protectedContext.CreateVerifier();
        (await verifier.VerifyStreamAsync(streamOrder[0])).IsIntact.Should().BeFalse();
        (await verifier.VerifyStreamAsync(streamOrder[1])).IsIntact.Should().BeFalse();
        (await verifier.VerifyStreamAsync(streamOrder[2])).IsIntact.Should().BeTrue();
        var status = await protectedContext.Sweep.GetStatusAsync();
        status.Should().NotBeNull();
        status.PassStartedUtc.Should().Be(passStartedUtc);
        status.StreamsChecked.Should().Be(3);
        status.StreamsSealed.Should().Be(1);
        status.Cursor.Should().BeNull();
    }

    [Test]
    public async Task RunPassAsync_LaterChunkFails_CountsEventsOfCommittedChunks()
    {
        var (unprotected, protectedContext) = await CreateSharedContextsAsync(options => options.SealingChunkSize = 2);
        var aggregateId = await unprotected.AppendStreamAsync(5);
        await unprotected.Events.DeleteOneAsync(EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, 4));

        var completed = await protectedContext.Sweep.RunPassAsync();

        completed.Should().BeTrue();
        var status = await protectedContext.Sweep.GetStatusAsync();
        status.Should().NotBeNull();
        status.EventsSealed.Should().Be(2);
        status.StreamsSealed.Should().Be(1);
        status.StreamsChecked.Should().Be(1);
    }

    [Test]
    public async Task RunPassAsync_LockHeldByAnotherInstance_ReturnsFalseWithoutSealing()
    {
        var (unprotected, protectedContext) = await CreateSharedContextsAsync();
        var aggregateId = await unprotected.AppendStreamAsync(1);
        await using var foreignLock = await protectedContext.MongoHelper.TryAcquireLockAsync(protectedContext.Sweep.LockName);
        foreignLock.Should().NotBeNull();

        var completed = await protectedContext.Sweep.RunPassAsync();

        completed.Should().BeFalse();
        (await protectedContext.CreateVerifier().VerifyStreamAsync(aggregateId))
            .Should().Be(StreamVerificationResult.Broken(1, StreamVerificationFailure.NotSealed));
        (await protectedContext.Sweep.GetStatusAsync()).Should().BeNull();
    }

    [Test]
    public async Task RunPassAsync_MalformedIntegrityOnSealedPrefix_SkipsStreamAndSealsLaterStreams()
    {
        var (unprotected, protectedContext) = await CreateSharedContextsAsync();
        var corruptedStream = await protectedContext.AppendStreamAsync(2);
        await unprotected.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(corruptedStream, 3)]);
        await protectedContext.Events.UpdateOneAsync(
            EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(corruptedStream, 2),
            Builders<BsonDocument>.Update.Set(EventIntegrityChain.ElementName, "tampered"));
        var healthyStreams = new[]
        {
            await unprotected.AppendStreamAsync(2),
            await unprotected.AppendStreamAsync(2)
        };

        var completed = await protectedContext.Sweep.RunPassAsync();

        completed.Should().BeTrue();
        foreach (var aggregateId in healthyStreams)
        {
            (await protectedContext.CreateVerifier().VerifyStreamAsync(aggregateId)).IsIntact.Should().BeTrue();
        }

        (await protectedContext.CreateVerifier().VerifyStreamAsync(corruptedStream))
            .Should().Be(StreamVerificationResult.Broken(2, StreamVerificationFailure.MalformedIntegrity));
        var status = await protectedContext.Sweep.GetStatusAsync();
        status.Should().NotBeNull();
        status.StreamsChecked.Should().Be(3);
    }

    [Test]
    public async Task RunPassAsync_RetroactiveSealing_PreservesStoredBytes()
    {
        var (unprotected, protectedContext) = await CreateSharedContextsAsync();
        var aggregateId = await unprotected.AppendStreamAsync(2);
        var originalBytes = await unprotected.ReadRawEventAsync(aggregateId, 2);

        await protectedContext.Sweep.RunPassAsync();

        var sealedBytes = await protectedContext.ReadRawEventAsync(aggregateId, 2);
        sealedBytes.Should().NotEqual(originalBytes);
        RawBsonElements.RemoveTopLevelElement(sealedBytes, EventIntegrityChain.ElementName).Should().Equal(originalBytes);
    }

    [Test]
    public async Task RunPassAsync_StreamLongerThanOneChunk_SealsAllChunks()
    {
        var (unprotected, protectedContext) = await CreateSharedContextsAsync(options => options.SealingChunkSize = 2);
        var aggregateId = await unprotected.AppendStreamAsync(5);

        await protectedContext.Sweep.RunPassAsync();

        (await protectedContext.CreateVerifier().VerifyStreamAsync(aggregateId)).IsIntact.Should().BeTrue();
        var status = await protectedContext.Sweep.GetStatusAsync();
        status.Should().NotBeNull();
        status.EventsSealed.Should().Be(5);
    }

    [Test]
    public async Task RunPassAsync_StreamWithMissingVersion_SkipsStreamAndCompletesPass()
    {
        var (unprotected, protectedContext) = await CreateSharedContextsAsync();
        var brokenStream = await unprotected.AppendStreamAsync(3);
        await unprotected.Events.DeleteOneAsync(EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(brokenStream, 2));
        var healthyStream = await unprotected.AppendStreamAsync(2);

        var completed = await protectedContext.Sweep.RunPassAsync();

        completed.Should().BeTrue();
        (await protectedContext.CreateVerifier().VerifyStreamAsync(healthyStream)).IsIntact.Should().BeTrue();
        (await protectedContext.CreateVerifier().VerifyStreamAsync(brokenStream))
            .Should().Be(StreamVerificationResult.Broken(1, StreamVerificationFailure.NotSealed));
        var status = await protectedContext.Sweep.GetStatusAsync();
        status.Should().NotBeNull();
        status.StreamsChecked.Should().Be(2);
        status.StreamsSealed.Should().Be(1);
    }

    [Test]
    public async Task RunPassAsync_UnsealedStreams_SealsEveryEventRetroactivelyAndReportsProgress()
    {
        var (unprotected, protectedContext) = await CreateSharedContextsAsync();
        var unsealedStreams = new[]
        {
            await unprotected.AppendStreamAsync(1),
            await unprotected.AppendStreamAsync(2),
            await unprotected.AppendStreamAsync(3)
        };
        var sealedStream = await protectedContext.AppendStreamAsync(2);

        var completed = await protectedContext.Sweep.RunPassAsync();

        completed.Should().BeTrue();
        foreach (var aggregateId in unsealedStreams.Append(sealedStream))
        {
            (await protectedContext.CreateVerifier().VerifyStreamAsync(aggregateId)).IsIntact.Should().BeTrue();
        }

        LedgerStoreContext.ReadIntegrity(await protectedContext.FindEventAsync(unsealedStreams[2], 3))
                          .SealMode.Should().Be(IntegritySealMode.Retroactive);
        var status = await protectedContext.Sweep.GetStatusAsync();
        status.Should().NotBeNull();
        status.StreamsChecked.Should().Be(4);
        status.StreamsSealed.Should().Be(3);
        status.EventsSealed.Should().Be(6);
        status.PassStartedUtc.Should().NotBeNull();
        status.PassCompletedUtc.Should().NotBeNull();
        status.IsPassInProgress.Should().BeFalse();
    }

    [Test]
    public async Task SealNextChunkAsync_StaleSealedVersion_ThrowsMongoConcurrencyException()
    {
        var context = await CreateContextAsync(true);
        var aggregateId = await context.AppendStreamAsync(2);
        var sealer = new EventStreamSealer<LedgerAggregate>(context.MongoHelper, context.Options);

        var act = () => context.MongoHelper.ExecuteInTransaction((_, session, ct) => sealer.SealNextChunkAsync(session, aggregateId, 0, ct));

        await act.Should().ThrowAsync<MongoConcurrencyException>().WithMessage("*sealed concurrently*");
    }

    [Test]
    public async Task SweepQueries_ReadOnlyIndexEntriesPerStream()
    {
        var context = await CreateContextAsync(true);
        await context.AppendStreamAsync(50);
        await context.AppendStreamAsync(50);
        var streamOrder = await ReadStreamOrderAsync(context);

        var nextStream = await ExplainAsync(context, SealingSweepQueries<LedgerAggregate>.NextStream(null));
        var nextAfterCursor = await ExplainAsync(context, SealingSweepQueries<LedgerAggregate>.NextStream(streamOrder[0]));
        var head = await ExplainAsync(context, SealingSweepQueries<LedgerAggregate>.StreamHead(streamOrder[0]));

        nextAfterCursor["nReturned"].ToInt64().Should().Be(1, "the stream after the cursor must be found without scanning its events");

        foreach (var stats in new[]
                 {
                     nextStream,
                     nextAfterCursor,
                     head
                 })
        {
            stats["totalKeysExamined"].ToInt64().Should().BeLessThanOrEqualTo(2);
            stats["totalDocsExamined"].ToInt64().Should().BeLessThanOrEqualTo(1);
        }
    }

    [TearDown]
    public async Task TearDown()
    {
        foreach (var context in _contexts)
        {
            await context.DisposeAsync();
        }

        _contexts.Clear();
    }

    private static async Task AppendNextWithRetryAsync(LedgerStoreContext context, Guid aggregateId, Int64 version)
    {
        for (var attempt = 0;; attempt++)
        {
            try
            {
                await context.Store.AppendEventsAsync([LedgerStoreContext.CreateEntry(aggregateId, version)]);
                return;
            }
            catch (MongoConcurrencyException) when (attempt < 20)
            {
                // The sweep sealed the stream concurrently; nothing was persisted, so retry.
            }
        }
    }

    private static async Task<BsonDocument> ExplainAsync(LedgerStoreContext context, SweepQuery query, Int32 limit = 1)
    {
        var command = new BsonDocument
        {
            {
                "explain", new BsonDocument
                {
                    { "find", context.Options.EventsCollectionName },
                    { "filter", query.Filter },
                    { "sort", query.Sort },
                    { "projection", query.Projection },
                    { "limit", limit }
                }
            },
            { "verbosity", "executionStats" }
        };

        var explanation = await context.MongoHelper.Database.RunCommandAsync<BsonDocument>(command);
        return explanation["executionStats"].AsBsonDocument;
    }

    private static async Task<List<Guid>> ReadStreamOrderAsync(LedgerStoreContext context)
    {
        var aggregateIds = await context.Events
                                        .Find(FilterDefinition<BsonDocument>.Empty)
                                        .Sort(new BsonDocument("AggregateId", 1))
                                        .Project(new BsonDocument("AggregateId", 1))
                                        .ToListAsync();
        return aggregateIds.Select(d => d["AggregateId"].AsGuid).Distinct().ToList();
    }

    private static async Task WaitUntilAsync(Func<Task<Boolean>> condition, Action? beforeEachCheck = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            beforeEachCheck?.Invoke();
            if (await condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail("The condition was not met within the timeout.");
    }

    private async Task<LedgerStoreContext> CreateContextAsync(Boolean integrityProtection,
                                                              String? databaseName = null,
                                                              Action<MongoEventStoreOptions<LedgerAggregate>>? configureOptions = null,
                                                              TimeProvider? timeProvider = null)
    {
        var context = await LedgerStoreContext.CreateAsync(
            _container,
            integrityProtection,
            databaseName: databaseName,
            configureOptions: configureOptions,
            timeProvider: timeProvider);
        _contexts.Add(context);
        return context;
    }

    /// <summary>
    /// Creates an unprotected and a protected store on the same collections, so the unprotected one
    /// can write events without integrity data for the protected one to seal.
    /// </summary>
    private async Task<(LedgerStoreContext Unprotected, LedgerStoreContext Protected)> CreateSharedContextsAsync(
        Action<MongoEventStoreOptions<LedgerAggregate>>? configureOptions = null)
    {
        var databaseName = $"IntegrityTestDb_{Guid.NewGuid():N}";
        var unprotected = await CreateContextAsync(false, databaseName);
        var protectedContext = await CreateContextAsync(true, databaseName, configureOptions);
        return (unprotected, protectedContext);
    }
}
