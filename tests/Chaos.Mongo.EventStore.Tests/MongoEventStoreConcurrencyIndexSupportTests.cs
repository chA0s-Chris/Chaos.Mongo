// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests;

using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using NUnit.Framework;

/// <summary>
/// Covers <see cref="MongoEventStoreConcurrencyIndexSupport"/>'s shared-task caching directly, without a real
/// MongoDB server, so the cancellation race between concurrent callers is deterministic.
/// </summary>
public class MongoEventStoreConcurrencyIndexSupportTests
{
    [Test]
    public async Task EnsureIndexAsync_WhenInitiatingCallerCancels_DoesNotPoisonSharedCheckForConcurrentWaiterOrLaterCallers()
    {
        var indexManager = new ControllableIndexManager();
        var eventsCollection = MongoCollectionProxy.Create(indexManager);
        var client = new InertMongoClient();

        using var initiatingCancellation = new CancellationTokenSource();
        var initiatingTask = MongoEventStoreConcurrencyIndexSupport.EnsureIndexAsync(client, eventsCollection, initiatingCancellation.Token);

        // Join the same shared check while it is still in flight, as a concurrent append would.
        await indexManager.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var concurrentWaiterTask = MongoEventStoreConcurrencyIndexSupport.EnsureIndexAsync(client, eventsCollection, CancellationToken.None);

        await initiatingCancellation.CancelAsync();

        var initiatingAct = async () => await initiatingTask.WaitAsync(TimeSpan.FromSeconds(5));
        await initiatingAct.Should().ThrowAsync<OperationCanceledException>();

        // The shared check itself must not have observed the initiating caller's cancellation.
        indexManager.Complete(IndexNames.AggregateIdWithVersionUnique);

        await concurrentWaiterTask.WaitAsync(TimeSpan.FromSeconds(5));

        // A later, uncanceled caller reuses the already-succeeded shared check instead of retrying it.
        await MongoEventStoreConcurrencyIndexSupport.EnsureIndexAsync(client, eventsCollection, CancellationToken.None);

        indexManager.CreateOneAsyncCallCount.Should().Be(1);
    }

    public sealed class TestAggregate : IAggregate
    {
        public DateTime CreatedUtc { get; set; }
        public Guid Id { get; set; }
        public Int64 Revision { get; set; }
        public Int64 Version { get; set; }
    }

    private sealed class ControllableIndexManager : NotSupportedMongoIndexManager<Event<TestAggregate>>
    {
        private readonly TaskCompletionSource<String> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Int32 CreateOneAsyncCallCount { get; private set; }

        public TaskCompletionSource<Boolean> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Complete(String indexName) => _completion.TrySetResult(indexName);

        public override Task<String> CreateOneAsync(CreateIndexModel<Event<TestAggregate>> model, CreateOneIndexOptions options, CancellationToken cancellationToken)
        {
            CreateOneAsyncCallCount++;
            // Mirrors the real driver: cancellation of a token passed into the underlying wire call cancels the
            // returned task, regardless of which caller supplied that token.
            cancellationToken.Register(() => _completion.TrySetCanceled(cancellationToken));
            Started.TrySetResult(true);
            return _completion.Task;
        }
    }

    /// <summary>
    /// A test double for <see cref="IMongoClient"/> that throws <see cref="NotSupportedException"/> for every
    /// member. This test uses the client only as the identity key of <see cref="MongoEventStoreConcurrencyIndexSupport"/>'s
    /// shared-check cache; it never calls any client member. Implemented directly, instead of mocked, per
    /// tests/AGENTS.md's prohibition on new mocking-framework usage.
    /// </summary>
    private sealed class InertMongoClient : IMongoClient
    {
        public ICluster Cluster => throw Unsupported(nameof(Cluster));

        public MongoClientSettings Settings => throw Unsupported(nameof(Settings));

        private static NotSupportedException Unsupported(String member)
            => new($"Member '{member}' is not supported by the test client.");

        public void Dispose() { }

        public ClientBulkWriteResult BulkWrite(IReadOnlyList<BulkWriteModel> models, ClientBulkWriteOptions options, CancellationToken cancellationToken)
            => throw Unsupported(nameof(BulkWrite));

        public ClientBulkWriteResult BulkWrite(IClientSessionHandle session, IReadOnlyList<BulkWriteModel> models, ClientBulkWriteOptions options,
                                               CancellationToken cancellationToken)
            => throw Unsupported(nameof(BulkWrite));

        public Task<ClientBulkWriteResult> BulkWriteAsync(IReadOnlyList<BulkWriteModel> models, ClientBulkWriteOptions options, CancellationToken cancellationToken)
            => throw Unsupported(nameof(BulkWriteAsync));

        public Task<ClientBulkWriteResult> BulkWriteAsync(IClientSessionHandle session, IReadOnlyList<BulkWriteModel> models, ClientBulkWriteOptions options,
                                                          CancellationToken cancellationToken)
            => throw Unsupported(nameof(BulkWriteAsync));

        public void DropDatabase(String name, CancellationToken cancellationToken)
            => throw Unsupported(nameof(DropDatabase));

        public void DropDatabase(IClientSessionHandle session, String name, CancellationToken cancellationToken)
            => throw Unsupported(nameof(DropDatabase));

        public Task DropDatabaseAsync(String name, CancellationToken cancellationToken)
            => throw Unsupported(nameof(DropDatabaseAsync));

        public Task DropDatabaseAsync(IClientSessionHandle session, String name, CancellationToken cancellationToken)
            => throw Unsupported(nameof(DropDatabaseAsync));

        public IMongoDatabase GetDatabase(String name, MongoDatabaseSettings settings)
            => throw Unsupported(nameof(GetDatabase));

