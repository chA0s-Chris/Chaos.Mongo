// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Queues;

internal static class MongoQueueSubscriptionDefaults
{
    /// <summary>
    /// The initial delay before retrying a failed <c>MonitorChangeStreamAsync</c> open or read, matching today's
    /// behavior on the first failure. Doubled after each consecutive failure, up to
    /// <see cref="ChangeStreamRetryMaxDelay"/>, and reset once an open cursor reads successfully.
    /// </summary>
    internal static readonly TimeSpan ChangeStreamRetryInitialDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// The cap on the change-stream retry loop's exponential backoff delay.
    /// </summary>
    internal static readonly TimeSpan ChangeStreamRetryMaxDelay = TimeSpan.FromSeconds(30);

    internal static readonly TimeSpan CompletionGraceTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan MaxLeaseRecoveryWakeInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The <see cref="MongoDB.Driver.MongoCommandException"/> code <c>WatchAsync</c> throws when the server is not
    /// a replica set or sharded cluster ("The $changeStream stage is only supported on replica sets"), confirmed
    /// empirically against a standalone MongoDB 7 server with <c>MongoDB.Driver</c> 3.9.0.
    /// </summary>
    internal const Int32 StandaloneServerErrorCode = 40573;
}
