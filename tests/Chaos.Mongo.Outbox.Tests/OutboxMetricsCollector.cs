// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox.Tests;

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;

/// <summary>
/// Collects the measurements of the outbox meter. <c>OutboxDiagnostics</c> uses one static meter and tests run in parallel,
/// so measurements arrive on arbitrary threads and every assertion must filter by a unique destination.
/// </summary>
internal sealed class OutboxMetricsCollector : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<OutboxMetricMeasurement> _measurements = new();

    public OutboxMetricsCollector()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == OutboxMetrics.MeterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<Int64>(RecordMeasurement);
        _listener.SetMeasurementEventCallback<Double>(RecordMeasurement);
        _listener.Start();
    }

    public IReadOnlyList<OutboxMetricMeasurement> For(String destination)
        => _measurements.Where(m => m.Tags.GetValueOrDefault(OutboxMetrics.Tags.Destination) == destination).ToArray();

    public IReadOnlyList<OutboxMetricMeasurement> ForDestinationPrefix(String prefix)
        => _measurements.Where(m => m.Tags.GetValueOrDefault(OutboxMetrics.Tags.Destination)?.StartsWith(prefix, StringComparison.Ordinal) == true)
                        .ToArray();

    private void RecordMeasurement<T>(Instrument instrument,
                                      T measurement,
                                      ReadOnlySpan<KeyValuePair<String, Object?>> tags,
                                      Object? state)
        where T : struct
    {
        var tagDictionary = new Dictionary<String, String?>(tags.Length);
        foreach (var tag in tags)
        {
            tagDictionary[tag.Key] = tag.Value?.ToString();
        }

        _measurements.Enqueue(new OutboxMetricMeasurement(instrument.Name, Convert.ToDouble(measurement, CultureInfo.InvariantCulture), tagDictionary, instrument.Unit));
    }

    public void Dispose()
        => _listener.Dispose();
}

internal sealed record OutboxMetricMeasurement(String InstrumentName, Double Value, Dictionary<String, String?> Tags, String? Unit);
