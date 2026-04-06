// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox.Tests.OrdersApi;

using System.Collections.Concurrent;

public sealed class TestNotificationPublisher : IOutboxPublisher
{
    private Int32 _failCount;

    public ConcurrentBag<OutboxMessage> DeliveredMessages { get; } = [];

    /// <summary>
    /// When set to a value greater than zero, the next N publish attempts will throw.
    /// </summary>
    public Int32 FailNextAttempts
    {
        get => _failCount;
        set => Interlocked.Exchange(ref _failCount, value);
    }

    public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Decrement(ref _failCount) >= 0)
        {
            throw new InvalidOperationException("Simulated broker failure");
        }

        DeliveredMessages.Add(message);
        return Task.CompletedTask;
    }
}
