// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore;

using MongoDB.Driver;

/// <summary>
/// MongoDB-backed repository for reading aggregate state.
/// </summary>
/// <typeparam name="TAggregate">The aggregate type.</typeparam>
public sealed class MongoAggregateRepository<TAggregate> : IAggregateRepository<TAggregate>
    where TAggregate : class, IAggregate, new()
{
    private readonly IMongoHelper _mongoHelper;
    private readonly MongoEventStoreOptions<TAggregate> _options;

    public MongoAggregateRepository(IMongoHelper mongoHelper, MongoEventStoreOptions<TAggregate> options)
    {
        ArgumentNullException.ThrowIfNull(mongoHelper);
        ArgumentNullException.ThrowIfNull(options);
        _mongoHelper = mongoHelper;
        _options = options;
    }

    /// <inheritdoc/>
    public IMongoCollection<TAggregate> Collection
        => _mongoHelper.Database.GetCollection<TAggregate>(_options.ReadModelCollectionName);

    /// <inheritdoc/>
    public async Task<TAggregate?> GetAsync(Guid aggregateId, CancellationToken cancellationToken = default)
    {
        var aggregate = await Collection
                              .Find(Builders<TAggregate>.Filter.Eq(a => a.Id, aggregateId))
                              .FirstOrDefaultAsync(cancellationToken);

        if (aggregate is not null)
        {
            LegacyRevision.Normalize(aggregate);
        }

        return aggregate;
    }

    /// <inheritdoc/>
    public async Task<TAggregate?> GetAtVersionAsync(Guid aggregateId, Int64 version, CancellationToken cancellationToken = default)
    {
        // Try to load from checkpoint if available
        TAggregate? aggregate = null;
        Int64 fromVersion = 1;

        if (_options.CheckpointsEnabled)
        {
            var checkpointCollection = _mongoHelper.Database
                                                   .GetCollection<CheckpointDocument<TAggregate>>(_options.CheckpointCollectionName);

            var checkpoint = await checkpointCollection
                                   .Find(Builders<CheckpointDocument<TAggregate>>.Filter.Eq(c => c.Id.AggregateId, aggregateId) &
                                         Builders<CheckpointDocument<TAggregate>>.Filter.Lte(c => c.Id.Version, version))
                                   .SortByDescending(c => c.Id.Version)
                                   .FirstOrDefaultAsync(cancellationToken);

            if (checkpoint is not null)
            {
                LegacyRevision.Normalize(checkpoint);
                aggregate = checkpoint.State;
                fromVersion = checkpoint.Id.Version + 1;
            }
        }

        // Load events from checkpoint version (or beginning) up to target version
        var eventsCollection = _mongoHelper.Database.GetCollection<Event<TAggregate>>(_options.EventsCollectionName);
        var events = await eventsCollection
                           .Find(Builders<Event<TAggregate>>.Filter.Eq(e => e.AggregateId, aggregateId) &
                                 Builders<Event<TAggregate>>.Filter.Gte(e => e.Version, fromVersion) &
                                 Builders<Event<TAggregate>>.Filter.Lte(e => e.Version, version))
                           .SortBy(e => e.Version)
                           .ToListAsync(cancellationToken);

        if (aggregate is null && events.Count == 0)
            return null;

        aggregate ??= new TAggregate
        {
            Id = aggregateId,
            CreatedUtc = events[0].CreatedUtc
        };

        // Observational events do not change the aggregate, so replay skips them. The aggregate's version
        // and revision are those of the last state-changing event, as in the read model.
        foreach (var evt in events)
        {
            if (evt is ObservationalEvent<TAggregate>)
                continue;

            LegacyRevision.Normalize(evt);
            evt.Execute(aggregate);
            aggregate.Version = evt.Version;
            aggregate.Revision = evt.Revision;
        }

        return aggregate;
    }
}
