// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests.Queues;

using Chaos.Mongo.Queues;
using System.Diagnostics.Metrics;

internal sealed class QueueMetricsCollector : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly List<QueueMetricMeasurement> _measurements = [];

    public QueueMetricsCollector()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == MongoQueueMetrics.MeterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<Int64>(RecordMeasurement);
        _listener.SetMeasurementEventCallback<Double>(RecordMeasurement);
        _listener.Start();
    }

    public IReadOnlyList<QueueMetricMeasurement> Measurements => _measurements;

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

        _measurements.Add(new(instrument.Name,
                              Convert.ToDouble(measurement),
                              tagDictionary));
    }

    public void Dispose()
        => _listener.Dispose();
}

internal sealed record QueueMetricMeasurement(String InstrumentName, Double Value, Dictionary<String, String?> Tags);
