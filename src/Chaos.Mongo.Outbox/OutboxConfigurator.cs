// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox;

using Chaos.Mongo.Configuration;
using MongoDB.Driver;

/// <summary>
/// Creates required indexes on the outbox collection at startup.
/// </summary>
public sealed class OutboxConfigurator : IMongoConfigurator
{
    private readonly OutboxOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="OutboxConfigurator"/> class.
    /// </summary>
    /// <param name="options">The outbox configuration options.</param>
    public OutboxConfigurator(OutboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc/>
    public async Task ConfigureAsync(IMongoHelper helper, CancellationToken cancellationToken = default)
    {
        var collection = helper.Database.GetCollection<OutboxMessage>(_options.CollectionName);

        // Primary polling index: { NextAttemptUtc, LockedUtc, _id } with partial filter State == Pending
        var pollingIndex = new CreateIndexModel<OutboxMessage>(
            Builders<OutboxMessage>.IndexKeys
                                   .Ascending(m => m.NextAttemptUtc)
                                   .Ascending(m => m.LockedUtc)
                                   .Ascending(m => m.Id),
            new CreateIndexOptions<OutboxMessage>
            {
                Name = "IX_Outbox_Polling",
                PartialFilterExpression = Builders<OutboxMessage>.Filter.Eq(m => m.State, OutboxMessageState.Pending)
            });

        await collection.Indexes.CreateOneOrUpdateAsync(pollingIndex, cancellationToken: cancellationToken);

        // TTL indexes for processed and failed messages (only if retention period is configured)
        if (_options.RetentionPeriod.HasValue)
        {
            var processedTtlIndex = new CreateIndexModel<OutboxMessage>(
                Builders<OutboxMessage>.IndexKeys.Ascending(m => m.ProcessedUtc),
                new CreateIndexOptions<OutboxMessage>
                {
                    Name = "IX_Outbox_ProcessedUtc_TTL",
                    ExpireAfter = _options.RetentionPeriod.Value,
                    PartialFilterExpression = Builders<OutboxMessage>.Filter.Ne(m => m.ProcessedUtc, null)
                });

            await collection.Indexes.CreateOneOrUpdateAsync(processedTtlIndex, cancellationToken: cancellationToken);

            var failedTtlIndex = new CreateIndexModel<OutboxMessage>(
                Builders<OutboxMessage>.IndexKeys.Ascending(m => m.FailedUtc),
                new CreateIndexOptions<OutboxMessage>
                {
                    Name = "IX_Outbox_FailedUtc_TTL",
                    ExpireAfter = _options.RetentionPeriod.Value,
                    PartialFilterExpression = Builders<OutboxMessage>.Filter.Ne(m => m.FailedUtc, null)
                });

            await collection.Indexes.CreateOneOrUpdateAsync(failedTtlIndex, cancellationToken: cancellationToken);
        }
    }
}
