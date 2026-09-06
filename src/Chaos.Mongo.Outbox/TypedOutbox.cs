// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox;

using MongoDB.Driver;

internal sealed class TypedOutbox<TOutbox> : IOutbox<TOutbox>
{
    private readonly MongoOutbox _outbox;

    public TypedOutbox(MongoOutbox outbox) => _outbox = outbox;

    public Task AddMessageAsync<TPayload>(IClientSessionHandle session, TPayload payload,
                                          String? correlationId = null, CancellationToken cancellationToken = default)
        where TPayload : class, new()
        => _outbox.AddMessageAsync(session, payload, correlationId, cancellationToken);
}
