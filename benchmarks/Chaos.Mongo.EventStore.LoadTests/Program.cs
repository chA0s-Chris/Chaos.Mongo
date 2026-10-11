// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.LoadTests;

using System.Globalization;
using System.Runtime.InteropServices;

/// <summary>
/// Load tests for the anchor candidate sources of #157.
/// </summary>
/// <remarks>
/// Usage:
/// <c>
/// dotnet run -c Release -- &lt;load|oplog|consume&gt; [--topology single|three-node] [--runs 3]
/// [--warmup 10] [--duration 30] [--concurrency 16,64] [--delay-ms 1]
/// </c>
/// .
/// </remarks>
public static class Program
{
    private static readonly LoadTestMode[] Modes = [LoadTestMode.Chain, LoadTestMode.ChainWithChangeStream];

    public static async Task<Int32> Main(String[] args)
    {
        if (args.Length == 0 || args[0] is not ("load" or "oplog" or "consume"))
        {
            await Console.Error.WriteLineAsync("Usage: <load|oplog|consume> [--topology single|three-node] [--runs 3] [--warmup 10] " +
                                               "[--duration 30] [--concurrency 16,64] [--delay-ms 1]");
            return 1;
        }

        var options = ParseOptions(args.Skip(1).ToArray());
        await using var topology = options.GetValueOrDefault("topology", "single") == "three-node"
            ? await MongoTopology.StartThreeNodeAsync(TimeSpan.FromMilliseconds(Double.Parse(options.GetValueOrDefault("delay-ms", "1"), CultureInfo.InvariantCulture)))
            : await MongoTopology.StartSingleNodeAsync();

        Console.WriteLine($"Environment: .NET {Environment.Version}, {RuntimeInformation.OSDescription}, {Environment.ProcessorCount} logical CPUs");
        Console.WriteLine($"Topology: {topology.Description}");
        Console.WriteLine();

        switch (args[0])
        {
            case "load":
                await RunLoadAsync(topology,
                                   Int32.Parse(options.GetValueOrDefault("runs", "3"), CultureInfo.InvariantCulture),
                                   TimeSpan.FromSeconds(Int32.Parse(options.GetValueOrDefault("warmup", "10"), CultureInfo.InvariantCulture)),
                                   TimeSpan.FromSeconds(Int32.Parse(options.GetValueOrDefault("duration", "30"), CultureInfo.InvariantCulture)),
                                   options.GetValueOrDefault("concurrency", "16,64").Split(',').Select(value => Int32.Parse(value, CultureInfo.InvariantCulture)).ToArray());
                break;
            case "oplog":
                await RunOplogAsync(topology);
                break;
            default:
                await RunConsumeAsync(topology);
                break;
        }

        return 0;
    }

