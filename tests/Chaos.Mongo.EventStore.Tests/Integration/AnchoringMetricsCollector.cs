// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Chaos.Mongo.EventStore.Integrity;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

/// <summary>
/// Collects the measurements of the anchoring meter.
/// </summary>
internal sealed class AnchoringMetricsCollector : IDisposable
{
    private readonly MeterListener _listener = new();

    public AnchoringMetricsCollector()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == IntegrityMetrics.MeterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<Int64>(RecordMeasurement);
        _listener.SetMeasurementEventCallback<Double>(RecordMeasurement);
        _listener.Start();
    }

    public ConcurrentQueue<(String Instrument, Double Value, String? Unit, IReadOnlyDictionary<String, String?> Tags)> Measurements { get; } = new();

    private void RecordMeasurement<T>(Instrument instrument, T measurement, ReadOnlySpan<KeyValuePair<String, Object?>> tags, Object? state)
        where T : struct
    {
        var tagDictionary = new Dictionary<String, String?>(tags.Length);
        foreach (var tag in tags)
        {
            tagDictionary[tag.Key] = tag.Value?.ToString();
        }

        Measurements.Enqueue((instrument.Name, Convert.ToDouble(measurement), instrument.Unit, tagDictionary));
    }

    public void Dispose() => _listener.Dispose();
}
