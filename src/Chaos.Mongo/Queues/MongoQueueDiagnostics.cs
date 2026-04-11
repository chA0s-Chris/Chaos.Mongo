// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Queues;

using System.Diagnostics;
using System.Diagnostics.Metrics;

internal static class MongoQueueDiagnostics
{
    private const String CleanupModeTagName = "queue.cleanup_mode";
    internal const String MeterName = "Chaos.Mongo.Queues";
    private const String PayloadTypeTagName = "queue.payload_type";
    private const String QueueCollectionTagName = "queue.collection";
    private const String ResultTagName = "queue.result";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<Int64> ProcessingFailedCounter = Meter.CreateCounter<Int64>(
        "chaos.mongo.queue.processing.failed",
        description: "Number of failed queue item processing attempts.");
    private static readonly Counter<Int64> ProcessingSucceededCounter = Meter.CreateCounter<Int64>(
        "chaos.mongo.queue.processing.succeeded",
        description: "Number of successfully completed queue items.");
    private static readonly Histogram<Double> ProcessingDurationHistogram = Meter.CreateHistogram<Double>(
        "chaos.mongo.queue.processing.duration",
        unit: "ms",
        description: "Queue item processing duration in milliseconds.");
    private static readonly Counter<Int64> PublishedCounter = Meter.CreateCounter<Int64>(
        "chaos.mongo.queue.published",
        description: "Number of queue items published.");
    private static readonly Histogram<Double> QueueAgeHistogram = Meter.CreateHistogram<Double>(
        "chaos.mongo.queue.processing.queue_age",
        unit: "ms",
        description: "Time from queue item creation to lock acquisition in milliseconds.");
    private static readonly Counter<Int64> RecoveredLockCounter = Meter.CreateCounter<Int64>(
        "chaos.mongo.queue.lock.recovered",
        description: "Number of expired or malformed queue item locks recovered.");
    private static readonly Histogram<Double> RecoveredLockAgeHistogram = Meter.CreateHistogram<Double>(
        "chaos.mongo.queue.lock.recovery_age",
        unit: "ms",
        description: "Age of recovered queue item locks in milliseconds.");

    internal static void RecordLockRecovered(MongoQueueDefinition queueDefinition, TimeSpan lockAge)
    {
        var tags = CreateCommonTags(queueDefinition);
        RecoveredLockCounter.Add(1, tags);
        RecoveredLockAgeHistogram.Record(lockAge.TotalMilliseconds, tags);
    }

    internal static void RecordProcessingFailed(MongoQueueDefinition queueDefinition, String result)
    {
        var tags = CreateCommonTags(queueDefinition);
        tags.Add(ResultTagName, result);
        ProcessingFailedCounter.Add(1, tags);
    }

    internal static void RecordProcessingSucceeded(MongoQueueDefinition queueDefinition,
                                                   TimeSpan processingDuration,
                                                   TimeSpan queueAge,
                                                   String cleanupMode)
    {
        var tags = CreateCommonTags(queueDefinition);
        tags.Add(CleanupModeTagName, cleanupMode);

        ProcessingSucceededCounter.Add(1, tags);
        ProcessingDurationHistogram.Record(processingDuration.TotalMilliseconds, tags);
        QueueAgeHistogram.Record(queueAge.TotalMilliseconds, tags);
    }

    internal static void RecordPublished(MongoQueueDefinition queueDefinition)
        => PublishedCounter.Add(1, CreateCommonTags(queueDefinition));

    private static TagList CreateCommonTags(MongoQueueDefinition queueDefinition)
    {
        TagList tags = [];
        tags.Add(QueueCollectionTagName, queueDefinition.CollectionName);
        tags.Add(PayloadTypeTagName, queueDefinition.PayloadType.FullName ?? queueDefinition.PayloadType.Name);
        return tags;
    }
}
