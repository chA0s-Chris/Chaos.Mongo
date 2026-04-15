// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Benchmarks;

using BenchmarkDotNet.Attributes;
using Chaos.Mongo.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Testcontainers.MongoDb;

[MemoryDiagnoser]
public class EventStoreAppendBenchmarks
{
    private static readonly BenchmarkScenario[] _scenarioMatrix =
    [
        new("SingleEventNoCheckpoint", 1),
        new("MediumBatchNoCheckpoint", 10),
        new("CheckpointForcingBatch", 50, 1)
    ];
    private const Int32 OperationsPerBenchmarkInvocation = 32;
    private BenchmarkContext _baseline = null!;
    private Int32 _baselineInvocation;
    private MongoDbContainer _container = null!;
    private BenchmarkContext _optimized = null!;
    private Int32 _optimizedInvocation;

    [ParamsSource(nameof(Scenarios))]
    public BenchmarkScenario Scenario { get; set; } = null!;

    public IEnumerable<BenchmarkScenario> Scenarios => _scenarioMatrix;

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
                     .Build();

        _container.StartAsync().GetAwaiter().GetResult();
        _baselineInvocation = 0;
        _optimizedInvocation = 0;

        _baseline = CreateContextAsync("baseline", false).GetAwaiter().GetResult();
        _optimized = CreateContextAsync("optimized", true).GetAwaiter().GetResult();

        WarmupAsync(_baseline.Store).GetAwaiter().GetResult();
        WarmupAsync(_optimized.Store).GetAwaiter().GetResult();
    }

    private static async Task DisposeContextAsync(BenchmarkContext context)
    {
        if (context is null)
        {
            return;
        }

        await context.MongoHelper.Client.DropDatabaseAsync(context.DatabaseName);
        await context.ServiceProvider.DisposeAsync();
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
        var aggregateId = Guid.CreateVersion7();
        return await context.Store.AppendEventsAsync(CreateEvents(aggregateId, invocation));
    }

    private async Task<BenchmarkContext> CreateContextAsync(String scenario, Boolean bulkWriteOptimizationEnabled)
    {
        var databaseName = $"EventStoreBenchmark_{scenario}_{Guid.NewGuid():N}";
        var services = new ServiceCollection()
                       .AddMongo(
                           MongoUrl.Create(_container.GetConnectionString()),
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
                       })
                       .Services
                       .BuildServiceProvider();

        var mongoHelper = services.GetRequiredService<IMongoHelper>();
        foreach (var configurator in services.GetServices<IMongoConfigurator>())
        {
            await configurator.ConfigureAsync(mongoHelper);
        }

        return new(
            services,
            services.GetRequiredService<IEventStore<BenchmarkOrderAggregate>>(),
            mongoHelper,
            databaseName);
    }

    private IReadOnlyList<Event<BenchmarkOrderAggregate>> CreateEvents(Guid aggregateId, Int32 invocationCount)
    {
        var events = new List<Event<BenchmarkOrderAggregate>>(Scenario.EventCount);

        for (var version = 1; version <= Scenario.EventCount; version++)
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
        String DatabaseName);

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
}
