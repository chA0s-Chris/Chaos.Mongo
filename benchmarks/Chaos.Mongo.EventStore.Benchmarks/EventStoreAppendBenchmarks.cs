// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Benchmarks;

using BenchmarkDotNet.Attributes;
using Chaos.Mongo.Configuration;
using Docker.DotNet.Models;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Testcontainers.MongoDb;

[MemoryDiagnoser]
public class EventStoreAppendBenchmarks
{
    private const Int32 ExistingStreamPoolSize = 256;
    private const Int32 OperationsPerBenchmarkInvocation = 32;

    private static readonly BenchmarkScenario[] ScenarioMatrix =
    [
        new("SingleEventNoCheckpoint", 1),
        new("MediumBatchNoCheckpoint", 10),
        new("CheckpointForcingBatch", 50, 1)
    ];

    private BenchmarkContext _baseline = null!;
    private Int32 _baselineInvocation;
    private MongoDbContainer? _container;
    private BenchmarkContext _optimized = null!;
    private Int32 _optimizedInvocation;

    /// <summary>
    /// Gets or sets a value indicating whether appends target pre-seeded streams (version &gt; 1)
    /// instead of new aggregates. Only appends to existing streams read their predecessor.
    /// </summary>
    [Params(false, true)]
    public Boolean ExistingStream { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether appended events are sealed into a hash chain.
    /// </summary>
    [Params(false, true)]
    public Boolean IntegrityProtection { get; set; }

    [ParamsSource(nameof(Scenarios))]
    public BenchmarkScenario Scenario { get; set; } = null!;

    public IEnumerable<BenchmarkScenario> Scenarios => ScenarioMatrix;

    [Benchmark(OperationsPerInvoke = OperationsPerBenchmarkInvocation)]
    public async Task AppendWithBulkWrite()
    {
        for (var operation = 0; operation < OperationsPerBenchmarkInvocation; operation++)
        {
            _ = await AppendAsync(_optimized, Interlocked.Increment(ref _optimizedInvocation));
        }
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = OperationsPerBenchmarkInvocation)]
    public async Task AppendWithoutBulkWrite()
    {
        for (var operation = 0; operation < OperationsPerBenchmarkInvocation; operation++)
        {
            _ = await AppendAsync(_baseline, Interlocked.Increment(ref _baselineInvocation));
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        DisposeContextAsync(_baseline).GetAwaiter().GetResult();
        DisposeContextAsync(_optimized).GetAwaiter().GetResult();

        if (_container is not null)
        {
            _container.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }


    [GlobalSetup]
    public void GlobalSetup()
    {
        _container = new MongoDbBuilder("mongo:8")
                     .WithReplicaSet()
                     // MongoDB 8.x images crash on Linux kernels newer than 6.19 unless rseq is
                     // pinned. See https://jira.mongodb.org/browse/SERVER-121912
                     .WithEnvironment("GLIBC_TUNABLES", "glibc.pthread.rseq=1")
                     .WithCreateParameterModifier(parameters =>
                     {
                         parameters.HostConfig ??= new HostConfig();
                         parameters.HostConfig.Ulimits =
                         [
                             new Ulimit
                             {
                                 Name = "nofile",
                                 Soft = 65536,
                                 Hard = 65536
                             }
                         ];
                     })
                     .Build();

        _container.StartAsync().GetAwaiter().GetResult();
        _baselineInvocation = 0;
        _optimizedInvocation = 0;

        _baseline = CreateContextAsync("baseline", false).GetAwaiter().GetResult();
        _optimized = CreateContextAsync("optimized", true).GetAwaiter().GetResult();

        WarmupAsync(_baseline.Store).GetAwaiter().GetResult();
        WarmupAsync(_optimized.Store).GetAwaiter().GetResult();

        if (ExistingStream)
        {
            SeedExistingStreamsAsync(_baseline).GetAwaiter().GetResult();
            SeedExistingStreamsAsync(_optimized).GetAwaiter().GetResult();
        }
    }

    private static async Task DisposeContextAsync(BenchmarkContext? context)
    {
        if (context is null)
        {
            return;
        }

        await context.MongoHelper.Client.DropDatabaseAsync(context.DatabaseName);
        await context.ServiceProvider.DisposeAsync();
    }

    private static async Task SeedExistingStreamsAsync(BenchmarkContext context)
    {
        for (var index = 0; index < ExistingStreamPoolSize; index++)
        {
            var stream = new ExistingStreamSlot(Guid.CreateVersion7());
            await context.Store.AppendEventsAsync(
            [
                new BenchmarkOrderAdjustedEvent
                {
                    Id = Guid.CreateVersion7(),
                    AggregateId = stream.AggregateId,
                    Version = 1,
                    AmountDelta = 1
                }
            ]);
            stream.NextVersion = 2;
            context.ExistingStreams.Add(stream);
        }
    }

    private static async Task WarmupAsync(IEventStore<BenchmarkOrderAggregate> eventStore)
    {
        var aggregateId = Guid.CreateVersion7();
        await eventStore.AppendEventsAsync(
        [
            new BenchmarkOrderAdjustedEvent
            {
                Id = Guid.CreateVersion7(),
                AggregateId = aggregateId,
                Version = 1,
                AmountDelta = 1
            }
        ]);
    }

    private async Task<BenchmarkOrderAggregate> AppendAsync(BenchmarkContext context, Int32 invocation)
    {
        if (!ExistingStream)
        {
            return await context.Store.AppendEventsAsync(CreateEvents(Guid.CreateVersion7(), 1, invocation));
        }

        // Round-robin over pre-seeded streams, so every append reads its predecessor.
        var stream = context.ExistingStreams[invocation % context.ExistingStreams.Count];
        var events = CreateEvents(stream.AggregateId, stream.NextVersion, invocation);
        stream.NextVersion += events.Count;
        return await context.Store.AppendEventsAsync(events);
    }

    private async Task<BenchmarkContext> CreateContextAsync(String scenario, Boolean bulkWriteOptimizationEnabled)
    {
        var container = _container ?? throw new InvalidOperationException("The MongoDB container has not been initialized.");
        var databaseName = $"EventStoreBenchmark_{scenario}_{Guid.NewGuid():N}";
        var services = new ServiceCollection()
                       .AddMongo(
                           MongoUrl.Create(container.GetConnectionString()),
                           configure: options =>
                           {
                               options.DefaultDatabase = databaseName;
                               options.RunConfiguratorsOnStartup = false;
                           })
                       .WithEventStore<BenchmarkOrderAggregate>(builder =>
                       {
                           builder.WithEvent<BenchmarkOrderAdjustedEvent>("OrderAdjusted")
                                  .WithCollectionPrefix("Orders");

                           if (Scenario.CheckpointInterval is { } checkpointInterval)
                           {
                               builder.WithCheckpoints(checkpointInterval);
                           }

                           if (bulkWriteOptimizationEnabled)
                           {
                               builder.WithBulkWriteOptimization();
                           }

                           if (IntegrityProtection)
                           {
                               builder.WithIntegrityProtection();
                           }
                       })
                       .Services
                       .BuildServiceProvider();

        var mongoHelper = services.GetRequiredService<IMongoHelper>();
        foreach (var configurator in services.GetServices<IMongoConfigurator>())
        {
            await configurator.ConfigureAsync(mongoHelper);
        }

        return new BenchmarkContext(
            services,
            services.GetRequiredService<IEventStore<BenchmarkOrderAggregate>>(),
            mongoHelper,
            databaseName);
    }

    private IReadOnlyList<Event<BenchmarkOrderAggregate>> CreateEvents(Guid aggregateId, Int64 firstVersion, Int32 invocationCount)
    {
        var events = new List<Event<BenchmarkOrderAggregate>>(Scenario.EventCount);

        for (var version = firstVersion; version < firstVersion + Scenario.EventCount; version++)
        {
            events.Add(new BenchmarkOrderAdjustedEvent
            {
                Id = Guid.CreateVersion7(),
                AggregateId = aggregateId,
                Version = version,
                AmountDelta = invocationCount + version
            });
        }

        return events;
    }

    public sealed record BenchmarkScenario(String Name, Int32 EventCount, Int32? CheckpointInterval = null)
    {
        public override String ToString()
            => Name;
    }

    private sealed record BenchmarkContext(
        ServiceProvider ServiceProvider,
        IEventStore<BenchmarkOrderAggregate> Store,
        IMongoHelper MongoHelper,
        String DatabaseName)
    {
        public List<ExistingStreamSlot> ExistingStreams { get; } = [];
    }

    private sealed class BenchmarkOrderAdjustedEvent : Event<BenchmarkOrderAggregate>
    {
        public Decimal AmountDelta { get; set; }

        public override void Execute(BenchmarkOrderAggregate aggregate)
            => aggregate.TotalAmount += AmountDelta;
    }

    private sealed class BenchmarkOrderAggregate : Aggregate
    {
        public Decimal TotalAmount { get; set; }
    }

    private sealed class ExistingStreamSlot
    {
        public ExistingStreamSlot(Guid aggregateId)
        {
            AggregateId = aggregateId;
        }

        public Guid AggregateId { get; }

        public Int64 NextVersion { get; set; }
    }
}
