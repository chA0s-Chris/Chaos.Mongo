// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.LoadTests;

using System.Diagnostics;

/// <summary>
/// Concurrent appenders, each on its own pre-seeded stream, append one event at a time for a fixed duration.
/// </summary>
internal static class AppendLoadTest
{
    public static async Task<AppendLoadResult> RunAsync(String connectionString,
                                                        LoadTestMode mode,
                                                        Boolean bulkWrite,
                                                        Int32 concurrency,
                                                        TimeSpan warmup,
                                                        TimeSpan duration)
    {
        await using var store = await LoadTestStore.CreateAsync(connectionString, mode, bulkWrite);
        var streams = await store.SeedStreamsAsync(concurrency);
        await store.StartConsumerAsync();

        var clock = Stopwatch.StartNew();
        var measureFrom = warmup;
        var stopAt = warmup + duration;
        var appenders = new List<Task<List<Double>>>(streams.Count);
        foreach (var aggregateId in streams)
        {
            appenders.Add(AppendUntilAsync(store, aggregateId, clock, measureFrom, stopAt));
        }

        var latencies = await Task.WhenAll(appenders);

        var all = latencies.SelectMany(appender => appender).ToArray();
        Array.Sort(all);
        return new AppendLoadResult(all.Length / duration.TotalSeconds, Percentile(all, 0.50), Percentile(all, 0.99), all.Length);
    }

    private static async Task<List<Double>> AppendUntilAsync(LoadTestStore store,
                                                             Guid aggregateId,
                                                             Stopwatch clock,
                                                             TimeSpan measureFrom,
                                                             TimeSpan stopAt)
    {
        var latencies = new List<Double>();
        var version = 2L;
        while (clock.Elapsed < stopAt)
        {
            var startedAt = clock.Elapsed;
            await store.Store.AppendEventsAsync([LoadTestAdjustedEvent.Create(aggregateId, version)]);
            var completedAt = clock.Elapsed;
            version++;

            if (startedAt >= measureFrom && completedAt <= stopAt)
            {
                latencies.Add((completedAt - startedAt).TotalMilliseconds);
            }
        }

        return latencies;
    }

    private static Double Percentile(Double[] sorted, Double percentile)
        => sorted.Length == 0 ? Double.NaN : sorted[Math.Max(0, (Int32)Math.Ceiling(percentile * sorted.Length) - 1)];
}
