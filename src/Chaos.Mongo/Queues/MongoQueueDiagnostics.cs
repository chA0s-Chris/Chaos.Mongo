// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Queues;

using System.Diagnostics;
using System.Diagnostics.Metrics;

internal static class MongoQueueDiagnostics
{
    private static readonly Meter _meter = new(MongoQueueMetrics.MeterName);

    private static readonly Histogram<Double> _processingDurationHistogram = _meter.CreateHistogram<Double>(
        MongoQueueMetrics.Instruments.ProcessingDuration,
        "ms",
        "Queue item processing duration in milliseconds.");

    private static readonly Counter<Int64> _processingFailedCounter = _meter.CreateCounter<Int64>(
        MongoQueueMetrics.Instruments.ProcessingFailed,
        description: "Number of failed queue item processing attempts.");

    private static readonly Counter<Int64> _processingSucceededCounter = _meter.CreateCounter<Int64>(
        MongoQueueMetrics.Instruments.ProcessingSucceeded,
        description: "Number of successfully completed queue items.");

    private static readonly Counter<Int64> _publishedCounter = _meter.CreateCounter<Int64>(
        MongoQueueMetrics.Instruments.Published,
        description: "Number of queue items published.");

    private static readonly Histogram<Double> _queueAgeHistogram = _meter.CreateHistogram<Double>(
        MongoQueueMetrics.Instruments.ProcessingQueueAge,
        "ms",
        "Time from queue item creation to lock acquisition in milliseconds.");

    private static readonly Histogram<Double> _recoveredLockAgeHistogram = _meter.CreateHistogram<Double>(
        MongoQueueMetrics.Instruments.LockRecoveryAge,
        "ms",
        "Age of recovered queue item locks in milliseconds.");

    private static readonly Counter<Int64> _recoveredLockCounter = _meter.CreateCounter<Int64>(
        MongoQueueMetrics.Instruments.LockRecovered,
        description: "Number of expired or malformed queue item locks recovered.");

    internal static void RecordLockRecovered(MongoQueueDefinition queueDefinition, TimeSpan lockAge)
    {
        var tags = CreateCommonTags(queueDefinition);
        _recoveredLockCounter.Add(1, tags);
        _recoveredLockAgeHistogram.Record(lockAge.TotalMilliseconds, tags);
    }

    internal static void RecordProcessingFailed(MongoQueueDefinition queueDefinition, String result)
    {
        var tags = CreateCommonTags(queueDefinition);
        tags.Add(MongoQueueMetrics.Tags.Result, result);
        _processingFailedCounter.Add(1, tags);
    }

    internal static void RecordProcessingSucceeded(MongoQueueDefinition queueDefinition,
                                                   TimeSpan processingDuration,
                                                   TimeSpan queueAge,
                                                   String cleanupMode)
    {
        var tags = CreateCommonTags(queueDefinition);
        tags.Add(MongoQueueMetrics.Tags.CleanupMode, cleanupMode);

        _processingSucceededCounter.Add(1, tags);
        _processingDurationHistogram.Record(processingDuration.TotalMilliseconds, tags);
        _queueAgeHistogram.Record(queueAge.TotalMilliseconds, tags);
    }

    internal static void RecordPublished(MongoQueueDefinition queueDefinition)
        => _publishedCounter.Add(1, CreateCommonTags(queueDefinition));

    private static TagList CreateCommonTags(MongoQueueDefinition queueDefinition)
    {
        TagList tags = [];
        tags.Add(MongoQueueMetrics.Tags.QueueCollection, queueDefinition.CollectionName);
        tags.Add(MongoQueueMetrics.Tags.PayloadType, queueDefinition.PayloadType.FullName ?? queueDefinition.PayloadType.Name);
        return tags;
    }
}
