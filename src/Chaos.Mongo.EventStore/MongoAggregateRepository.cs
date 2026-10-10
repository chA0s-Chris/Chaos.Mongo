// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore;

using Chaos.Mongo.EventStore.Integrity;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

/// <summary>
/// MongoDB-backed repository for reading aggregate state.
/// </summary>
/// <typeparam name="TAggregate">The aggregate type.</typeparam>
public sealed class MongoAggregateRepository<TAggregate> : IAggregateRepository<TAggregate>
    where TAggregate : class, IAggregate, new()
{
    private readonly IMongoHelper _mongoHelper;
    private readonly FilterDefinition<Event<TAggregate>> _observationalEventExclusion;
    private readonly MongoEventStoreOptions<TAggregate> _options;

    public MongoAggregateRepository(IMongoHelper mongoHelper, MongoEventStoreOptions<TAggregate> options)
    {
        ArgumentNullException.ThrowIfNull(mongoHelper);
        ArgumentNullException.ThrowIfNull(options);
        _mongoHelper = mongoHelper;
        _options = options;
        _observationalEventExclusion = CreateObservationalEventExclusion(options.ObservationalEventDiscriminators);
    }

    /// <inheritdoc/>
    public IMongoCollection<TAggregate> Collection
        => _mongoHelper.Database.GetCollection<TAggregate>(_options.ReadModelCollectionName);

    private IMongoCollection<Event<TAggregate>> EventsCollection
        => _mongoHelper.Database.GetCollection<Event<TAggregate>>(_options.EventsCollectionName);

    /// <summary>
    /// Creates a filter matching events above <paramref name="revision"/>, the complement of
    /// <see cref="AtOrBelowRevision"/>.
    /// </summary>
    private static FilterDefinition<Event<TAggregate>> AboveRevision(Int64 revision)
    {
        var filter = Builders<Event<TAggregate>>.Filter;
        return filter.Gt(e => e.Revision, revision) |
               (filter.Exists(e => e.Revision, false) & filter.Gt(e => e.Version, revision));
    }

    /// <summary>
    /// Creates a filter matching events at or below <paramref name="revision"/>. Events stored before revisions
    /// existed lack the element; their revision equals their version.
    /// </summary>
    private static FilterDefinition<Event<TAggregate>> AtOrBelowRevision(Int64 revision)
    {
        var filter = Builders<Event<TAggregate>>.Filter;
        return filter.Lte(e => e.Revision, revision) |
               (filter.Exists(e => e.Revision, false) & filter.Lte(e => e.Version, revision));
    }

    /// <summary>
    /// Creates a filter that excludes events whose concrete type is a registered observational event type.
    /// The concrete discriminator is the last element of a hierarchical discriminator array, or the scalar
    /// discriminator itself. Matching only the concrete discriminator keeps unknown descendants of
    /// observational types in the results, so they still fail during deserialization.
    /// </summary>
    private static FilterDefinition<Event<TAggregate>> CreateObservationalEventExclusion(IReadOnlyList<String> discriminators)
    {
        if (discriminators.Count == 0)
        {
            return Builders<Event<TAggregate>>.Filter.Empty;
        }

        var discriminatorElement = $"${GetDiscriminatorElementName()}";
        var concreteDiscriminator = new BsonDocument(
            "$cond",
            new BsonArray
            {
                new BsonDocument("$isArray", discriminatorElement),
                new BsonDocument("$arrayElemAt", new BsonArray
                {
                    discriminatorElement,
                    -1
                }),
                discriminatorElement
            });

        return new BsonDocumentFilterDefinition<Event<TAggregate>>(
            new BsonDocument(
                "$expr",
                new BsonDocument(
                    "$not",
                    new BsonArray
                    {
                        new BsonDocument("$in", new BsonArray
                        {
                            concreteDiscriminator,
                            new BsonDocument("$literal", new BsonArray(discriminators))
                        })
                    })));
    }

    /// <summary>
    /// Resolves the discriminator element name through the event serializer. Unlike
    /// <see cref="BsonSerializer.LookupDiscriminatorConvention"/>, this lets the class map register the
    /// hierarchical convention of its root class first; looking the convention up directly before the first
    /// event is serialized would cache the scalar convention and change how every event is stored.
    /// </summary>
    private static String GetDiscriminatorElementName()
        => BsonSerializer.LookupSerializer<Event<TAggregate>>() is IHasDiscriminatorConvention { DiscriminatorConvention: { } convention }
            ? convention.ElementName
            : BsonSerializer.LookupDiscriminatorConvention(typeof(Event<TAggregate>)).ElementName;

    /// <summary>
    /// Creates the replay filter of <see cref="GetAtRevisionAsync"/>. Only the <c>(AggregateId, Version)</c> index
    /// exists, so a revision filter alone would scan to the end of the stream. Revisions never decrease along the
    /// stream, so the first event above the target bounds the replay; it is found by an index scan that stops at
    /// that event. The revision filter itself always remains part of the replay, so the bound is only an
    /// optimization and events appended in the meantime cannot change the result.
    /// </summary>
    private async Task<FilterDefinition<Event<TAggregate>>> CreateRevisionTargetFilterAsync(
        Guid aggregateId,
        Int64 revision,
        Int64 fromVersion,
        CancellationToken cancellationToken)
    {
        var targetFilter = AtOrBelowRevision(revision);

        // A target at or above the stream's revision has no event above it; searching would scan the whole range.
        if (revision >= await ReadStreamRevisionAsync(aggregateId, cancellationToken))
        {
            return targetFilter;
        }

        var filter = Builders<Event<TAggregate>>.Filter;
        var versionElement = EventDocumentFields<TAggregate>.Version;
        var firstAbove = await EventsCollection
                               .Find(filter.Eq(e => e.AggregateId, aggregateId) &
                                     filter.Gte(e => e.Version, fromVersion) &
                                     AboveRevision(revision))
                               .SortBy(e => e.Version)
                               .Limit(1)
                               .Project(Builders<Event<TAggregate>>.Projection.Include(versionElement).Exclude("_id"))
                               .FirstOrDefaultAsync(cancellationToken);

        return firstAbove is null
            ? targetFilter
            : targetFilter & filter.Lt(e => e.Version, firstAbove[versionElement].ToInt64());
    }

    /// <summary>
    /// Reads the normalized revision of the last event in the stream through an indexed point read, or <c>0</c>
    /// for an empty stream.
    /// </summary>
    private async Task<Int64> ReadStreamRevisionAsync(Guid aggregateId, CancellationToken cancellationToken)
    {
        var versionElement = EventDocumentFields<TAggregate>.Version;
        var revisionElement = EventDocumentFields<TAggregate>.Revision;

        var lastEvent = await EventsCollection
                              .Find(Builders<Event<TAggregate>>.Filter.Eq(e => e.AggregateId, aggregateId))
                              .SortByDescending(e => e.Version)
                              .Limit(1)
                              .Project(Builders<Event<TAggregate>>.Projection
                                                                  .Include(versionElement)
                                                                  .Include(revisionElement)
                                                                  .Exclude("_id"))
                              .FirstOrDefaultAsync(cancellationToken);

        if (lastEvent is null)
        {
            return 0;
        }

        var revision = lastEvent.TryGetValue(revisionElement, out var revisionValue) ? revisionValue.ToInt64() : 0;
        return LegacyRevision.Of(revision, lastEvent[versionElement].ToInt64());
    }

    /// <summary>
    /// Loads the nearest checkpoint matching <paramref name="checkpointFilter"/> and replays the state-changing
    /// events after it that match the filter created by <paramref name="createTargetFilterAsync"/>, which receives
    /// the first position to replay.
    /// </summary>
    private async Task<TAggregate?> ReconstructAsync(
        Guid aggregateId,
        FilterDefinition<CheckpointDocument<TAggregate>> checkpointFilter,
        SortDefinition<CheckpointDocument<TAggregate>> checkpointSort,
        Func<Int64, CancellationToken, Task<FilterDefinition<Event<TAggregate>>>> createTargetFilterAsync,
        CancellationToken cancellationToken)
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
                                         checkpointFilter)
                                   .Sort(checkpointSort)
                                   .FirstOrDefaultAsync(cancellationToken);

            if (checkpoint is not null)
            {
                LegacyRevision.Normalize(checkpoint);
                aggregate = checkpoint.State;
                fromVersion = checkpoint.Id.Version + 1;
            }
        }

        // Load the state-changing events from the checkpoint (or the beginning) up to the target
        var targetFilter = await createTargetFilterAsync(fromVersion, cancellationToken);
        var events = await EventsCollection
                           .Find(Builders<Event<TAggregate>>.Filter.Eq(e => e.AggregateId, aggregateId) &
                                 Builders<Event<TAggregate>>.Filter.Gte(e => e.Version, fromVersion) &
                                 targetFilter &
                                 _observationalEventExclusion)
                           .SortBy(e => e.Version)
                           .ToListAsync(cancellationToken);

        if (aggregate is null && events.Count == 0)
            return null;

        aggregate ??= new TAggregate
        {
            Id = aggregateId,
            CreatedUtc = events[0].CreatedUtc
        };

        foreach (var evt in events)
        {
            // The server-side filter is the primary exclusion; this guard keeps replay correct if it does not apply.
            if (evt is ObservationalEvent<TAggregate>)
                continue;

            LegacyRevision.Normalize(evt);
            evt.Execute(aggregate);
            aggregate.Version = evt.Version;
            aggregate.Revision = evt.Revision;
        }

        return aggregate;
    }

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
    public Task<TAggregate?> GetAtRevisionAsync(Guid aggregateId, Int64 revision, CancellationToken cancellationToken = default)
        => ReconstructAsync(
            aggregateId,
            Builders<CheckpointDocument<TAggregate>>.Filter.Lte(c => c.Revision, revision),
            Builders<CheckpointDocument<TAggregate>>.Sort.Descending(c => c.Revision),
            (fromVersion, ct) => CreateRevisionTargetFilterAsync(aggregateId, revision, fromVersion, ct),
            cancellationToken);

    /// <inheritdoc/>
    public Task<TAggregate?> GetAtVersionAsync(Guid aggregateId, Int64 version, CancellationToken cancellationToken = default)
        => ReconstructAsync(
            aggregateId,
            Builders<CheckpointDocument<TAggregate>>.Filter.Lte(c => c.Id.Version, version),
            Builders<CheckpointDocument<TAggregate>>.Sort.Descending(c => c.Id.Version),
            (_, _) => Task.FromResult(Builders<Event<TAggregate>>.Filter.Lte(e => e.Version, version)),
            cancellationToken);
}
