// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox;

/// <summary>
/// Provides public metric names for transactional outbox diagnostics.
/// </summary>
public static class OutboxMetrics
{
    /// <summary>
    /// The meter name used for transactional outbox diagnostics.
    /// </summary>
    public const String MeterName = "Chaos.Mongo.Outbox";

    /// <summary>
    /// Provides the instrument names used for transactional outbox diagnostics.
    /// </summary>
    public static class Instruments
    {
        /// <summary>
        /// The histogram name for the age of an outbox message when it reached the processed state.
        /// </summary>
        public const String MessageAge = "chaos.mongo.outbox.message.age";

        /// <summary>
        /// The histogram name for the duration of the publisher call of delivered outbox messages.
        /// </summary>
        public const String PublishDuration = "chaos.mongo.outbox.publish.duration";

        /// <summary>
        /// The counter name for failed outbox publish attempts.
        /// </summary>
        public const String PublishFailed = "chaos.mongo.outbox.publish.failed";

        /// <summary>
        /// The counter name for outbox messages that permanently failed.
        /// </summary>
        public const String PublishTerminalFailed = "chaos.mongo.outbox.publish.terminal_failed";

        /// <summary>
        /// The counter name for outbox messages that were published and reached the processed state.
        /// </summary>
        public const String Published = "chaos.mongo.outbox.published";
    }

    /// <summary>
    /// Provides the tag names used for transactional outbox diagnostics.
    /// </summary>
    public static class Tags
    {
        /// <summary>
        /// The tag name describing the destination of the outbox, which is the name of its MongoDB collection.
        /// </summary>
        public const String Destination = "outbox.destination";
    }
}
