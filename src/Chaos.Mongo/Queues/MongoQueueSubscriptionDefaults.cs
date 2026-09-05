// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Queues;

internal static class MongoQueueSubscriptionDefaults
{
    internal static readonly TimeSpan MaxLeaseRecoveryWakeInterval = TimeSpan.FromSeconds(1);
}
