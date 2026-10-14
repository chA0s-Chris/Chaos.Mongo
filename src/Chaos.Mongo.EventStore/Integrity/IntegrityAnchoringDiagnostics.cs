// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using System.Diagnostics.Metrics;

/// <summary>
/// Metrics of the anchoring pipeline.
/// </summary>
internal static class IntegrityAnchoringDiagnostics
{
    /// <summary>
    /// The number of runs after which the anchor delay exceeded the configured threshold.
    /// </summary>
    public const String AnchorDelayExceededInstrument = "chaos.mongo.eventstore.anchoring.delay_exceeded";

    /// <summary>
    /// The age of the oldest unanchored candidate in seconds, recorded after every run that saw one.
    /// </summary>
    public const String AnchorDelayInstrument = "chaos.mongo.eventstore.anchoring.delay";

    /// <summary>
    /// The meter name used for anchoring diagnostics.
    /// </summary>
    public const String MeterName = "Chaos.Mongo.EventStore.Integrity";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Histogram<Double> AnchorDelayHistogram = Meter.CreateHistogram<Double>(
        AnchorDelayInstrument,
        "s",
        "Age of the oldest unanchored candidate in seconds.");

    private static readonly Counter<Int64> AnchorDelayExceededCounter = Meter.CreateCounter<Int64>(
        AnchorDelayExceededInstrument,
        description: "Number of anchoring runs after which the anchor delay exceeded its threshold.");


    /// <summary>
    /// Records the anchor delay of a run.
    /// </summary>
    /// <param name="delay">The age of the oldest unanchored candidate.</param>
    /// <param name="exceeded">Whether the delay exceeded the threshold.</param>
    internal static void RecordAnchorDelay(TimeSpan delay, Boolean exceeded)
    {
        AnchorDelayHistogram.Record(delay.TotalSeconds);
        if (exceeded)
        {
            AnchorDelayExceededCounter.Add(1);
        }
    }
}
