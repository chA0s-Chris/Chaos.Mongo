// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Chaos.Mongo.Configuration;
using Chaos.Mongo.EventStore.Errors;
using Chaos.Mongo.EventStore.Integrity;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using NUnit.Framework;
using Testcontainers.MongoDb;

public class IntegrityChainIntegrationTests
{
    private static readonly DateTime FixedCreatedUtc = new DateTime(2026, 10, 7, 12, 30, 45, DateTimeKind.Utc).AddTicks(1234);
    private static readonly Guid FixedReference = Guid.Parse("8b4d4c1e-9e3f-4c55-a1a4-0f1e6c2d7b90");

    private readonly List<ServiceProvider> _serviceProviders = [];
    private MongoDbContainer _container;

    [Test]
    public async Task AppendEventsAsync_BatchWithIntegrityProtection_SealsEventsIntoChainFromGenesis()
    {
        var context = await CreateContextAsync();
        var aggregateId = Guid.NewGuid();

        await context.Store.AppendEventsAsync([CreateEntry(aggregateId, 1), CreateEntry(aggregateId, 2)]);

        var first = await context.FindEventAsync(aggregateId, 1);
        var second = await context.FindEventAsync(aggregateId, 2);
        var firstIntegrity = ReadIntegrity(first);
        var secondIntegrity = ReadIntegrity(second);

        first[EventIntegrityChain.ElementName]["FormatVersion"].AsInt32.Should().Be(1);
        first[EventIntegrityChain.ElementName]["Algorithm"].AsString.Should().Be("SHA-256");
        first[EventIntegrityChain.ElementName]["SealMode"].AsString.Should().Be("Append");
        firstIntegrity.PreviousHash.Should().Equal(EventIntegrityChain.ComputeGenesis(nameof(LedgerAggregate), aggregateId));
        firstIntegrity.Hash.Should().HaveCount(32);
        secondIntegrity.PreviousHash.Should().Equal(firstIntegrity.Hash);
        secondIntegrity.SealMode.Should().Be(IntegritySealMode.Append);
    }

    [Test]
    [TestCase(false)]
    [TestCase(true)]
    public async Task AppendEventsAsync_BothAppendPaths_PersistByteIdenticalDocuments(Boolean integrityProtection)
    {
        var defaultPath = await CreateContextAsync(integrityProtection);
        var bulkWritePath = await CreateContextAsync(integrityProtection, true);
        var aggregateId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        await defaultPath.Store.AppendEventsAsync([CreateEntry(aggregateId, 1, eventId, FixedCreatedUtc)]);
        await bulkWritePath.Store.AppendEventsAsync([CreateEntry(aggregateId, 1, eventId, FixedCreatedUtc)]);

        var defaultBytes = await ReadRawEventAsync(defaultPath, aggregateId, 1);
        var bulkWriteBytes = await ReadRawEventAsync(bulkWritePath, aggregateId, 1);

        defaultBytes.Should().Equal(bulkWriteBytes);
    }

