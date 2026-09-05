// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox;

internal sealed class TypedOutboxProcessor<TOutbox> : IOutboxProcessor<TOutbox>
{
    private readonly OutboxProcessor _processor;

    public TypedOutboxProcessor(OutboxProcessor processor) => _processor = processor;

    public Task StartAsync(CancellationToken cancellationToken = default) => _processor.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default) => _processor.StopAsync(cancellationToken);
}