    private static String Format(Double value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static Double Median(IEnumerable<Double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2;
    }

    private static Dictionary<String, String> ParseOptions(String[] args)
    {
        var options = new Dictionary<String, String>(StringComparer.Ordinal);
        for (var index = 0; index + 1 < args.Length; index += 2)
        {
            options[args[index].TrimStart('-')] = args[index + 1];
        }

        return options;
    }

    private static async Task RunConsumeAsync(MongoTopology topology)
    {
        Console.WriteLine("| Backlog | Candidates read | Streams | Seconds | Candidates/s | Streams/s |");
        Console.WriteLine("|---|---|---|---|---|---|");

        var appends = await ConsumptionMeasurement.RunAppendBacklogAsync(topology.ConnectionString, 1000, 20);
        Console.WriteLine($"| 20,000 appends on 1,000 streams | {appends.Candidates} | {appends.Streams} | {Format(appends.Seconds)} | " +
                          $"{Format(appends.CandidatesPerSecond)} | {Format(appends.StreamsPerSecond)} |");

        var sealing = await ConsumptionMeasurement.RunRetroactiveSealingBacklogAsync(topology.ConnectionString, 1000, 20);
        Console.WriteLine($"| 20,000 retroactively sealed events on 1,000 streams | {sealing.Candidates} | {sealing.Streams} | " +
                          $"{Format(sealing.Seconds)} | {Format(sealing.CandidatesPerSecond)} | {Format(sealing.StreamsPerSecond)} |");
    }

    private static async Task RunLoadAsync(MongoTopology topology, Int32 runs, TimeSpan warmup, TimeSpan duration, Int32[] concurrencies)
    {
        Console.WriteLine($"Warm-up {warmup.TotalSeconds} s, measurement {duration.TotalSeconds} s, {runs} runs per combination; " +
                          "every run uses a fresh database and pre-seeded streams, one single-event append at a time per appender.");
        Console.WriteLine();
        Console.WriteLine("| Path | Appenders | Source | Run | Appends/s | p50 ms | p99 ms |");
        Console.WriteLine("|---|---|---|---|---|---|---|");

        // A discarded run warms up the server and the client, so the first measured combination is not penalized.
        await AppendLoadTest.RunAsync(topology.ConnectionString, LoadTestMode.Chain, false, concurrencies[0], warmup, warmup);

        // Runs are interleaved across combinations, so slow drift of the environment affects every source alike.
        var results = new Dictionary<(Boolean BulkWrite, Int32 Concurrency, LoadTestMode Mode), List<AppendLoadResult>>();
        for (var run = 1; run <= runs; run++)
        {
            foreach (var bulkWrite in new[]
                     {
                         false,
                         true
                     })
            {
                foreach (var concurrency in concurrencies)
                {
                    foreach (var mode in Modes)
                    {
                        var result = await AppendLoadTest.RunAsync(topology.ConnectionString, mode, bulkWrite, concurrency, warmup, duration);
                        if (!results.TryGetValue((bulkWrite, concurrency, mode), out var combination))
                        {
                            results[(bulkWrite, concurrency, mode)] = combination = [];
                        }

                        combination.Add(result);
                        Console.WriteLine($"| {(bulkWrite ? "bulk write" : "default")} | {concurrency} | {mode} | {run} | " +
                                          $"{Format(result.Throughput)} | {Format(result.P50)} | {Format(result.P99)} |");
                    }
                }
            }
        }

        var medians = results.ToDictionary(entry => entry.Key,
                                           entry => (Throughput: Median(entry.Value.Select(result => result.Throughput)),
                                                     P99: Median(entry.Value.Select(result => result.P99))));

        Console.WriteLine();
        Console.WriteLine("Medians, and the change stream's overhead over chain only:");
        Console.WriteLine();
        Console.WriteLine("| Path | Appenders | Chain appends/s | Chain p99 | D appends/s | D p99 | D/chain throughput | D/chain p99 |");
        Console.WriteLine("|---|---|---|---|---|---|---|---|");
        foreach (var ((bulkWrite, concurrency, _), chain) in medians.Where(entry => entry.Key.Mode == LoadTestMode.Chain))
        {
            var d = medians[(bulkWrite, concurrency, LoadTestMode.ChainWithChangeStream)];
            Console.WriteLine($"| {(bulkWrite ? "bulk write" : "default")} | {concurrency} | {Format(chain.Throughput)} | {Format(chain.P99)} | " +
                              $"{Format(d.Throughput)} | {Format(d.P99)} | {Format(d.Throughput / chain.Throughput)} | {Format(d.P99 / chain.P99)} |");
        }
    }

    private static async Task RunOplogAsync(MongoTopology topology)
    {
        const Int32 appends = 2000;
        Console.WriteLine($"{appends} single-event appends to existing streams on the default path, consumer acknowledgements included.");
        Console.WriteLine();
        Console.WriteLine("| Source | Oplog bytes per append | Oplog entries per append |");
        Console.WriteLine("|---|---|---|");
        foreach (var mode in Modes)
        {
            var result = await OplogGrowthMeasurement.RunAsync(topology.ConnectionString, mode, appends);
            Console.WriteLine($"| {mode} | {Format(result.BytesPerAppend)} | {Format(result.EntriesPerAppend)} |");
        }
    }
}
