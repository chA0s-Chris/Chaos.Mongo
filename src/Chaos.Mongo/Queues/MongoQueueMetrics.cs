// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Queues;

/// <summary>
/// Provides public metric names for MongoDB queue diagnostics.
/// </summary>
public static class MongoQueueMetrics
{
    /// <summary>
    /// The meter name used for MongoDB queue diagnostics.
    /// </summary>
    public const String MeterName = "Chaos.Mongo.Queues";

    /// <summary>
    /// Provides the instrument names used for MongoDB queue diagnostics.
    /// </summary>
    public static class Instruments
    {
        /// <summary>
        /// The counter name for recovered queue item locks.
        /// </summary>
        public const String LockRecovered = "chaos.mongo.queue.lock.recovered";

        /// <summary>
        /// The histogram name for recovered lock age.
        /// </summary>
        public const String LockRecoveryAge = "chaos.mongo.queue.lock.recovery_age";

        /// <summary>
        /// The histogram name for queue item processing duration.
        /// </summary>
        public const String ProcessingDuration = "chaos.mongo.queue.processing.duration";

        /// <summary>
        /// The counter name for failed queue item processing attempts.
        /// </summary>
        public const String ProcessingFailed = "chaos.mongo.queue.processing.failed";

        /// <summary>
        /// The histogram name for queue item age at processing time.
        /// </summary>
        public const String ProcessingQueueAge = "chaos.mongo.queue.processing.queue_age";

        /// <summary>
        /// The counter name for successfully completed queue items.
        /// </summary>
        public const String ProcessingSucceeded = "chaos.mongo.queue.processing.succeeded";
        /// <summary>
        /// The counter name for published queue items.
        /// </summary>
        public const String Published = "chaos.mongo.queue.published";
    }

    /// <summary>
    /// Provides the tag names used for MongoDB queue diagnostics.
    /// </summary>
    public static class Tags
    {
        /// <summary>
        /// The tag name describing how successful queue items are cleaned up.
        /// </summary>
        public const String CleanupMode = "queue.cleanup_mode";

        /// <summary>
        /// The tag name describing the payload type.
        /// </summary>
        public const String PayloadType = "queue.payload_type";

        /// <summary>
        /// The tag name describing the queue collection.
        /// </summary>
        public const String QueueCollection = "queue.collection";

        /// <summary>
        /// The tag name describing the processing result.
        /// </summary>
        public const String Result = "queue.result";
    }
}
