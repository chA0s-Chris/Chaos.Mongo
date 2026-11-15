// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox;

using System.Diagnostics;
using System.Diagnostics.Metrics;

internal static class OutboxDiagnostics
{
    private static readonly Meter Meter = new(OutboxMetrics.MeterName);

    private static readonly Histogram<Double> MessageAgeHistogram = Meter.CreateHistogram<Double>(
        OutboxMetrics.Instruments.MessageAge,
        "s",
        "Time from outbox message creation to the processed state in seconds.");


    private static readonly Histogram<Double> PublishDurationHistogram = Meter.CreateHistogram<Double>(
        OutboxMetrics.Instruments.PublishDuration,
        "s",
        "Duration of the publisher call of delivered outbox messages in seconds.");

    private static readonly Counter<Int64> PublishFailedCounter = Meter.CreateCounter<Int64>(
        OutboxMetrics.Instruments.PublishFailed,
        description: "Number of failed outbox publish attempts.");

    private static readonly Counter<Int64> PublishTerminalFailedCounter = Meter.CreateCounter<Int64>(
        OutboxMetrics.Instruments.PublishTerminalFailed,
        description: "Number of outbox messages that permanently failed.");

    private static readonly Counter<Int64> PublishedCounter = Meter.CreateCounter<Int64>(
        OutboxMetrics.Instruments.Published,
        description: "Number of outbox messages published and marked as processed.");

    internal static void RecordPublishFailed(String destination)
        => PublishFailedCounter.Add(1, CreateTags(destination));

    internal static void RecordPublishTerminalFailed(String destination)
        => PublishTerminalFailedCounter.Add(1, CreateTags(destination));

    internal static void RecordPublished(String destination, TimeSpan publishDuration, TimeSpan messageAge)
    {
        var tags = CreateTags(destination);
        PublishedCounter.Add(1, tags);
        PublishDurationHistogram.Record(publishDuration.TotalSeconds, tags);
        MessageAgeHistogram.Record(messageAge.TotalSeconds, tags);
    }

    private static TagList CreateTags(String destination)
    {
        TagList tags = [];
        tags.Add(OutboxMetrics.Tags.Destination, destination);
        return tags;
    }
}
