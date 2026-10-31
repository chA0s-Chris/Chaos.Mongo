// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore;

using Chaos.Mongo.EventStore.Errors;
using MongoDB.Driver;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

/// <summary>
/// Ensures the unique concurrency index exists on an events collection before an append relies on it, independent
/// of whether configurators have run. Caches a successful check per <see cref="IMongoClient"/> and collection
/// namespace: a single client can serve multiple databases, and ensuring the index for one database's events
/// collection must not suppress the check for a same-named events collection in another.
/// </summary>
internal static class MongoEventStoreConcurrencyIndexSupport
{
    private static readonly ConditionalWeakTable<IMongoClient, ConcurrentDictionary<CollectionNamespace, Lazy<Task>>> VerifiedNamespaces = new();

    public static async Task EnsureIndexAsync<TAggregate>(
        IMongoClient client,
        IMongoCollection<Event<TAggregate>> eventsCollection,
        CancellationToken cancellationToken)
        where TAggregate : class, IAggregate, new()
    {
        cancellationToken.ThrowIfCancellationRequested();

        var namespaces = VerifiedNamespaces.GetValue(client, _ => new ConcurrentDictionary<CollectionNamespace, Lazy<Task>>());
        var collectionNamespace = eventsCollection.CollectionNamespace;
        var lazyCheck = namespaces.GetOrAdd(
            collectionNamespace,
            _ => new Lazy<Task>(() => CreateIndexAsync(eventsCollection)));

        try
        {
            await lazyCheck.Value.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested &&
                                                 !lazyCheck.Value.IsFaulted)
        {
            // This caller's own wait was canceled while the shared check is still healthy,
            // so leave the cache entry in place for the other callers awaiting it.
            throw;
        }
        catch
        {
            // The shared check itself failed: evict it so the next append retries. Only remove
            // the entry we awaited, in case a concurrent caller already installed a fresh one.
            namespaces.TryRemove(KeyValuePair.Create(collectionNamespace, lazyCheck));
            throw;
        }
    }

    private static async Task CreateIndexAsync<TAggregate>(IMongoCollection<Event<TAggregate>> eventsCollection)
        where TAggregate : class, IAggregate, new()
    {
        var indexModel = EventStoreIndexes.CreateConcurrencyIndexModel<TAggregate>();

        try
        {
            // Deliberately not tied to any individual caller's cancellation token: this task is shared by every
            // concurrent appender waiting on the same namespace, and one caller canceling must not cancel the
            // shared check for the others. Each caller's own wait is still cancelable via WaitAsync below.
            await eventsCollection.Indexes.CreateOneAsync(indexModel);
        }
        catch (MongoCommandException ex) when (ex.Code == 13) // Unauthorized
        {
            throw new MongoConcurrencyIndexMissingException(
                $"The unique concurrency index '{IndexNames.AggregateIdWithVersionUnique}' on " +
                $"'{eventsCollection.CollectionNamespace}' could not be verified or created because the connection " +
                "lacks permission to create indexes. Appends are unsafe without this index. Either grant 'createIndex' " +
                "on the events collection, provision the index once with an elevated-privilege connection (for example " +
                "by running configurators with RunConfiguratorsOnStartup or IMongoConfiguratorRunner), or set " +
                $"{nameof(MongoEventStoreOptions<TAggregate>.VerifyConcurrencyIndexOnAppend)} to false once the index " +
                "is confirmed to exist.",
                ex);
        }
        catch (MongoCommandException ex) when (ex.CodeName is "IndexOptionsConflict" or "IndexKeySpecsConflict")
        {
            var conflictingName = ex.Command["indexes"][0]["name"].AsString;
            throw new MongoConcurrencyIndexMissingException(
                $"An index named '{conflictingName}' already exists on '{eventsCollection.CollectionNamespace}' with " +
                "different keys or options than the unique concurrency index. Appends are unsafe without the exact " +
                "concurrency index, and it was left intact instead of being replaced. Reconcile the conflicting index " +
                "manually, for example by dropping it and running configurators with an elevated-privilege connection.",
                ex);
        }
    }
}
