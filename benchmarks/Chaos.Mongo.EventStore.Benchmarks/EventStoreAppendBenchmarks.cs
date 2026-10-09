// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Benchmarks;

using BenchmarkDotNet.Attributes;
using Chaos.Mongo.Configuration;
using Docker.DotNet.Models;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Testcontainers.MongoDb;

/// <remarks>
/// Each iteration runs exactly one invocation, because <see cref="IterationSetup"/> seeds one fresh
/// stream per append of that invocation.
/// </remarks>
[MemoryDiagnoser]
[InvocationCount(1)]
public class EventStoreAppendBenchmarks
{
    private const Int32 OperationsPerBenchmarkInvocation = 32;

    private static readonly BenchmarkScenario[] ScenarioMatrix =
    [
        new("SingleEventNoCheckpoint", 1, false),
        new("SingleEventNoCheckpoint", 1, true),
        new("MediumBatchNoCheckpoint", 10, false),
        new("MediumBatchNoCheckpoint", 10, true),
        new("CheckpointForcingBatch", 50, false, 1),
        new("CheckpointForcingBatch", 50, true, 1),

        // An observational event requires a preceding state-changing event, so it only targets existing streams.
        new("SingleObservationalEvent", 1, true, Observational: true)
    ];

    private BenchmarkContext _baseline = null!;
    private Int32 _baselineInvocation;
    private MongoDbContainer? _container;
    private BenchmarkContext _optimized = null!;
    private Int32 _optimizedInvocation;

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
            _ = await AppendAsync(_optimized, operation, Interlocked.Increment(ref _optimizedInvocation));
        }
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = OperationsPerBenchmarkInvocation)]
    public async Task AppendWithoutBulkWrite()
    {
        for (var operation = 0; operation < OperationsPerBenchmarkInvocation; operation++)
        {
            _ = await AppendAsync(_baseline, operation, Interlocked.Increment(ref _baselineInvocation));
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

        AppendFirstEventAsync(_baseline.Store).GetAwaiter().GetResult();
        AppendFirstEventAsync(_optimized.Store).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Seeds fresh streams outside the measurement, so every measured append to an existing stream
    /// starts from identical state instead of a stream grown by earlier iterations.
    /// </summary>
    [IterationSetup]
    public void IterationSetup()
    {
        if (!Scenario.ExistingStream)
        {
            return;
        }

        SeedFreshStreamsAsync(_baseline).GetAwaiter().GetResult();
        SeedFreshStreamsAsync(_optimized).GetAwaiter().GetResult();
    }

    private static async Task<Guid> AppendFirstEventAsync(IEventStore<BenchmarkOrderAggregate> eventStore)
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

        return aggregateId;
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

    private static async Task SeedFreshStreamsAsync(BenchmarkContext context)
    {
        context.FreshStreams.Clear();
        for (var index = 0; index < OperationsPerBenchmarkInvocation; index++)
        {
            context.FreshStreams.Add(await AppendFirstEventAsync(context.Store));
        }
    }

    private async Task<BenchmarkOrderAggregate> AppendAsync(BenchmarkContext context, Int32 operation, Int32 invocation)
    {
        if (!Scenario.ExistingStream)
        {
            return await context.Store.AppendEventsAsync(CreateEvents(Guid.CreateVersion7(), 1, invocation));
        }

        // Every append targets its own stream seeded for this iteration, so it reads its predecessor.
        return await context.Store.AppendEventsAsync(CreateEvents(context.FreshStreams[operation], 2, invocation));
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
                                  .WithEvent<BenchmarkOrderViewedEvent>("OrderViewed")
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
            events.Add(Scenario.Observational
                           ? new BenchmarkOrderViewedEvent
                           {
                               Id = Guid.CreateVersion7(),
                               AggregateId = aggregateId,
                               Version = version
                           }
                           : new BenchmarkOrderAdjustedEvent
                           {
                               Id = Guid.CreateVersion7(),
                               AggregateId = aggregateId,
                               Version = version,
                               AmountDelta = invocationCount + version
                           });
        }

        return events;
    }

    /// <summary>
    /// A valid benchmark scenario.
    /// </summary>
    /// <param name="Name">The scenario name.</param>
    /// <param name="EventCount">The number of events appended per operation.</param>
    /// <param name="ExistingStream">
    /// Whether appends target pre-seeded streams (version 2) instead of new aggregates. Only appends to
    /// existing streams read their predecessor.
    /// </param>
    /// <param name="CheckpointInterval">The checkpoint interval, or <c>null</c> to disable checkpoints.</param>
    /// <param name="Observational">Whether the appended events are observational.</param>
    public sealed record BenchmarkScenario(
        String Name,
        Int32 EventCount,
        Boolean ExistingStream,
        Int32? CheckpointInterval = null,
        Boolean Observational = false)
    {
        public override String ToString()
            => ExistingStream ? $"{Name}/ExistingStream" : $"{Name}/NewStream";
    }

    private sealed record BenchmarkContext(
        ServiceProvider ServiceProvider,
        IEventStore<BenchmarkOrderAggregate> Store,
        IMongoHelper MongoHelper,
        String DatabaseName)
    {
        public List<Guid> FreshStreams { get; } = [];
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

    private sealed class BenchmarkOrderViewedEvent : ObservationalEvent<BenchmarkOrderAggregate> { }
}
