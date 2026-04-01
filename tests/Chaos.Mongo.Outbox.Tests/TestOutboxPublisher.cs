// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox.Tests;

public class TestOutboxPublisher : IOutboxPublisher
{
    private readonly List<OutboxMessage> _publishedMessages = [];

    public IReadOnlyList<OutboxMessage> PublishedMessages => _publishedMessages;

    public Boolean ShouldThrow { get; set; }

    public String? ThrowMessage { get; set; }

    public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        if (ShouldThrow)
        {
            throw new InvalidOperationException(ThrowMessage ?? "Simulated publish failure");
        }

        _publishedMessages.Add(message);
        return Task.CompletedTask;
    }
}
