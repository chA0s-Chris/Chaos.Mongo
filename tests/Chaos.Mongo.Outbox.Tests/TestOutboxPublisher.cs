// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox.Tests;

using System.Collections.Concurrent;

public class TestOutboxPublisher : IOutboxPublisher
{
    public ConcurrentBag<OutboxMessage> PublishedMessages { get; } = [];

    public Boolean ShouldThrow { get; set; }

    public String? ThrowMessage { get; set; }

    public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        if (ShouldThrow)
        {
            throw new InvalidOperationException(ThrowMessage ?? "Simulated publish failure");
        }

        PublishedMessages.Add(message);
        return Task.CompletedTask;
    }
}
