// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests.Queues;

using Chaos.Mongo.Queues;
using Chaos.Testing.Logging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Search;
using Moq;
using NUnit.Framework;

public class MongoQueueSubscriptionFactoryTests
{
    [Test]
    public void Constructor_WhenLoggerFactoryIsNull_ThrowsArgumentNullException()
    {
        // Arrange
        var mockMongoHelper = new Mock<IMongoHelper>();
        var mockPayloadHandlerFactory = new Mock<IMongoQueuePayloadHandlerFactory>();
        var mockPayloadPrioritizer = new Mock<IMongoQueuePayloadPrioritizer>();
        var serviceScopeFactory = CreateServiceScopeFactory();
        var timeProvider = TimeProvider.System;

        // Act
        var act = () => new MongoQueueSubscriptionFactory(
            mockMongoHelper.Object,
            mockPayloadHandlerFactory.Object,
            mockPayloadPrioritizer.Object,
            serviceScopeFactory,
            timeProvider,
            null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Constructor_WhenMongoHelperIsNull_ThrowsArgumentNullException()
    {
        // Arrange
        var mockPayloadHandlerFactory = new Mock<IMongoQueuePayloadHandlerFactory>();
        var mockPayloadPrioritizer = new Mock<IMongoQueuePayloadPrioritizer>();
        var serviceScopeFactory = CreateServiceScopeFactory();
        var timeProvider = TimeProvider.System;
        var loggerFactory = CreateLoggerFactory();

        // Act
        var act = () => new MongoQueueSubscriptionFactory(
            null!,
            mockPayloadHandlerFactory.Object,
            mockPayloadPrioritizer.Object,
            serviceScopeFactory,
            timeProvider,
            loggerFactory);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Constructor_WhenPayloadHandlerFactoryIsNull_ThrowsArgumentNullException()
    {
        // Arrange
        var mockMongoHelper = new Mock<IMongoHelper>();
        var mockPayloadPrioritizer = new Mock<IMongoQueuePayloadPrioritizer>();
        var serviceScopeFactory = CreateServiceScopeFactory();
        var timeProvider = TimeProvider.System;
        var loggerFactory = CreateLoggerFactory();

        // Act
        var act = () => new MongoQueueSubscriptionFactory(
            mockMongoHelper.Object,
            null!,
            mockPayloadPrioritizer.Object,
            serviceScopeFactory,
            timeProvider,
            loggerFactory);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Constructor_WhenPayloadPrioritizerIsNull_ThrowsArgumentNullException()
    {
        // Arrange
        var mockMongoHelper = new Mock<IMongoHelper>();
        var mockPayloadHandlerFactory = new Mock<IMongoQueuePayloadHandlerFactory>();
        var serviceScopeFactory = CreateServiceScopeFactory();
        var timeProvider = TimeProvider.System;
        var loggerFactory = CreateLoggerFactory();

        // Act
        var act = () => new MongoQueueSubscriptionFactory(
            mockMongoHelper.Object,
            mockPayloadHandlerFactory.Object,
            null!,
            serviceScopeFactory,
            timeProvider,
            loggerFactory);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Constructor_WhenServiceScopeFactoryIsNull_ThrowsArgumentNullException()
    {
        // Arrange
        var mongoHelper = new ThrowingMongoHelper();
        var payloadHandlerFactory = new ThrowingPayloadHandlerFactory();
        var payloadPrioritizer = new ThrowingPayloadPrioritizer();
        var timeProvider = TimeProvider.System;
        var loggerFactory = CreateLoggerFactory();

        // Act
        var act = () => new MongoQueueSubscriptionFactory(
            mongoHelper,
            payloadHandlerFactory,
            payloadPrioritizer,
            null!,
            timeProvider,
            loggerFactory);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Constructor_WithValidParameters_SuccessfullyCreatesInstance()
    {
        // Arrange
        var mockMongoHelper = new Mock<IMongoHelper>();
        var mockPayloadHandlerFactory = new Mock<IMongoQueuePayloadHandlerFactory>();
        var mockPayloadPrioritizer = new Mock<IMongoQueuePayloadPrioritizer>();
        var serviceScopeFactory = CreateServiceScopeFactory();
        var timeProvider = TimeProvider.System;
        var loggerFactory = CreateLoggerFactory();

        // Act
        var factory = new MongoQueueSubscriptionFactory(
            mockMongoHelper.Object,
            mockPayloadHandlerFactory.Object,
            mockPayloadPrioritizer.Object,
            serviceScopeFactory,
            timeProvider,
            loggerFactory);

        // Assert
        factory.Should().NotBeNull();
    }

    [Test]
    public async Task CreateAndRunAsync_WhenQueueDefinitionIsNull_ThrowsArgumentNullException()
    {
        // Arrange
        var mockMongoHelper = new Mock<IMongoHelper>();
        var mockPayloadHandlerFactory = new Mock<IMongoQueuePayloadHandlerFactory>();
        var mockPayloadPrioritizer = new Mock<IMongoQueuePayloadPrioritizer>();
        var serviceScopeFactory = CreateServiceScopeFactory();
        var timeProvider = TimeProvider.System;
        var loggerFactory = CreateLoggerFactory();
        var factory = new MongoQueueSubscriptionFactory(
            mockMongoHelper.Object,
            mockPayloadHandlerFactory.Object,
            mockPayloadPrioritizer.Object,
            serviceScopeFactory,
            timeProvider,
            loggerFactory);

        // Act
        var act = async () => await factory.CreateAndRunAsync<TestPayload>(null!);

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Test]
    public async Task CreateAndRunAsync_WithPreCancelledToken_PropagatesTokenToRealStartupPath()
    {
        // Arrange
        var indexManager = new RecordingIndexManager<MongoQueueItem<TestPayload>>();
        var collection = new RecordingMongoCollection<MongoQueueItem<TestPayload>>(indexManager);
        var database = new RecordingMongoDatabase("test-queue", collection);
        var mongoHelper = new RecordingMongoHelper(database);

        var payloadHandlerFactory = new ThrowingPayloadHandlerFactory();
        var payloadPrioritizer = new ThrowingPayloadPrioritizer();
        var serviceScopeFactory = CreateServiceScopeFactory();
        var loggerFactory = CreateLoggerFactory();
        var factory = new MongoQueueSubscriptionFactory(
            mongoHelper,
            payloadHandlerFactory,
            payloadPrioritizer,
            serviceScopeFactory,
            TimeProvider.System,
            loggerFactory);

        var queueDefinition = new MongoQueueDefinition
        {
            AutoStartSubscription = false,
            RetentionPeriod = null,
            CollectionName = "test-queue",
            LockLeaseTime = TimeSpan.FromMinutes(1),
            PayloadHandlerType = typeof(IMongoQueuePayloadHandler<TestPayload>),
            PayloadType = typeof(TestPayload),
            BatchSize = 1
        };
        CancellationToken cancellationToken;
        using (var cancellationTokenSource = new CancellationTokenSource())
        {
            await cancellationTokenSource.CancelAsync();
            cancellationToken = cancellationTokenSource.Token;
        }

        // Act
        var act = async () => await factory.CreateAndRunAsync<TestPayload>(queueDefinition, cancellationToken);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        indexManager.ReceivedCancellationToken.Should().Be(cancellationToken);
        indexManager.CreateOneCallCount.Should().Be(1);
    }

    private static ILoggerFactory CreateLoggerFactory()
    {
        var services = new ServiceCollection();
        services.AddNUnitTestLogging();
        return services.BuildServiceProvider().GetRequiredService<ILoggerFactory>();
    }

    private static IServiceScopeFactory CreateServiceScopeFactory()
        => new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

    public class TestPayload;

    /// <summary>
    /// Hand-crafted recording double for <see cref="IMongoIndexManager{TDocument}"/>, built on the driver's
    /// <see cref="MongoIndexManagerBase{TDocument}"/> to avoid introducing new mocking-framework usage per
    /// tests/AGENTS.md. Only
    /// <see cref="CreateOneAsync(CreateIndexModel{TDocument}, CreateOneIndexOptions?, CancellationToken)"/>
    /// is exercised by the test that uses it.
    /// </summary>
    private sealed class RecordingIndexManager<TDocument> : MongoIndexManagerBase<TDocument>
    {
        public override CollectionNamespace CollectionNamespace => throw new NotSupportedException();
        public Int32 CreateOneCallCount { get; private set; }

        public override IBsonSerializer<TDocument> DocumentSerializer => throw new NotSupportedException();

        public CancellationToken ReceivedCancellationToken { get; private set; }

        public override MongoCollectionSettings Settings => throw new NotSupportedException();

        public override Task<String?> CreateOneAsync(CreateIndexModel<TDocument> model, CreateOneIndexOptions? options = null, CancellationToken cancellationToken = default)
        {
            ReceivedCancellationToken = cancellationToken;
            CreateOneCallCount++;
            return cancellationToken.IsCancellationRequested
                ? Task.FromCanceled<String?>(cancellationToken)
                : Task.FromResult<String?>("index-name");
        }

        public override Task DropAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public override Task DropOneAsync(String name, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public override Task<IAsyncCursor<BsonDocument>> ListAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>
    /// Hand-crafted recording double for <see cref="IMongoCollection{TDocument}"/>. Only <see cref="Indexes"/>
    /// is exercised by the real startup path under test; every other member is unreachable and throws
    /// deliberately.
    /// </summary>
    private sealed class RecordingMongoCollection<TDocument> : IMongoCollection<TDocument>
    {
        public RecordingMongoCollection(IMongoIndexManager<TDocument> indexes) => Indexes = indexes;

        public CollectionNamespace CollectionNamespace => throw new NotSupportedException();
        public IMongoDatabase Database => throw new NotSupportedException();
        public IBsonSerializer<TDocument> DocumentSerializer => throw new NotSupportedException();

        public IMongoIndexManager<TDocument> Indexes { get; }
        public IMongoSearchIndexManager SearchIndexes => throw new NotSupportedException();
        public MongoCollectionSettings Settings => throw new NotSupportedException();

        public IAsyncCursor<TResult> Aggregate<TResult>(PipelineDefinition<TDocument, TResult> pipeline, AggregateOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public IAsyncCursor<TResult> Aggregate<TResult>(IClientSessionHandle session, PipelineDefinition<TDocument, TResult> pipeline, AggregateOptions options,
                                                        CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IAsyncCursor<TResult>>
            AggregateAsync<TResult>(PipelineDefinition<TDocument, TResult> pipeline, AggregateOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IAsyncCursor<TResult>> AggregateAsync<TResult>(IClientSessionHandle session, PipelineDefinition<TDocument, TResult> pipeline, AggregateOptions options,
                                                                   CancellationToken cancellationToken) => throw new NotSupportedException();

        public void AggregateToCollection<TResult>(PipelineDefinition<TDocument, TResult> pipeline, AggregateOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void AggregateToCollection<TResult>(IClientSessionHandle session, PipelineDefinition<TDocument, TResult> pipeline, AggregateOptions options,
                                                   CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task AggregateToCollectionAsync<TResult>(PipelineDefinition<TDocument, TResult> pipeline, AggregateOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AggregateToCollectionAsync<TResult>(IClientSessionHandle session, PipelineDefinition<TDocument, TResult> pipeline, AggregateOptions options,
                                                        CancellationToken cancellationToken) => throw new NotSupportedException();

        public BulkWriteResult<TDocument> BulkWrite(IEnumerable<WriteModel<TDocument>> requests, BulkWriteOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public BulkWriteResult<TDocument> BulkWrite(IClientSessionHandle session, IEnumerable<WriteModel<TDocument>> requests, BulkWriteOptions options,
                                                    CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<BulkWriteResult<TDocument>> BulkWriteAsync(IEnumerable<WriteModel<TDocument>> requests, BulkWriteOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<BulkWriteResult<TDocument>> BulkWriteAsync(IClientSessionHandle session, IEnumerable<WriteModel<TDocument>> requests, BulkWriteOptions options,
                                                               CancellationToken cancellationToken) => throw new NotSupportedException();

        public Int64 Count(FilterDefinition<TDocument> filter, CountOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Int64 Count(IClientSessionHandle session, FilterDefinition<TDocument> filter, CountOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Int64> CountAsync(FilterDefinition<TDocument> filter, CountOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Int64> CountAsync(IClientSessionHandle session, FilterDefinition<TDocument> filter, CountOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Int64 CountDocuments(FilterDefinition<TDocument> filter, CountOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Int64 CountDocuments(IClientSessionHandle session, FilterDefinition<TDocument> filter, CountOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Int64> CountDocumentsAsync(FilterDefinition<TDocument> filter, CountOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Int64> CountDocumentsAsync(IClientSessionHandle session, FilterDefinition<TDocument> filter, CountOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public DeleteResult DeleteMany(FilterDefinition<TDocument> filter, CancellationToken cancellationToken) => throw new NotSupportedException();
        public DeleteResult DeleteMany(FilterDefinition<TDocument> filter, DeleteOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public DeleteResult DeleteMany(IClientSessionHandle session, FilterDefinition<TDocument> filter, DeleteOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DeleteResult> DeleteManyAsync(FilterDefinition<TDocument> filter, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DeleteResult> DeleteManyAsync(FilterDefinition<TDocument> filter, DeleteOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DeleteResult> DeleteManyAsync(IClientSessionHandle session, FilterDefinition<TDocument> filter, DeleteOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public DeleteResult DeleteOne(FilterDefinition<TDocument> filter, CancellationToken cancellationToken) => throw new NotSupportedException();
        public DeleteResult DeleteOne(FilterDefinition<TDocument> filter, DeleteOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public DeleteResult DeleteOne(IClientSessionHandle session, FilterDefinition<TDocument> filter, DeleteOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DeleteResult> DeleteOneAsync(FilterDefinition<TDocument> filter, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DeleteResult> DeleteOneAsync(FilterDefinition<TDocument> filter, DeleteOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DeleteResult> DeleteOneAsync(IClientSessionHandle session, FilterDefinition<TDocument> filter, DeleteOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public IAsyncCursor<TField> Distinct<TField>(FieldDefinition<TDocument, TField> field, FilterDefinition<TDocument> filter, DistinctOptions options,
                                                     CancellationToken cancellationToken) => throw new NotSupportedException();

        public IAsyncCursor<TField> Distinct<TField>(IClientSessionHandle session, FieldDefinition<TDocument, TField> field, FilterDefinition<TDocument> filter,
                                                     DistinctOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IAsyncCursor<TField>> DistinctAsync<TField>(FieldDefinition<TDocument, TField> field, FilterDefinition<TDocument> filter, DistinctOptions options,
                                                                CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IAsyncCursor<TField>> DistinctAsync<TField>(IClientSessionHandle session, FieldDefinition<TDocument, TField> field, FilterDefinition<TDocument> filter,
                                                                DistinctOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public IAsyncCursor<TItem> DistinctMany<TItem>(FieldDefinition<TDocument, IEnumerable<TItem>> field, FilterDefinition<TDocument> filter, DistinctOptions options,
                                                       CancellationToken cancellationToken) => throw new NotSupportedException();

        public IAsyncCursor<TItem> DistinctMany<TItem>(IClientSessionHandle session, FieldDefinition<TDocument, IEnumerable<TItem>> field, FilterDefinition<TDocument> filter,
                                                       DistinctOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IAsyncCursor<TItem>> DistinctManyAsync<TItem>(FieldDefinition<TDocument, IEnumerable<TItem>> field, FilterDefinition<TDocument> filter, DistinctOptions options,
                                                                  CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IAsyncCursor<TItem>> DistinctManyAsync<TItem>(IClientSessionHandle session, FieldDefinition<TDocument, IEnumerable<TItem>> field,
                                                                  FilterDefinition<TDocument> filter, DistinctOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Int64 EstimatedDocumentCount(EstimatedDocumentCountOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Int64> EstimatedDocumentCountAsync(EstimatedDocumentCountOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IAsyncCursor<TProjection>> FindAsync<TProjection>(FilterDefinition<TDocument> filter, FindOptions<TDocument, TProjection> options,
                                                                      CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IAsyncCursor<TProjection>> FindAsync<TProjection>(IClientSessionHandle session, FilterDefinition<TDocument> filter, FindOptions<TDocument, TProjection> options,
                                                                      CancellationToken cancellationToken) => throw new NotSupportedException();

        public TProjection FindOneAndDelete<TProjection>(FilterDefinition<TDocument> filter, FindOneAndDeleteOptions<TDocument, TProjection> options,
                                                         CancellationToken cancellationToken) => throw new NotSupportedException();

        public TProjection FindOneAndDelete<TProjection>(IClientSessionHandle session, FilterDefinition<TDocument> filter, FindOneAndDeleteOptions<TDocument, TProjection> options,
                                                         CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TProjection> FindOneAndDeleteAsync<TProjection>(FilterDefinition<TDocument> filter, FindOneAndDeleteOptions<TDocument, TProjection> options,
                                                                    CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TProjection> FindOneAndDeleteAsync<TProjection>(IClientSessionHandle session, FilterDefinition<TDocument> filter,
                                                                    FindOneAndDeleteOptions<TDocument, TProjection> options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public TProjection FindOneAndReplace<TProjection>(FilterDefinition<TDocument> filter, TDocument replacement, FindOneAndReplaceOptions<TDocument, TProjection> options,
                                                          CancellationToken cancellationToken) => throw new NotSupportedException();

        public TProjection FindOneAndReplace<TProjection>(IClientSessionHandle session, FilterDefinition<TDocument> filter, TDocument replacement,
                                                          FindOneAndReplaceOptions<TDocument, TProjection> options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TProjection> FindOneAndReplaceAsync<TProjection>(FilterDefinition<TDocument> filter, TDocument replacement,
                                                                     FindOneAndReplaceOptions<TDocument, TProjection> options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TProjection> FindOneAndReplaceAsync<TProjection>(IClientSessionHandle session, FilterDefinition<TDocument> filter, TDocument replacement,
                                                                     FindOneAndReplaceOptions<TDocument, TProjection> options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public TProjection FindOneAndUpdate<TProjection>(FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update,
                                                         FindOneAndUpdateOptions<TDocument, TProjection> options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public TProjection FindOneAndUpdate<TProjection>(IClientSessionHandle session, FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update,
                                                         FindOneAndUpdateOptions<TDocument, TProjection> options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TProjection> FindOneAndUpdateAsync<TProjection>(FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update,
                                                                    FindOneAndUpdateOptions<TDocument, TProjection> options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TProjection> FindOneAndUpdateAsync<TProjection>(IClientSessionHandle session, FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update,
                                                                    FindOneAndUpdateOptions<TDocument, TProjection> options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public IAsyncCursor<TProjection>
            FindSync<TProjection>(FilterDefinition<TDocument> filter, FindOptions<TDocument, TProjection> options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public IAsyncCursor<TProjection> FindSync<TProjection>(IClientSessionHandle session, FilterDefinition<TDocument> filter, FindOptions<TDocument, TProjection> options,
                                                               CancellationToken cancellationToken) => throw new NotSupportedException();

        public void InsertMany(IEnumerable<TDocument> documents, InsertManyOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void InsertMany(IClientSessionHandle session, IEnumerable<TDocument> documents, InsertManyOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task InsertManyAsync(IEnumerable<TDocument> documents, InsertManyOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task InsertManyAsync(IClientSessionHandle session, IEnumerable<TDocument> documents, InsertManyOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void InsertOne(TDocument document, InsertOneOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void InsertOne(IClientSessionHandle session, TDocument document, InsertOneOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task InsertOneAsync(TDocument document, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task InsertOneAsync(TDocument document, InsertOneOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task InsertOneAsync(IClientSessionHandle session, TDocument document, InsertOneOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public IAsyncCursor<TResult> MapReduce<TResult>(BsonJavaScript map, BsonJavaScript reduce, MapReduceOptions<TDocument, TResult> options,
                                                        CancellationToken cancellationToken) => throw new NotSupportedException();

        public IAsyncCursor<TResult> MapReduce<TResult>(IClientSessionHandle session, BsonJavaScript map, BsonJavaScript reduce, MapReduceOptions<TDocument, TResult> options,
                                                        CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IAsyncCursor<TResult>> MapReduceAsync<TResult>(BsonJavaScript map, BsonJavaScript reduce, MapReduceOptions<TDocument, TResult> options,
                                                                   CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IAsyncCursor<TResult>> MapReduceAsync<TResult>(IClientSessionHandle session, BsonJavaScript map, BsonJavaScript reduce,
                                                                   MapReduceOptions<TDocument, TResult> options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public IFilteredMongoCollection<TDerivedDocument> OfType<TDerivedDocument>() where TDerivedDocument : TDocument => throw new NotSupportedException();

        public ReplaceOneResult ReplaceOne(FilterDefinition<TDocument> filter, TDocument replacement, ReplaceOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ReplaceOneResult ReplaceOne(FilterDefinition<TDocument> filter, TDocument replacement, UpdateOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ReplaceOneResult ReplaceOne(IClientSessionHandle session, FilterDefinition<TDocument> filter, TDocument replacement, ReplaceOptions options,
                                           CancellationToken cancellationToken) => throw new NotSupportedException();

        public ReplaceOneResult ReplaceOne(IClientSessionHandle session, FilterDefinition<TDocument> filter, TDocument replacement, UpdateOptions options,
                                           CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ReplaceOneResult> ReplaceOneAsync(FilterDefinition<TDocument> filter, TDocument replacement, ReplaceOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ReplaceOneResult> ReplaceOneAsync(FilterDefinition<TDocument> filter, TDocument replacement, UpdateOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ReplaceOneResult> ReplaceOneAsync(IClientSessionHandle session, FilterDefinition<TDocument> filter, TDocument replacement, ReplaceOptions options,
                                                      CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ReplaceOneResult> ReplaceOneAsync(IClientSessionHandle session, FilterDefinition<TDocument> filter, TDocument replacement, UpdateOptions options,
                                                      CancellationToken cancellationToken) => throw new NotSupportedException();

        public UpdateResult UpdateMany(FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update, UpdateOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public UpdateResult UpdateMany(IClientSessionHandle session, FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update, UpdateOptions options,
                                       CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<UpdateResult> UpdateManyAsync(FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update, UpdateOptions options,
                                                  CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<UpdateResult> UpdateManyAsync(IClientSessionHandle session, FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update, UpdateOptions options,
                                                  CancellationToken cancellationToken) => throw new NotSupportedException();

        public UpdateResult UpdateOne(FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update, UpdateOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public UpdateResult UpdateOne(IClientSessionHandle session, FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update, UpdateOptions options,
                                      CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<UpdateResult>
            UpdateOneAsync(FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update, UpdateOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<UpdateResult> UpdateOneAsync(IClientSessionHandle session, FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update, UpdateOptions options,
                                                 CancellationToken cancellationToken) => throw new NotSupportedException();

        public IChangeStreamCursor<TResult> Watch<TResult>(PipelineDefinition<ChangeStreamDocument<TDocument>, TResult> pipeline, ChangeStreamOptions options,
                                                           CancellationToken cancellationToken) => throw new NotSupportedException();

        public IChangeStreamCursor<TResult> Watch<TResult>(IClientSessionHandle session, PipelineDefinition<ChangeStreamDocument<TDocument>, TResult> pipeline,
                                                           ChangeStreamOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IChangeStreamCursor<TResult>> WatchAsync<TResult>(PipelineDefinition<ChangeStreamDocument<TDocument>, TResult> pipeline, ChangeStreamOptions options,
                                                                      CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IChangeStreamCursor<TResult>> WatchAsync<TResult>(IClientSessionHandle session, PipelineDefinition<ChangeStreamDocument<TDocument>, TResult> pipeline,
                                                                      ChangeStreamOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public IMongoCollection<TDocument> WithReadConcern(ReadConcern readConcern) => throw new NotSupportedException();
        public IMongoCollection<TDocument> WithReadPreference(ReadPreference readPreference) => throw new NotSupportedException();
        public IMongoCollection<TDocument> WithWriteConcern(WriteConcern writeConcern) => throw new NotSupportedException();
    }

    /// <summary>
    /// Hand-crafted recording double for <see cref="IMongoDatabase"/>. Only <see cref="GetCollection{TDocument}"/>
    /// is exercised by the real startup path under test; every other member is unreachable and throws
    /// deliberately.
    /// </summary>
    private sealed class RecordingMongoDatabase : IMongoDatabase
    {
        private readonly IMongoCollection<MongoQueueItem<TestPayload>> _collection;
        private readonly String _collectionName;

        public RecordingMongoDatabase(String collectionName, IMongoCollection<MongoQueueItem<TestPayload>> collection)
        {
            _collectionName = collectionName;
            _collection = collection;
        }

        public IMongoClient Client => throw new NotSupportedException();
        public DatabaseNamespace DatabaseNamespace => throw new NotSupportedException();
        public MongoDatabaseSettings Settings => throw new NotSupportedException();

        public IAsyncCursor<TResult> Aggregate<TResult>(PipelineDefinition<NoPipelineInput, TResult> pipeline, AggregateOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public IAsyncCursor<TResult> Aggregate<TResult>(IClientSessionHandle session, PipelineDefinition<NoPipelineInput, TResult> pipeline, AggregateOptions options,
                                                        CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IAsyncCursor<TResult>> AggregateAsync<TResult>(PipelineDefinition<NoPipelineInput, TResult> pipeline, AggregateOptions options,
                                                                   CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IAsyncCursor<TResult>> AggregateAsync<TResult>(IClientSessionHandle session, PipelineDefinition<NoPipelineInput, TResult> pipeline, AggregateOptions options,
                                                                   CancellationToken cancellationToken) => throw new NotSupportedException();

        public void AggregateToCollection<TResult>(PipelineDefinition<NoPipelineInput, TResult> pipeline, AggregateOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void AggregateToCollection<TResult>(IClientSessionHandle session, PipelineDefinition<NoPipelineInput, TResult> pipeline, AggregateOptions options,
                                                   CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task AggregateToCollectionAsync<TResult>(PipelineDefinition<NoPipelineInput, TResult> pipeline, AggregateOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AggregateToCollectionAsync<TResult>(IClientSessionHandle session, PipelineDefinition<NoPipelineInput, TResult> pipeline, AggregateOptions options,
                                                        CancellationToken cancellationToken) => throw new NotSupportedException();

        public void CreateCollection(String name, CreateCollectionOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void CreateCollection(IClientSessionHandle session, String name, CreateCollectionOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CreateCollectionAsync(String name, CreateCollectionOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CreateCollectionAsync(IClientSessionHandle session, String name, CreateCollectionOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void CreateView<TDocument, TResult>(String viewName, String viewOn, PipelineDefinition<TDocument, TResult> pipeline, CreateViewOptions<TDocument> options,
                                                   CancellationToken cancellationToken) => throw new NotSupportedException();

        public void CreateView<TDocument, TResult>(IClientSessionHandle session, String viewName, String viewOn, PipelineDefinition<TDocument, TResult> pipeline,
                                                   CreateViewOptions<TDocument> options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CreateViewAsync<TDocument, TResult>(String viewName, String viewOn, PipelineDefinition<TDocument, TResult> pipeline, CreateViewOptions<TDocument> options,
                                                        CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CreateViewAsync<TDocument, TResult>(IClientSessionHandle session, String viewName, String viewOn, PipelineDefinition<TDocument, TResult> pipeline,
                                                        CreateViewOptions<TDocument> options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void DropCollection(String name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void DropCollection(String name, DropCollectionOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void DropCollection(IClientSessionHandle session, String name, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void DropCollection(IClientSessionHandle session, String name, DropCollectionOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DropCollectionAsync(String name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DropCollectionAsync(String name, DropCollectionOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DropCollectionAsync(IClientSessionHandle session, String name, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DropCollectionAsync(IClientSessionHandle session, String name, DropCollectionOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public IMongoCollection<TDocument> GetCollection<TDocument>(String name, MongoCollectionSettings? settings = null)
        {
            if (name == _collectionName && _collection is IMongoCollection<TDocument> typedCollection)
            {
                return typedCollection;
            }

            throw new NotSupportedException();
        }

        public IAsyncCursor<String> ListCollectionNames(ListCollectionNamesOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public IAsyncCursor<String> ListCollectionNames(IClientSessionHandle session, ListCollectionNamesOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IAsyncCursor<String>> ListCollectionNamesAsync(ListCollectionNamesOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IAsyncCursor<String>> ListCollectionNamesAsync(IClientSessionHandle session, ListCollectionNamesOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public IAsyncCursor<BsonDocument> ListCollections(ListCollectionsOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public IAsyncCursor<BsonDocument> ListCollections(IClientSessionHandle session, ListCollectionsOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IAsyncCursor<BsonDocument>> ListCollectionsAsync(ListCollectionsOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IAsyncCursor<BsonDocument>> ListCollectionsAsync(IClientSessionHandle session, ListCollectionsOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void RenameCollection(String oldName, String newName, RenameCollectionOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void RenameCollection(IClientSessionHandle session, String oldName, String newName, RenameCollectionOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RenameCollectionAsync(String oldName, String newName, RenameCollectionOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RenameCollectionAsync(IClientSessionHandle session, String oldName, String newName, RenameCollectionOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public TResult RunCommand<TResult>(Command<TResult> command, ReadPreference readPreference, CancellationToken cancellationToken) => throw new NotSupportedException();

        public TResult RunCommand<TResult>(IClientSessionHandle session, Command<TResult> command, ReadPreference readPreference, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TResult> RunCommandAsync<TResult>(Command<TResult> command, ReadPreference readPreference, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TResult> RunCommandAsync<TResult>(IClientSessionHandle session, Command<TResult> command, ReadPreference readPreference, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public IChangeStreamCursor<TResult> Watch<TResult>(PipelineDefinition<ChangeStreamDocument<BsonDocument>, TResult> pipeline, ChangeStreamOptions options,
                                                           CancellationToken cancellationToken) => throw new NotSupportedException();

        public IChangeStreamCursor<TResult> Watch<TResult>(IClientSessionHandle session, PipelineDefinition<ChangeStreamDocument<BsonDocument>, TResult> pipeline,
                                                           ChangeStreamOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IChangeStreamCursor<TResult>> WatchAsync<TResult>(PipelineDefinition<ChangeStreamDocument<BsonDocument>, TResult> pipeline, ChangeStreamOptions options,
                                                                      CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IChangeStreamCursor<TResult>> WatchAsync<TResult>(IClientSessionHandle session, PipelineDefinition<ChangeStreamDocument<BsonDocument>, TResult> pipeline,
                                                                      ChangeStreamOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public IMongoDatabase WithReadConcern(ReadConcern readConcern) => throw new NotSupportedException();
        public IMongoDatabase WithReadPreference(ReadPreference readPreference) => throw new NotSupportedException();
        public IMongoDatabase WithWriteConcern(WriteConcern writeConcern) => throw new NotSupportedException();
    }

    /// <summary>
    /// Hand-crafted recording double for <see cref="IMongoHelper"/>. Only <see cref="Database"/> is exercised
    /// by the real startup path under test; every other member is unreachable and throws deliberately.
    /// </summary>
    private sealed class RecordingMongoHelper : IMongoHelper
    {
        public RecordingMongoHelper(IMongoDatabase database) => Database = database;

        public IMongoClient Client => throw new NotSupportedException();

        public IMongoDatabase Database { get; }

        public IMongoCollection<TDocument> GetCollection<TDocument>(MongoCollectionSettings? settings = null) => throw new NotSupportedException();

        public Task<IMongoLock?> TryAcquireLockAsync(String lockName, TimeSpan? leaseTime = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// Hand-crafted stub for <see cref="IMongoHelper"/>. Not exercised by the constructor-guard test that uses
    /// it; the constructor's earlier null checks never invoke it before the test's targeted argument is validated.
    /// </summary>
    private sealed class ThrowingMongoHelper : IMongoHelper
    {
        public IMongoClient Client => throw new NotSupportedException();
        public IMongoDatabase Database => throw new NotSupportedException();
        public IMongoCollection<TDocument> GetCollection<TDocument>(MongoCollectionSettings? settings = null) => throw new NotSupportedException();

        public Task<IMongoLock?> TryAcquireLockAsync(String lockName, TimeSpan? leaseTime = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// Hand-crafted stub for <see cref="IMongoQueuePayloadHandlerFactory"/>. Not exercised by the real startup
    /// path under test; cancellation is observed before any payload handler would be created.
    /// </summary>
    private sealed class ThrowingPayloadHandlerFactory : IMongoQueuePayloadHandlerFactory
    {
        public IMongoQueuePayloadHandler<TPayload> CreateHandler<TPayload>(IServiceProvider serviceProvider) where TPayload : class, new()
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Hand-crafted stub for <see cref="IMongoQueuePayloadPrioritizer"/>. Not exercised by the real startup
    /// path under test; cancellation is observed before any sort definition would be created.
    /// </summary>
    private sealed class ThrowingPayloadPrioritizer : IMongoQueuePayloadPrioritizer
    {
        public SortDefinition<MongoQueueItem<TPayload>> CreateSortDefinition<TPayload>() where TPayload : class, new()
            => throw new NotSupportedException();
    }
}