    [Test]
    public async Task AppendEventsAsync_ConcurrentAppendsWithIntegrityProtection_ProduceLinearChain()
    {
        var context = await CreateContextAsync();
        var aggregateId = Guid.NewGuid();
        await context.Store.AppendEventsAsync([CreateEntry(aggregateId, 1)]);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => AppendWithRetryAsync(context, aggregateId)));

        var result = await context.CreateVerifier().VerifyStreamAsync(aggregateId);
        result.IsIntact.Should().BeTrue();
        (await context.Events.CountDocumentsAsync(EventDocumentFields<LedgerAggregate>.ForAggregate<BsonDocument>(aggregateId)))
            .Should().Be(9);
    }

    [Test]
    public async Task AppendEventsAsync_LaterAppendWithIntegrityProtection_ChainsFromStoredPredecessor()
    {
        var context = await CreateContextAsync();
        var aggregateId = Guid.NewGuid();

        await context.Store.AppendEventsAsync([CreateEntry(aggregateId, 1)]);
        await context.Store.AppendEventsAsync([CreateEntry(aggregateId, 2)]);

        var firstIntegrity = ReadIntegrity(await context.FindEventAsync(aggregateId, 1));
        var secondIntegrity = ReadIntegrity(await context.FindEventAsync(aggregateId, 2));
        secondIntegrity.PreviousHash.Should().Equal(firstIntegrity.Hash);
        (await context.CreateVerifier().VerifyStreamAsync(aggregateId)).IsIntact.Should().BeTrue();
    }

    [Test]
    public async Task AppendEventsAsync_MissingPredecessor_ThrowsMongoEventStoreExceptionAndPersistsNothing()
    {
        var context = await CreateContextAsync();
        var aggregateId = Guid.NewGuid();
        await context.Store.AppendEventsAsync([CreateEntry(aggregateId, 1), CreateEntry(aggregateId, 2), CreateEntry(aggregateId, 3)]);
        await context.Events.DeleteOneAsync(EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, 3));

        var act = () => context.Store.AppendEventsAsync([CreateEntry(aggregateId, 4)]);

        await act.Should().ThrowAsync<MongoEventStoreException>().WithMessage("*Version 3*missing*");
        (await context.Events.CountDocumentsAsync(EventDocumentFields<LedgerAggregate>.ForAggregate<BsonDocument>(aggregateId)))
            .Should().Be(2);
        var readModel = await context.MongoHelper.Database
                                     .GetCollection<LedgerAggregate>(context.Options.ReadModelCollectionName)
                                     .Find(a => a.Id == aggregateId)
                                     .SingleAsync();
        readModel.Version.Should().Be(3);
    }

    [Test]
    public async Task AppendEventsAsync_SubMillisecondCreatedUtc_StoredBytesRecomputeToWrittenHash()
    {
        var context = await CreateContextAsync();
        var aggregateId = Guid.NewGuid();
        var entry = CreateEntry(aggregateId, 1, createdUtc: FixedCreatedUtc);

        await context.Store.AppendEventsAsync([entry]);

        var storedBytes = await ReadRawEventAsync(context, aggregateId, 1);
        var stored = BsonSerializer.Deserialize<BsonDocument>(storedBytes);
        stored["Counter"].BsonType.Should().Be(BsonType.Int64);
        stored["Reference"].AsBsonBinaryData.SubType.Should().Be(BsonBinarySubType.UuidStandard);
        stored["Details"].BsonType.Should().Be(BsonType.Document);
        entry.CreatedUtc.Ticks.Should().Be(FixedCreatedUtc.Ticks, "the event store must not truncate CreatedUtc");

        var canonical = RawBsonElements.RemoveTopLevelElement(storedBytes, EventIntegrityChain.ElementName);
        var recomputedHash = EventIntegrityChain.ComputeHash(
            canonical,
            EventIntegrityChain.ComputeGenesis(nameof(LedgerAggregate), aggregateId));

        entry.Integrity.Should().NotBeNull();
        recomputedHash.Should().Equal(entry.Integrity.Hash);
        recomputedHash.Should().Equal(ReadIntegrity(stored).Hash);
    }

    [Test]
    public async Task AppendEventsAsync_UnsealedPredecessor_ThrowsMongoEventStoreException()
    {
        var databaseName = $"IntegrityTestDb_{Guid.NewGuid():N}";
        var unprotected = await CreateContextAsync(false, databaseName: databaseName);
        var protectedContext = await CreateContextAsync(databaseName: databaseName);
        var aggregateId = Guid.NewGuid();
        await unprotected.Store.AppendEventsAsync([CreateEntry(aggregateId, 1)]);

        var act = () => protectedContext.Store.AppendEventsAsync([CreateEntry(aggregateId, 2)]);

        await act.Should().ThrowAsync<MongoEventStoreException>().WithMessage("*Version 1*not sealed*");
        (await protectedContext.Events.CountDocumentsAsync(EventDocumentFields<LedgerAggregate>.ForAggregate<BsonDocument>(aggregateId)))
            .Should().Be(1);
    }

    [Test]
    public async Task AppendEventsAsync_WithoutIntegrityProtection_StoresSameBytesAsTypedInsert()
    {
        var context = await CreateContextAsync(false);
        var entry = CreateEntry(Guid.NewGuid(), 1, createdUtc: FixedCreatedUtc);
        await context.Store.AppendEventsAsync([entry]);

        // Typed insertion is how events were stored before; the server stores _id first in both cases.
        var typedCollection = context.MongoHelper.Database.GetCollection<Event<LedgerAggregate>>("TypedInsertReference");
        await typedCollection.InsertOneAsync(entry);
        using var typedDocument = await context.MongoHelper.Database
                                               .GetCollection<RawBsonDocument>("TypedInsertReference")
                                               .Find(FilterDefinition<RawBsonDocument>.Empty)
                                               .SingleAsync();

        var storedBytes = await ReadRawEventAsync(context, entry.AggregateId, 1);
        storedBytes.Should().Equal(typedDocument.ToBson());
        BsonSerializer.Deserialize<BsonDocument>(storedBytes).Contains(EventIntegrityChain.ElementName).Should().BeFalse();
    }

    [OneTimeSetUp]
    public async Task GetMongoDbContainer() => _container = await MongoDbTestContainer.StartContainerAsync();

    [TearDown]
    public async Task TearDown()
    {
        foreach (var serviceProvider in _serviceProviders)
        {
            await serviceProvider.DisposeAsync();
        }

        _serviceProviders.Clear();
    }

    [Test]
    public async Task VerifyStreamAsync_DeletedEvent_ReportsVersionGap()
    {
        var context = await CreateContextAsync();
        var aggregateId = await AppendStreamAsync(context);
        await context.Events.DeleteOneAsync(EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, 2));

        var result = await context.CreateVerifier().VerifyStreamAsync(aggregateId);

        result.Should().Be(StreamVerificationResult.Broken(2, StreamVerificationFailure.VersionGap));
    }

    [Test]
    public async Task VerifyStreamAsync_InsertedEventWithRecomputedChain_ReportsShiftedSuccessor()
    {
        var context = await CreateContextAsync();
        var aggregateId = await AppendStreamAsync(context);
        await context.SetVersionAsync(aggregateId, 3, 4);

        var forged = await context.FindEventAsync(aggregateId, 2);
        var predecessorHash = ReadIntegrity(forged).Hash;
        forged.Remove(EventIntegrityChain.ElementName);
        forged["_id"] = new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard);
        forged["Version"] = 3L;
        var forgedIntegrity = EventIntegrityChain.Seal(forged.ToBson(), predecessorHash, IntegritySealMode.Append);
        forged.Add(EventIntegrityChain.ElementName, forgedIntegrity.ToBsonDocument());
        await context.Events.InsertOneAsync(forged);

        var result = await context.CreateVerifier().VerifyStreamAsync(aggregateId);

        result.Should().Be(StreamVerificationResult.Broken(4, StreamVerificationFailure.PreviousHashMismatch));
    }

    [Test]
    public async Task VerifyStreamAsync_IntactStream_ReportsIntact()
    {
        var context = await CreateContextAsync();
        var aggregateId = await AppendStreamAsync(context);

        var result = await context.CreateVerifier().VerifyStreamAsync(aggregateId);

        result.Should().Be(StreamVerificationResult.Intact);
        result.IsIntact.Should().BeTrue();
    }

    [Test]
    public async Task VerifyStreamAsync_ModifiedEvent_ReportsHashMismatch()
    {
        var context = await CreateContextAsync();
        var aggregateId = await AppendStreamAsync(context);
        await context.Events.UpdateOneAsync(
            EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, 2),
            Builders<BsonDocument>.Update.Set("Counter", 43L));

        var result = await context.CreateVerifier().VerifyStreamAsync(aggregateId);

        result.Should().Be(StreamVerificationResult.Broken(2, StreamVerificationFailure.HashMismatch));
    }

    [Test]
    public async Task VerifyStreamAsync_ReorderedEvents_ReportsFirstSwappedVersion()
    {
        var context = await CreateContextAsync();
        var aggregateId = await AppendStreamAsync(context);
        await context.SetVersionAsync(aggregateId, 2, 100);
        await context.SetVersionAsync(aggregateId, 3, 2);
        await context.SetVersionAsync(aggregateId, 100, 3);

        var result = await context.CreateVerifier().VerifyStreamAsync(aggregateId);

        result.Should().Be(StreamVerificationResult.Broken(2, StreamVerificationFailure.PreviousHashMismatch));
    }

    [Test]
    public async Task VerifyStreamAsync_UnsealedEvent_ReportsNotSealed()
    {
        var context = await CreateContextAsync(false);
        var aggregateId = Guid.NewGuid();
        await context.Store.AppendEventsAsync([CreateEntry(aggregateId, 1)]);

        var result = await context.CreateVerifier().VerifyStreamAsync(aggregateId);

        result.Should().Be(StreamVerificationResult.Broken(1, StreamVerificationFailure.NotSealed));
    }

    [Test]
    public async Task VerifyStreamAsync_UnsupportedFormatVersion_ReportsUnsupportedFormat()
    {
        var context = await CreateContextAsync();
        var aggregateId = await AppendStreamAsync(context);
        await context.Events.UpdateOneAsync(
            EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, 1),
            Builders<BsonDocument>.Update.Set($"{EventIntegrityChain.ElementName}.FormatVersion", 2));

        var result = await context.CreateVerifier().VerifyStreamAsync(aggregateId);

        result.Should().Be(StreamVerificationResult.Broken(1, StreamVerificationFailure.UnsupportedFormat));
    }

    private static async Task AppendWithRetryAsync(LedgerStoreContext context, Guid aggregateId)
    {
        while (true)
        {
            var version = await context.Store.GetExpectedNextVersionAsync(aggregateId);
            try
            {
                await context.Store.AppendEventsAsync([CreateEntry(aggregateId, version)]);
                return;
            }
            catch (MongoConcurrencyException)
            {
                // Another writer committed this version first; retry with the next one.
            }
        }
    }

    private static LedgerEntryRecordedEvent CreateEntry(Guid aggregateId, Int64 version, Guid? id = null, DateTime createdUtc = default)
        => new()
        {
            Id = id ?? Guid.NewGuid(),
            AggregateId = aggregateId,
            Version = version,
            CreatedUtc = createdUtc,
            Amount = 12.34m + version,
            Counter = 42,
            Reference = FixedReference,
            Details = new LedgerEntryDetails
            {
                Category = "fees",
                Ratio = 0.25,
                Tags = ["monthly", "recurring"]
            }
        };

    private static EventIntegrity ReadIntegrity(BsonDocument document)
        => BsonSerializer.Deserialize<EventIntegrity>(document[EventIntegrityChain.ElementName].AsBsonDocument);

    private static async Task<Byte[]> ReadRawEventAsync(LedgerStoreContext context, Guid aggregateId, Int64 version)
    {
        using var document = await context.MongoHelper.Database
                                          .GetCollection<RawBsonDocument>(context.Options.EventsCollectionName)
                                          .Find(EventDocumentFields<LedgerAggregate>.ForVersion<RawBsonDocument>(aggregateId, version))
                                          .SingleAsync();
        return document.ToBson();
    }

    private async Task<Guid> AppendStreamAsync(LedgerStoreContext context)
    {
        var aggregateId = Guid.NewGuid();
        await context.Store.AppendEventsAsync([CreateEntry(aggregateId, 1), CreateEntry(aggregateId, 2), CreateEntry(aggregateId, 3)]);
        return aggregateId;
    }

    private async Task<LedgerStoreContext> CreateContextAsync(Boolean integrityProtection = true,
                                                              Boolean bulkWrite = false,
                                                              String? databaseName = null)
    {
        var serviceProvider = new ServiceCollection()
                              .AddMongo(MongoUrl.Create(_container.GetConnectionString()), configure: options =>
                              {
                                  options.DefaultDatabase = databaseName ?? $"IntegrityTestDb_{Guid.NewGuid():N}";
                                  options.RunConfiguratorsOnStartup = false;
                              })
                              .WithEventStore<LedgerAggregate>(es =>
                              {
                                  es.WithEvent<LedgerEntryRecordedEvent>("LedgerEntryRecorded")
                                    .WithCollectionPrefix("Ledgers");

                                  if (integrityProtection)
                                  {
                                      es.WithIntegrityProtection();
                                  }

                                  if (bulkWrite)
                                  {
                                      es.WithBulkWriteOptimization();
                                  }
                              })
                              .Services
                              .BuildServiceProvider();
        _serviceProviders.Add(serviceProvider);

        var mongoHelper = serviceProvider.GetRequiredService<IMongoHelper>();
        foreach (var configurator in serviceProvider.GetServices<IMongoConfigurator>())
        {
            await configurator.ConfigureAsync(mongoHelper);
        }

        return new LedgerStoreContext(
            serviceProvider.GetRequiredService<IEventStore<LedgerAggregate>>(),
            mongoHelper,
            serviceProvider.GetRequiredService<MongoEventStoreOptions<LedgerAggregate>>());
    }

    private sealed record LedgerStoreContext(
        IEventStore<LedgerAggregate> Store,
        IMongoHelper MongoHelper,
        MongoEventStoreOptions<LedgerAggregate> Options)
    {
        public IMongoCollection<BsonDocument> Events
            => MongoHelper.Database.GetCollection<BsonDocument>(Options.EventsCollectionName);

        public EventStreamVerifier<LedgerAggregate> CreateVerifier()
            => new(MongoHelper, Options);

        public Task<BsonDocument> FindEventAsync(Guid aggregateId, Int64 version)
            => Events.Find(EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, version)).SingleAsync();

        public Task SetVersionAsync(Guid aggregateId, Int64 version, Int64 newVersion)
            => Events.UpdateOneAsync(
                EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, version),
                Builders<BsonDocument>.Update.Set("Version", newVersion));
    }
}
