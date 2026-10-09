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

    public IMongoCollection<BsonDocument> Events
        => MongoHelper.Database.GetCollection<BsonDocument>(Options.EventsCollectionName);

    public IMongoHelper MongoHelper { get; }

    public MongoEventStoreOptions<LedgerAggregate> Options { get; }

    public ServiceProvider ServiceProvider { get; }

    public IEventStore<LedgerAggregate> Store { get; }

    public EventStreamSealingSweep<LedgerAggregate> Sweep
        => ServiceProvider.GetRequiredService<EventStreamSealingSweep<LedgerAggregate>>();

    public static async Task<LedgerStoreContext> CreateAsync(MongoDbContainer container,
                                                             Boolean integrityProtection = true,
                                                             Boolean bulkWrite = false,
                                                             String? databaseName = null,
                                                             Action<MongoEventStoreOptions<LedgerAggregate>>? configureOptions = null,
                                                             TimeProvider? timeProvider = null)
    {
        var services = new ServiceCollection();
        if (timeProvider is not null)
        {
            services.AddSingleton(timeProvider);
        }

        var serviceProvider = services.AddMongo(MongoUrl.Create(container.GetConnectionString()), configure: options =>
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

                                          configureOptions?.Invoke(es.Options);
                                      })
                                      .Services
                                      .BuildServiceProvider();

        var context = new LedgerStoreContext(serviceProvider);
        foreach (var configurator in serviceProvider.GetServices<IMongoConfigurator>())
        {
            await configurator.ConfigureAsync(context.MongoHelper);
        }

        return context;
    }

    public static LedgerEntryRecordedEvent CreateEntry(Guid aggregateId,
                                                       Int64 version,
                                                       Guid? id = null,
                                                       DateTime createdUtc = default)
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

    public EventStreamVerifier<LedgerAggregate> CreateVerifier()
        => new(MongoHelper, Options);

    public Task<BsonDocument> FindEventAsync(Guid aggregateId, Int64 version)
        => Events.Find(EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, version)).SingleAsync();

    public async Task<Byte[]> ReadRawEventAsync(Guid aggregateId, Int64 version)
    {
        using var document = await MongoHelper.Database
                                              .GetCollection<RawBsonDocument>(Options.EventsCollectionName)
                                              .Find(EventDocumentFields<LedgerAggregate>.ForVersion<RawBsonDocument>(aggregateId, version))
                                              .SingleAsync();
        return document.ToBson();
    }

    public Task SetVersionAsync(Guid aggregateId, Int64 version, Int64 newVersion)
        => Events.UpdateOneAsync(
            EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, version),
            Builders<BsonDocument>.Update.Set("Version", newVersion));

    public ValueTask DisposeAsync() => ServiceProvider.DisposeAsync();
}
