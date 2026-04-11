// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Queues;

using MongoDB.Bson;

/// <summary>
/// Provides functionality for publishing payloads to MongoDB queue collections.
/// </summary>
public class MongoQueuePublisher : IMongoQueuePublisher
{
    private readonly IMongoHelper _mongoHelper;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="MongoQueuePublisher"/> class.
    /// </summary>
    /// <param name="mongoHelper">The MongoDB helper for database operations.</param>
    /// <param name="timeProvider">The time provider for timestamp operations.</param>
    /// <exception cref="ArgumentNullException">Thrown when any parameter is null.</exception>
    public MongoQueuePublisher(IMongoHelper mongoHelper,
                               TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(mongoHelper);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _mongoHelper = mongoHelper;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc/>
    public async Task PublishAsync<TPayload>(MongoQueueDefinition queueDefinition,
                                             TPayload payload,
                                             CancellationToken cancellationToken)
        where TPayload : class, new()
    {
        ArgumentNullException.ThrowIfNull(queueDefinition);
        ArgumentNullException.ThrowIfNull(payload);

        if (typeof(TPayload) != queueDefinition.PayloadType)
        {
            throw new InvalidOperationException($"Payload type {typeof(TPayload)} does not match queue payload type {queueDefinition.PayloadType}.");
        }

        if (String.IsNullOrEmpty(queueDefinition.CollectionName))
        {
            throw new InvalidOperationException("Queue collection name is empty.");
        }

        var collection = _mongoHelper.Database.GetCollection<MongoQueueItem<TPayload>>(queueDefinition.CollectionName);

        var queueItem = new MongoQueueItem<TPayload>
        {
            CreatedUtc = _timeProvider.GetUtcNow().UtcDateTime,
            Payload = payload,
            Id = ObjectId.GenerateNewId()
        };

        await collection.InsertOneAsync(queueItem, null, cancellationToken);
        MongoQueueDiagnostics.RecordPublished(queueDefinition);
    }
}
