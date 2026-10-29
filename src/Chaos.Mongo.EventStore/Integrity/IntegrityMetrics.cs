// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// Provides public metric names for integrity anchoring diagnostics.
/// </summary>
public static class IntegrityMetrics
{
    /// <summary>
    /// The meter name used for anchoring diagnostics.
    /// </summary>
    public const String MeterName = "Chaos.Mongo.EventStore.Integrity";

    /// <summary>
    /// Provides the instrument names used for anchoring diagnostics.
    /// </summary>
    public static class Instruments
    {
        /// <summary>
        /// The histogram name for the age of the oldest unanchored candidate.
        /// </summary>
        public const String AnchorDelay = "chaos.mongo.eventstore.anchoring.delay";

        /// <summary>
        /// The counter name for runs after which the anchor delay exceeded the configured threshold.
        /// </summary>
        public const String AnchorDelayExceeded = "chaos.mongo.eventstore.anchoring.delay_exceeded";

        /// <summary>
        /// The histogram name for the age of the oldest timestamp token with an unconfirmed revocation status.
        /// </summary>
        public const String RevocationConfirmationDelay = "chaos.mongo.eventstore.anchoring.revocation_confirmation_delay";

        /// <summary>
        /// The counter name for runs after which the revocation confirmation delay exceeded the configured threshold.
        /// </summary>
        public const String RevocationConfirmationDelayExceeded = "chaos.mongo.eventstore.anchoring.revocation_confirmation_delay_exceeded";

        /// <summary>
        /// The counter name for timestamp tokens marked revoked, because a confirming CRL lists a certificate of
        /// their chain as revoked at or before the token's time.
        /// </summary>
        public const String RevokedTimestamps = "chaos.mongo.eventstore.anchoring.revoked_timestamps";
    }

    /// <summary>
    /// Provides the tag names used for anchoring diagnostics.
    /// </summary>
    public static class Tags
    {
        /// <summary>
        /// The tag name describing the timestamp authority.
        /// </summary>
        public const String Authority = "anchoring.authority";
    }
}
