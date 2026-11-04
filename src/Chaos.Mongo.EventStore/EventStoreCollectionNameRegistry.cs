// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore;

/// <summary>
/// Tracks the collection names claimed by every aggregate type registered via
/// <see cref="MongoBuilderExtensions.WithEventStore{TAggregate}"/> on a given <see cref="MongoBuilder"/>, so a
/// second aggregate type resolving to one of the same names fails registration instead of silently sharing data.
/// </summary>
internal sealed class EventStoreCollectionNameRegistry
{
    private readonly List<(Type AggregateType, IReadOnlyList<String> CollectionNames)> _aggregates = [];

    /// <summary>
    /// Throws when <paramref name="collectionNames"/> shares a name with a previously committed aggregate type.
    /// Checking is separate from <see cref="Commit"/> so a call that fails a later check, after this one passed,
    /// leaves no claim behind for a corrected retry of the same aggregate type.
    /// </summary>
    /// <param name="aggregateType">The aggregate type being registered.</param>
    /// <param name="collectionNames">The collection names <paramref name="aggregateType"/> resolves to.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="aggregateType"/> shares a collection name with an already committed aggregate type.
    /// </exception>
    public void CheckForCollisions(Type aggregateType, IReadOnlyList<String> collectionNames)
    {
        ArgumentNullException.ThrowIfNull(aggregateType);
        ArgumentNullException.ThrowIfNull(collectionNames);

        foreach (var (registeredType, registeredNames) in _aggregates)
        {
            var sharedName = registeredNames.FirstOrDefault(collectionNames.Contains);
            if (sharedName is not null)
            {
                throw new InvalidOperationException(
                    $"Event-store aggregate types '{registeredType.FullName ?? registeredType.Name}' and " +
                    $"'{aggregateType.FullName ?? aggregateType.Name}' both resolve to the collection '{sharedName}'. Two event-store " +
                    "aggregates must not share a collection name; configure a distinct collection prefix for one of them with " +
                    "WithCollectionPrefix(string).");
            }
        }
    }

    /// <summary>
    /// Commits <paramref name="collectionNames"/> as claimed by <paramref name="aggregateType"/>, so a later
    /// registration's <see cref="CheckForCollisions"/> call detects an overlap with them.
    /// </summary>
    /// <param name="aggregateType">The aggregate type being registered.</param>
    /// <param name="collectionNames">The collection names <paramref name="aggregateType"/> resolves to.</param>
    public void Commit(Type aggregateType, IReadOnlyList<String> collectionNames)
    {
        ArgumentNullException.ThrowIfNull(aggregateType);
        ArgumentNullException.ThrowIfNull(collectionNames);

        _aggregates.Add((aggregateType, collectionNames));
    }
}
