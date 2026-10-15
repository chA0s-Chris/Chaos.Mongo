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

    /// <summary>
    /// The number of runs after which the revocation confirmation delay exceeded the configured threshold.
    /// </summary>
    public const String RevocationConfirmationDelayExceededInstrument = "chaos.mongo.eventstore.anchoring.revocation_confirmation_delay_exceeded";

    /// <summary>
    /// The age in seconds of the oldest timestamp token whose revocation status is unconfirmed, recorded after every run
    /// that saw one.
    /// </summary>
    public const String RevocationConfirmationDelayInstrument = "chaos.mongo.eventstore.anchoring.revocation_confirmation_delay";

    /// <summary>
    /// The number of timestamp tokens marked revoked, because a confirming CRL lists a certificate of their chain as
    /// revoked at or before the token's time.
    /// </summary>
    public const String RevokedTimestampsInstrument = "chaos.mongo.eventstore.anchoring.revoked_timestamps";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Histogram<Double> AnchorDelayHistogram = Meter.CreateHistogram<Double>(
        AnchorDelayInstrument,
        "s",
        "Age of the oldest unanchored candidate in seconds.");

    private static readonly Counter<Int64> AnchorDelayExceededCounter = Meter.CreateCounter<Int64>(
        AnchorDelayExceededInstrument,
        description: "Number of anchoring runs after which the anchor delay exceeded its threshold.");


    private static readonly Histogram<Double> RevocationConfirmationDelayHistogram = Meter.CreateHistogram<Double>(
        RevocationConfirmationDelayInstrument,
        "s",
        "Age of the oldest timestamp token with an unconfirmed revocation status in seconds.");

    private static readonly Counter<Int64> RevocationConfirmationDelayExceededCounter = Meter.CreateCounter<Int64>(
        RevocationConfirmationDelayExceededInstrument,
        description: "Number of anchoring runs after which the revocation confirmation delay exceeded its threshold.");


    private static readonly Counter<Int64> RevokedTimestampsCounter = Meter.CreateCounter<Int64>(
        RevokedTimestampsInstrument,
        description: "Number of timestamp tokens whose chain a confirming CRL lists as revoked at or before the token's time.");


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

    /// <summary>
    /// Records the revocation confirmation delay of a run.
    /// </summary>
    /// <param name="delay">The age of the oldest timestamp token with an unconfirmed revocation status.</param>
    /// <param name="exceeded">Whether the delay exceeded the threshold.</param>
    internal static void RecordRevocationConfirmationDelay(TimeSpan delay, Boolean exceeded)
    {
        RevocationConfirmationDelayHistogram.Record(delay.TotalSeconds);
        if (exceeded)
        {
            RevocationConfirmationDelayExceededCounter.Add(1);
        }
    }

    /// <summary>
    /// Records a timestamp token marked revoked.
    /// </summary>
    internal static void RecordRevokedTimestamp() => RevokedTimestampsCounter.Add(1);
}
