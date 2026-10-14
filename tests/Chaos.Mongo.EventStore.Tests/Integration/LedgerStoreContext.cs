// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Chaos.Mongo.Configuration;
using Chaos.Mongo.EventStore.Integrity;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Testcontainers.MongoDb;

/// <summary>
/// A ledger event store wired against the shared test container, plus raw access to its collections.
/// </summary>
internal sealed class LedgerStoreContext : IAsyncDisposable
{
    private static readonly Guid FixedReference = Guid.Parse("8b4d4c1e-9e3f-4c55-a1a4-0f1e6c2d7b90");

    private LedgerStoreContext(ServiceProvider serviceProvider)
    {
        ServiceProvider = serviceProvider;
        MongoHelper = serviceProvider.GetRequiredService<IMongoHelper>();
        Options = serviceProvider.GetRequiredService<MongoEventStoreOptions<LedgerAggregate>>();
        Store = serviceProvider.GetRequiredService<IEventStore<LedgerAggregate>>();
    }

    public IAnchorCandidateSource CandidateSource
        => ServiceProvider.GetRequiredService<IAnchorCandidateSource>();

    public PersonalDataEraser<LedgerAggregate> Eraser
        => ServiceProvider.GetRequiredService<PersonalDataEraser<LedgerAggregate>>();

    public IMongoCollection<BsonDocument> Events
        => MongoHelper.Database.GetCollection<BsonDocument>(Options.EventsCollectionName);

    public IMongoHelper MongoHelper { get; }

    public MongoEventStoreOptions<LedgerAggregate> Options { get; }

    public IAggregateRepository<LedgerAggregate> Repository
        => ServiceProvider.GetRequiredService<IAggregateRepository<LedgerAggregate>>();

    public ServiceProvider ServiceProvider { get; }

    public IEventStore<LedgerAggregate> Store { get; }

    public EventStreamSealingSweep<LedgerAggregate> Sweep
        => ServiceProvider.GetRequiredService<EventStreamSealingSweep<LedgerAggregate>>();

    /// <summary>
    /// Registers a protected shipment event store next to the ledger store.
    /// </summary>
    public static void AddShipmentStore(MongoBuilder builder)
        => builder.WithEventStore<ShipmentAggregate>(es => es.WithEvent<ShipmentDispatchedEvent>("ShipmentDispatched")
                                                             .WithCollectionPrefix("Shipments")
                                                             .WithIntegrityProtection());

    public static async Task<LedgerStoreContext> CreateAsync(MongoDbContainer container,
                                                             Boolean integrityProtection = true,
                                                             Boolean bulkWrite = false,
                                                             String? databaseName = null,
                                                             Action<MongoEventStoreOptions<LedgerAggregate>>? configureOptions = null,
                                                             TimeProvider? timeProvider = null,
                                                             Boolean anchorCandidates = false,
                                                             String? applicationName = null,
                                                             Action<MongoBuilder>? configureMongo = null,
                                                             Boolean personalData = false)
    {
        var services = new ServiceCollection();
        if (timeProvider is not null)
        {
            services.AddSingleton(timeProvider);
        }

        var url = new MongoUrlBuilder(container.GetConnectionString())
        {
            ApplicationName = applicationName
        }.ToMongoUrl();
        var mongoBuilder = services.AddMongo(url, configure: options =>
                                   {
                                       options.DefaultDatabase = databaseName ?? $"IntegrityTestDb_{Guid.NewGuid():N}";
                                       options.RunConfiguratorsOnStartup = false;
                                   })
                                   .WithEventStore<LedgerAggregate>(es =>
                                   {
                                       if (personalData)
                                       {
                                           es.WithEvent<LedgerEntryRecordedEvent>(
                                               "LedgerEntryRecorded",
                                               e => e.WithPersonalData(x => x.Memo).WithPersonalData(x => x.Counterparty.Name));
                                       }
                                       else
                                       {
                                           es.WithEvent<LedgerEntryRecordedEvent>("LedgerEntryRecorded");
                                       }

                                       es.WithEvent<LedgerAuditedEvent>("LedgerAudited")
                                         .WithCollectionPrefix("Ledgers");

                                       if (integrityProtection)
                                       {
                                           es.WithIntegrityProtection();
                                       }

                                       if (bulkWrite)
                                       {
                                           es.WithBulkWriteOptimization();
                                       }

                                       configureOptions?.Invoke(es.Options);
                                   });

        configureMongo?.Invoke(mongoBuilder);
        if (anchorCandidates)
        {
            mongoBuilder.WithAnchorCandidates();
        }

        var serviceProvider = mongoBuilder.Services.BuildServiceProvider();

        var context = new LedgerStoreContext(serviceProvider);
        foreach (var configurator in serviceProvider.GetServices<IMongoConfigurator>())
        {
            await configurator.ConfigureAsync(context.MongoHelper);
        }

        return context;
    }

