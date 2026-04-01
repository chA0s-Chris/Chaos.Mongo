// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox;

using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

/// <summary>
/// MongoDB-backed implementation of <see cref="IOutbox"/> that writes outbox messages
/// within the caller's transaction.
/// </summary>
public sealed class MongoOutbox : IOutbox
{
    private readonly IMongoHelper _mongoHelper;
    private readonly OutboxOptions _options;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="MongoOutbox"/> class.
    /// </summary>
    /// <param name="mongoHelper">The MongoDB helper for database operations.</param>
    /// <param name="options">The outbox configuration options.</param>
    /// <param name="timeProvider">The time provider for timestamp operations.</param>
    public MongoOutbox(IMongoHelper mongoHelper, OutboxOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(mongoHelper);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _mongoHelper = mongoHelper;
        _options = options;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc/>
    public async Task AddMessageAsync<TPayload>(IClientSessionHandle session,
                                                TPayload payload,
                                                String? correlationId = null,
                                                CancellationToken cancellationToken = default)
        where TPayload : class, new()
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(payload);

        if (!session.IsInTransaction)
        {
            throw new InvalidOperationException(
                "The outbox requires an active MongoDB transaction. " +
                "The provided session does not have an active transaction.");
        }

        var payloadType = typeof(TPayload);
        if (!_options.MessageTypeLookup.TryGetValue(payloadType, out var discriminator))
        {
            throw new InvalidOperationException(
                $"Payload type '{payloadType.Name}' is not registered. " +
                "Use WithMessage<TPayload>() in the outbox builder to register it.");
        }

        var bsonPayload = payload.ToBsonDocument(payloadType, BsonSerializer.LookupSerializer(payloadType));

        var message = new OutboxMessage
        {
            Type = discriminator,
            Payload = bsonPayload,
            CreatedUtc = _timeProvider.GetUtcNow().UtcDateTime,
            CorrelationId = correlationId,
            State = OutboxMessageState.Pending
        };

        var collection = _mongoHelper.Database.GetCollection<OutboxMessage>(_options.CollectionName);

        await collection.InsertOneAsync(session, message, cancellationToken: cancellationToken);
    }
}
