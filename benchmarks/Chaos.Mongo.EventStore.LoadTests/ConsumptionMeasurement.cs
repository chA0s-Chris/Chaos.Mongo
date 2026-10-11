// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.LoadTests;

using System.Diagnostics;

/// <summary>
/// Measures how fast the change-stream candidate source catches up on a backlog that built up while no consumer ran.
/// </summary>
internal static class ConsumptionMeasurement
{
    /// <summary>
    /// Builds a backlog of single-event appends and times draining it.
    /// </summary>
    public static async Task<ConsumptionResult> RunAppendBacklogAsync(String connectionString, Int32 streams, Int32 appendsPerStream)
    {
        await using var store = await LoadTestStore.CreateAsync(connectionString, LoadTestMode.ChainWithChangeStream, false);
        var aggregateIds = await store.SeedStreamsAsync(streams);
        await store.DrainUntilIdleAsync();

        await Parallel.ForEachAsync(aggregateIds,
                                    new ParallelOptions
                                    {
                                        MaxDegreeOfParallelism = 32
                                    },
                                    async (aggregateId, _) =>
                                    {
                                        for (var version = 2; version < 2 + appendsPerStream; version++)
                                        {
                                            await store.Store.AppendEventsAsync([LoadTestAdjustedEvent.Create(aggregateId, version)]);
                                        }
                                    });

        // Every append yields one candidate.
        return await TimeDrainAsync(store, (Int64)streams * appendsPerStream, streams);
    }

    /// <summary>
    /// Writes unsealed events, seals them retroactively and times draining the candidates the sealing produced.
    /// </summary>
    public static async Task<ConsumptionResult> RunRetroactiveSealingBacklogAsync(String connectionString, Int32 streams, Int32 eventsPerStream)
    {
        var databaseName = $"EventStoreLoadTest_{Guid.NewGuid():N}";
        await using var unprotected = await LoadTestStore.CreateAsync(connectionString, LoadTestMode.Chain, false, databaseName, false);
        await unprotected.SeedStreamsAsync(streams, eventsPerStream);

        // The source starts after the unsealed events exist, so the change stream only sees the sealing updates.
        await using var store = await LoadTestStore.CreateAsync(connectionString, LoadTestMode.ChainWithChangeStream, false, databaseName);
        await store.DrainUntilIdleAsync();
        await store.Sweep.RunPassAsync();

        // The change stream sees one update per sealed event.
        return await TimeDrainAsync(store, (Int64)streams * eventsPerStream, streams);
    }

    private static async Task<ConsumptionResult> TimeDrainAsync(LoadTestStore store, Int64 expectedCandidates, Int32 streams)
    {
        var clock = Stopwatch.StartNew();
        var read = await store.DrainAsync(expectedCandidates, TimeSpan.FromMinutes(10));
        clock.Stop();
        return new ConsumptionResult(read, streams, clock.Elapsed.TotalSeconds);
    }
}