    public static LedgerAuditedEvent CreateAudit(Guid aggregateId,
                                                 Int64 version,
                                                 Guid? id = null,
                                                 DateTime createdUtc = default)
        => new()
        {
            Id = id ?? Guid.NewGuid(),
            AggregateId = aggregateId,
            Version = version,
            CreatedUtc = createdUtc,
            Auditor = "auditor"
        };

    public static LedgerEntryRecordedEvent CreateEntry(Guid aggregateId,
                                                       Int64 version,
                                                       Guid? id = null,
                                                       DateTime createdUtc = default,
                                                       String? memo = "monthly fee")
        => new()
        {
            Id = id ?? Guid.NewGuid(),
            AggregateId = aggregateId,
            Version = version,
            CreatedUtc = createdUtc,
            Amount = 12.34m + version,
            Counter = 42,
            Counterparty = new LedgerCounterparty
            {
                Name = "Alice Example",
                Account = "DE00 1234 5678"
            },
            Memo = memo,
            Reference = FixedReference,
            Details = new LedgerEntryDetails
            {
                Category = "fees",
                Ratio = 0.25,
                Tags = ["monthly", "recurring"]
            }
        };

    public static EventIntegrity ReadIntegrity(BsonDocument document)
        => BsonSerializer.Deserialize<EventIntegrity>(document[EventIntegrityChain.ElementName].AsBsonDocument);

    public async Task<Guid> AppendStreamAsync(Int32 eventCount)
    {
        var aggregateId = Guid.NewGuid();
        await Store.AppendEventsAsync(Enumerable.Range(1, eventCount).Select(version => CreateEntry(aggregateId, version)));
        return aggregateId;
    }

    public Task<Int64> CountEventsAsync(Guid aggregateId)
        => Events.CountDocumentsAsync(EventDocumentFields<LedgerAggregate>.ForAggregate<BsonDocument>(aggregateId));

    public AnchorCandidateConsumer CreateConsumer()
        => new(MongoHelper, CandidateSource);

    public EventStreamVerifier<LedgerAggregate> CreateVerifier()
        => new(MongoHelper, Options);

    public Task<BsonDocument> FindEventAsync(Guid aggregateId, Int64 version)
        => Events.Find(EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, version)).SingleAsync();

    public Task<BsonDocument> FindShipmentEventAsync(Guid aggregateId, Int64 version)
        => MongoHelper.Database
                      .GetCollection<BsonDocument>(ServiceProvider.GetRequiredService<MongoEventStoreOptions<ShipmentAggregate>>().EventsCollectionName)
                      .Find(EventDocumentFields<ShipmentAggregate>.ForVersion<BsonDocument>(aggregateId, version))
                      .SingleAsync();

    public Task<List<BsonDocument>> ReadRawCheckpointsAsync(Guid aggregateId)
        => MongoHelper.Database
                      .GetCollection<BsonDocument>(Options.CheckpointCollectionName)
                      .Find(Builders<BsonDocument>.Filter.Eq("_id.AggregateId", new BsonBinaryData(aggregateId, GuidRepresentation.Standard)))
                      .Sort(Builders<BsonDocument>.Sort.Ascending("_id.Version"))
                      .ToListAsync();

    public async Task<Byte[]> ReadRawEventAsync(Guid aggregateId, Int64 version)
    {
        using var document = await MongoHelper.Database
                                              .GetCollection<RawBsonDocument>(Options.EventsCollectionName)
                                              .Find(EventDocumentFields<LedgerAggregate>.ForVersion<RawBsonDocument>(aggregateId, version))
                                              .SingleAsync();
        return document.ToBson();
    }

    public Task<BsonDocument> ReadRawReadModelAsync(Guid aggregateId)
        => MongoHelper.Database
                      .GetCollection<BsonDocument>(Options.ReadModelCollectionName)
                      .Find(Builders<BsonDocument>.Filter.Eq("_id", new BsonBinaryData(aggregateId, GuidRepresentation.Standard)))
                      .SingleAsync();

    public Task SetVersionAsync(Guid aggregateId, Int64 version, Int64 newVersion)
        => Events.UpdateOneAsync(
            EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, version),
            Builders<BsonDocument>.Update.Set("Version", newVersion));

    /// <summary>
    /// Removes the salt of one recorded personal data path, as an erasure does.
    /// </summary>
    public Task UnsetSaltAsync(Guid aggregateId, Int64 version, String path)
        => Events.UpdateOneAsync(
            EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, version),
            Builders<BsonDocument>.Update.Unset($"{EventIntegrityChain.ElementName}.PersonalData.$[entry].Salt"),
            new UpdateOptions
            {
                ArrayFilters = [new BsonDocumentArrayFilterDefinition<BsonDocument>(new BsonDocument("entry.Path", path))]
            });

    public ValueTask DisposeAsync() => ServiceProvider.DisposeAsync();
}
