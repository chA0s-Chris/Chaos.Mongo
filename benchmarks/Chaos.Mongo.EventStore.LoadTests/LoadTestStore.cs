// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.LoadTests;

using Chaos.Mongo.Configuration;
using Chaos.Mongo.EventStore.Integrity;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

/// <summary>
/// An event store in its own database, configured for one load-test mode and append path.
/// </summary>
internal sealed class LoadTestStore : IAsyncDisposable
{
    public const Int32 ConsumerBatchSize = 1000;

    private CancellationTokenSource? _consumerCancellation;
    private Task? _consumerLoop;

    private LoadTestStore(ServiceProvider serviceProvider, String databaseName)
    {
        ServiceProvider = serviceProvider;
        DatabaseName = databaseName;
        MongoHelper = serviceProvider.GetRequiredService<IMongoHelper>();
        Store = serviceProvider.GetRequiredService<IEventStore<LoadTestAggregate>>();
    }

    public IAnchorCandidateSource? CandidateSource => ServiceProvider.GetService<IAnchorCandidateSource>();

    public String DatabaseName { get; }

    public IMongoHelper MongoHelper { get; }

    public ServiceProvider ServiceProvider { get; }

    public IEventStore<LoadTestAggregate> Store { get; }

    public EventStreamSealingSweep<LoadTestAggregate> Sweep
        => ServiceProvider.GetRequiredService<EventStreamSealingSweep<LoadTestAggregate>>();

    public static async Task<LoadTestStore> CreateAsync(String connectionString,
                                                        LoadTestMode mode,
                                                        Boolean bulkWrite,
                                                        String? databaseName = null,
                                                        Boolean integrityProtection = true)
    {
        databaseName ??= $"EventStoreLoadTest_{Guid.NewGuid():N}";
        var mongoBuilder = new ServiceCollection()
                           .AddMongo(MongoUrl.Create(connectionString),
                                     configure: options =>
                                     {
                                         options.DefaultDatabase = databaseName;
                                         options.RunConfiguratorsOnStartup = false;
                                     })
                           .WithEventStore<LoadTestAggregate>(builder =>
                           {
                               builder.WithEvent<LoadTestAdjustedEvent>("Adjusted").WithCollectionPrefix("LoadTest");
                               if (integrityProtection)
                               {
                                   builder.WithIntegrityProtection();
                               }

                               if (bulkWrite)
                               {
                                   builder.WithBulkWriteOptimization();
                               }
                           });

        if (mode == LoadTestMode.ChainWithChangeStream)
        {
            mongoBuilder.WithAnchorCandidates();
        }

        var store = new LoadTestStore(mongoBuilder.Services.BuildServiceProvider(), databaseName);
        foreach (var configurator in store.ServiceProvider.GetServices<IMongoConfigurator>())
        {
            await configurator.ConfigureAsync(store.MongoHelper);
        }

        return store;
    }

    /// <summary>
    /// Reads and acknowledges batches until the given number of raw candidates has been read.
    /// </summary>
    public async Task<Int64> DrainAsync(Int64 expectedCandidates, TimeSpan timeout)
    {
        var source = CandidateSource ?? throw new InvalidOperationException("No candidate source is configured.");
        var deadline = DateTime.UtcNow + timeout;
        var read = 0L;
        while (read < expectedCandidates)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Only {read} of {expectedCandidates} candidates were read in time.");
            }

            var batch = await source.ReadBatchAsync(ConsumerBatchSize, CancellationToken.None);
            await MongoHelper.ExecuteInTransactionAsync((_, session, ct) => source.AcknowledgeAsync(session, batch, ct));
            read += batch.Candidates.Count;
        }

        return read;
    }

    /// <summary>
    /// Reads and acknowledges batches until the source has no more candidates right away.
    /// </summary>
    public async Task DrainUntilIdleAsync()
    {
        if (CandidateSource is { } source)
        {
            await new AnchorCandidateConsumer(MongoHelper, source).DrainAsync(ConsumerBatchSize);
        }
    }

    public async Task<List<Guid>> SeedStreamsAsync(Int32 count, Int32 eventsPerStream = 1)
    {
        var streams = Enumerable.Range(0, count).Select(_ => Guid.CreateVersion7()).ToList();
        await Parallel.ForEachAsync(streams,
                                    new ParallelOptions
                                    {
                                        MaxDegreeOfParallelism = 16
                                    },
                                    async (aggregateId, _) => await Store.AppendEventsAsync(
                                        Enumerable.Range(1, eventsPerStream)
                                                  .Select(version => LoadTestAdjustedEvent.Create(aggregateId, version))));
        return streams;
    }

    /// <summary>
    /// Starts the consumer of the candidate source in the background, after draining what already exists.
    /// </summary>
    public async Task StartConsumerAsync()
    {
        if (CandidateSource is not { } source)
        {
            return;
        }

        await DrainUntilIdleAsync();
        _consumerCancellation = new CancellationTokenSource();
        _consumerLoop = new AnchorCandidateConsumer(MongoHelper, source)
            .RunAsync(ConsumerBatchSize, TimeSpan.FromMilliseconds(10), _consumerCancellation.Token);
    }

    public async Task StopConsumerAsync()
    {
        if (_consumerCancellation is null || _consumerLoop is null)
        {
            return;
        }

        await _consumerCancellation.CancelAsync();
        await _consumerLoop;
        _consumerCancellation.Dispose();
        _consumerCancellation = null;
        _consumerLoop = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopConsumerAsync();
        await MongoHelper.Client.DropDatabaseAsync(DatabaseName);
        await ServiceProvider.DisposeAsync();
    }
}
