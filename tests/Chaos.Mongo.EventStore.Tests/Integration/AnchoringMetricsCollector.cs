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
            if (instrument.Meter.Name == IntegrityAnchoringDiagnostics.MeterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<Int64>((instrument, measurement, _, _) => Measurements.Enqueue((instrument.Name, measurement)));
        _listener.SetMeasurementEventCallback<Double>((instrument, measurement, _, _) => Measurements.Enqueue((instrument.Name, measurement)));
        _listener.Start();
    }

    public ConcurrentQueue<(String Instrument, Double Value)> Measurements { get; } = new();

    public void Dispose() => _listener.Dispose();
}
