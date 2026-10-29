// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using System.Diagnostics;
using System.Diagnostics.Metrics;

/// <summary>
/// Metrics of the anchoring pipeline.
/// </summary>
internal static class IntegrityAnchoringDiagnostics
{
    private static readonly Meter Meter = new(IntegrityMetrics.MeterName);

    private static readonly Counter<Int64> AnchorDelayExceededCounter = Meter.CreateCounter<Int64>(
        IntegrityMetrics.Instruments.AnchorDelayExceeded,
        description: "Number of anchoring runs after which the anchor delay exceeded its threshold.");

    private static readonly Histogram<Double> AnchorDelayHistogram = Meter.CreateHistogram<Double>(
        IntegrityMetrics.Instruments.AnchorDelay,
        "s",
        "Age of the oldest unanchored candidate in seconds.");


    private static readonly Counter<Int64> RevocationConfirmationDelayExceededCounter = Meter.CreateCounter<Int64>(
        IntegrityMetrics.Instruments.RevocationConfirmationDelayExceeded,
        description: "Number of anchoring runs after which the revocation confirmation delay exceeded its threshold.");


    private static readonly Histogram<Double> RevocationConfirmationDelayHistogram = Meter.CreateHistogram<Double>(
        IntegrityMetrics.Instruments.RevocationConfirmationDelay,
        "s",
        "Age of the oldest timestamp token with an unconfirmed revocation status in seconds.");


    private static readonly Counter<Int64> RevokedTimestampsCounter = Meter.CreateCounter<Int64>(
        IntegrityMetrics.Instruments.RevokedTimestamps,
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
    /// <param name="authority">The name of the timestamp authority that issued the token.</param>
    internal static void RecordRevokedTimestamp(String authority)
    {
        TagList tags = [];
        tags.Add(IntegrityMetrics.Tags.Authority, authority);
        RevokedTimestampsCounter.Add(1, tags);
    }
}