        public IAsyncCursor<String> ListDatabaseNames(CancellationToken cancellationToken)
            => throw Unsupported(nameof(ListDatabaseNames));

        public IAsyncCursor<String> ListDatabaseNames(ListDatabaseNamesOptions options, CancellationToken cancellationToken)
            => throw Unsupported(nameof(ListDatabaseNames));

        public IAsyncCursor<String> ListDatabaseNames(IClientSessionHandle session, CancellationToken cancellationToken)
            => throw Unsupported(nameof(ListDatabaseNames));

        public IAsyncCursor<String> ListDatabaseNames(IClientSessionHandle session, ListDatabaseNamesOptions options, CancellationToken cancellationToken)
            => throw Unsupported(nameof(ListDatabaseNames));

        public Task<IAsyncCursor<String>> ListDatabaseNamesAsync(CancellationToken cancellationToken)
            => throw Unsupported(nameof(ListDatabaseNamesAsync));

        public Task<IAsyncCursor<String>> ListDatabaseNamesAsync(ListDatabaseNamesOptions options, CancellationToken cancellationToken)
            => throw Unsupported(nameof(ListDatabaseNamesAsync));

        public Task<IAsyncCursor<String>> ListDatabaseNamesAsync(IClientSessionHandle session, CancellationToken cancellationToken)
            => throw Unsupported(nameof(ListDatabaseNamesAsync));

        public Task<IAsyncCursor<String>> ListDatabaseNamesAsync(IClientSessionHandle session, ListDatabaseNamesOptions options, CancellationToken cancellationToken)
            => throw Unsupported(nameof(ListDatabaseNamesAsync));

        public IAsyncCursor<BsonDocument> ListDatabases(CancellationToken cancellationToken)
            => throw Unsupported(nameof(ListDatabases));

        public IAsyncCursor<BsonDocument> ListDatabases(ListDatabasesOptions options, CancellationToken cancellationToken)
            => throw Unsupported(nameof(ListDatabases));

        public IAsyncCursor<BsonDocument> ListDatabases(IClientSessionHandle session, CancellationToken cancellationToken)
            => throw Unsupported(nameof(ListDatabases));

        public IAsyncCursor<BsonDocument> ListDatabases(IClientSessionHandle session, ListDatabasesOptions options, CancellationToken cancellationToken)
            => throw Unsupported(nameof(ListDatabases));

        public Task<IAsyncCursor<BsonDocument>> ListDatabasesAsync(CancellationToken cancellationToken)
            => throw Unsupported(nameof(ListDatabasesAsync));

        public Task<IAsyncCursor<BsonDocument>> ListDatabasesAsync(ListDatabasesOptions options, CancellationToken cancellationToken)
            => throw Unsupported(nameof(ListDatabasesAsync));

        public Task<IAsyncCursor<BsonDocument>> ListDatabasesAsync(IClientSessionHandle session, CancellationToken cancellationToken)
            => throw Unsupported(nameof(ListDatabasesAsync));

        public Task<IAsyncCursor<BsonDocument>> ListDatabasesAsync(IClientSessionHandle session, ListDatabasesOptions options, CancellationToken cancellationToken)
            => throw Unsupported(nameof(ListDatabasesAsync));

        public IClientSessionHandle StartSession(ClientSessionOptions options, CancellationToken cancellationToken)
            => throw Unsupported(nameof(StartSession));

        public Task<IClientSessionHandle> StartSessionAsync(ClientSessionOptions options, CancellationToken cancellationToken)
            => throw Unsupported(nameof(StartSessionAsync));

        public IChangeStreamCursor<TResult> Watch<TResult>(PipelineDefinition<ChangeStreamDocument<BsonDocument>, TResult> pipeline, ChangeStreamOptions options,
                                                           CancellationToken cancellationToken)
            => throw Unsupported(nameof(Watch));

        public IChangeStreamCursor<TResult> Watch<TResult>(IClientSessionHandle session, PipelineDefinition<ChangeStreamDocument<BsonDocument>, TResult> pipeline,
                                                           ChangeStreamOptions options, CancellationToken cancellationToken)
            => throw Unsupported(nameof(Watch));

        public Task<IChangeStreamCursor<TResult>> WatchAsync<TResult>(PipelineDefinition<ChangeStreamDocument<BsonDocument>, TResult> pipeline, ChangeStreamOptions options,
                                                                      CancellationToken cancellationToken)
            => throw Unsupported(nameof(WatchAsync));

        public Task<IChangeStreamCursor<TResult>> WatchAsync<TResult>(IClientSessionHandle session, PipelineDefinition<ChangeStreamDocument<BsonDocument>, TResult> pipeline,
                                                                      ChangeStreamOptions options, CancellationToken cancellationToken)
            => throw Unsupported(nameof(WatchAsync));

        public IMongoClient WithReadConcern(ReadConcern readConcern)
            => throw Unsupported(nameof(WithReadConcern));

        public IMongoClient WithReadPreference(ReadPreference readPreference)
            => throw Unsupported(nameof(WithReadPreference));

        public IMongoClient WithWriteConcern(WriteConcern writeConcern)
            => throw Unsupported(nameof(WithWriteConcern));
    }

    private sealed class MongoCollectionProxy : NotSupportedMongoCollection<Event<TestAggregate>>
    {
        private MongoCollectionProxy(IMongoIndexManager<Event<TestAggregate>> indexManager) => Indexes = indexManager;

        public override CollectionNamespace CollectionNamespace { get; } = new(new DatabaseNamespace("Tests"), "Orders_Events");

        public override IMongoIndexManager<Event<TestAggregate>> Indexes { get; }

        public static IMongoCollection<Event<TestAggregate>> Create(IMongoIndexManager<Event<TestAggregate>> indexManager)
            => new MongoCollectionProxy(indexManager);
    }
}
