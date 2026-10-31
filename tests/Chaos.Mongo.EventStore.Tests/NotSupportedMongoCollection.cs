// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests;

using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Search;

/// <summary>
/// A test double base for <see cref="IMongoCollection{TDocument}"/> that throws <see cref="NotSupportedException"/>
/// for every member; override only the ones a test exercises. Implemented directly, instead of through
/// <see cref="System.Reflection.DispatchProxy"/> or Moq, because both require a dynamically generated proxy
/// assembly that cannot access an internal <typeparamref name="TDocument"/>, such as
/// <see cref="CheckpointDocument{TAggregate}"/>.
/// </summary>
public abstract class NotSupportedMongoCollection<TDocument> : IMongoCollection<TDocument>
{
    public virtual CollectionNamespace CollectionNamespace => throw Unsupported(nameof(CollectionNamespace));

    public virtual IMongoDatabase Database => throw Unsupported(nameof(Database));

    public virtual IBsonSerializer<TDocument> DocumentSerializer => throw Unsupported(nameof(DocumentSerializer));

    public virtual IMongoIndexManager<TDocument> Indexes => throw Unsupported(nameof(Indexes));

    public virtual IMongoSearchIndexManager SearchIndexes => throw Unsupported(nameof(SearchIndexes));

    public virtual MongoCollectionSettings Settings => throw Unsupported(nameof(Settings));

    private static NotSupportedException Unsupported(String member)
        => new($"Member '{member}' is not supported by the test collection.");

    public virtual IAsyncCursor<TResult> Aggregate<TResult>(PipelineDefinition<TDocument, TResult> pipeline, AggregateOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(Aggregate));

    public virtual IAsyncCursor<TResult> Aggregate<TResult>(IClientSessionHandle session, PipelineDefinition<TDocument, TResult> pipeline, AggregateOptions options,
                                                            CancellationToken cancellationToken)
        => throw Unsupported(nameof(Aggregate));

    public virtual Task<IAsyncCursor<TResult>> AggregateAsync<TResult>(PipelineDefinition<TDocument, TResult> pipeline, AggregateOptions options,
                                                                       CancellationToken cancellationToken)
        => throw Unsupported(nameof(AggregateAsync));

    public virtual Task<IAsyncCursor<TResult>> AggregateAsync<TResult>(IClientSessionHandle session, PipelineDefinition<TDocument, TResult> pipeline, AggregateOptions options,
                                                                       CancellationToken cancellationToken)
        => throw Unsupported(nameof(AggregateAsync));

    public virtual void AggregateToCollection<TResult>(PipelineDefinition<TDocument, TResult> pipeline, AggregateOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(AggregateToCollection));

    public virtual void AggregateToCollection<TResult>(IClientSessionHandle session, PipelineDefinition<TDocument, TResult> pipeline, AggregateOptions options,
                                                       CancellationToken cancellationToken)
        => throw Unsupported(nameof(AggregateToCollection));

    public virtual Task AggregateToCollectionAsync<TResult>(PipelineDefinition<TDocument, TResult> pipeline, AggregateOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(AggregateToCollectionAsync));

    public virtual Task AggregateToCollectionAsync<TResult>(IClientSessionHandle session, PipelineDefinition<TDocument, TResult> pipeline, AggregateOptions options,
                                                            CancellationToken cancellationToken)
        => throw Unsupported(nameof(AggregateToCollectionAsync));

    public virtual BulkWriteResult<TDocument> BulkWrite(IEnumerable<WriteModel<TDocument>> requests, BulkWriteOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(BulkWrite));

    public virtual BulkWriteResult<TDocument> BulkWrite(IClientSessionHandle session, IEnumerable<WriteModel<TDocument>> requests, BulkWriteOptions options,
                                                        CancellationToken cancellationToken)
        => throw Unsupported(nameof(BulkWrite));

    public virtual Task<BulkWriteResult<TDocument>> BulkWriteAsync(IEnumerable<WriteModel<TDocument>> requests, BulkWriteOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(BulkWriteAsync));

    public virtual Task<BulkWriteResult<TDocument>> BulkWriteAsync(IClientSessionHandle session, IEnumerable<WriteModel<TDocument>> requests, BulkWriteOptions options,
                                                                   CancellationToken cancellationToken)
        => throw Unsupported(nameof(BulkWriteAsync));

    public virtual Int64 Count(FilterDefinition<TDocument> filter, CountOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(Count));

    public virtual Int64 Count(IClientSessionHandle session, FilterDefinition<TDocument> filter, CountOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(Count));

    public virtual Task<Int64> CountAsync(FilterDefinition<TDocument> filter, CountOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(CountAsync));

    public virtual Task<Int64> CountAsync(IClientSessionHandle session, FilterDefinition<TDocument> filter, CountOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(CountAsync));

    public virtual Int64 CountDocuments(FilterDefinition<TDocument> filter, CountOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(CountDocuments));

    public virtual Int64 CountDocuments(IClientSessionHandle session, FilterDefinition<TDocument> filter, CountOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(CountDocuments));

    public virtual Task<Int64> CountDocumentsAsync(FilterDefinition<TDocument> filter, CountOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(CountDocumentsAsync));

    public virtual Task<Int64> CountDocumentsAsync(IClientSessionHandle session, FilterDefinition<TDocument> filter, CountOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(CountDocumentsAsync));

    public virtual DeleteResult DeleteMany(FilterDefinition<TDocument> filter, CancellationToken cancellationToken)
        => throw Unsupported(nameof(DeleteMany));

    public virtual DeleteResult DeleteMany(FilterDefinition<TDocument> filter, DeleteOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(DeleteMany));

    public virtual DeleteResult DeleteMany(IClientSessionHandle session, FilterDefinition<TDocument> filter, DeleteOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(DeleteMany));

    public virtual Task<DeleteResult> DeleteManyAsync(FilterDefinition<TDocument> filter, CancellationToken cancellationToken)
        => throw Unsupported(nameof(DeleteManyAsync));

    public virtual Task<DeleteResult> DeleteManyAsync(FilterDefinition<TDocument> filter, DeleteOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(DeleteManyAsync));

    public virtual Task<DeleteResult> DeleteManyAsync(IClientSessionHandle session, FilterDefinition<TDocument> filter, DeleteOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(DeleteManyAsync));

    public virtual DeleteResult DeleteOne(FilterDefinition<TDocument> filter, CancellationToken cancellationToken)
        => throw Unsupported(nameof(DeleteOne));

    public virtual DeleteResult DeleteOne(FilterDefinition<TDocument> filter, DeleteOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(DeleteOne));

    public virtual DeleteResult DeleteOne(IClientSessionHandle session, FilterDefinition<TDocument> filter, DeleteOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(DeleteOne));

    public virtual Task<DeleteResult> DeleteOneAsync(FilterDefinition<TDocument> filter, CancellationToken cancellationToken)
        => throw Unsupported(nameof(DeleteOneAsync));

    public virtual Task<DeleteResult> DeleteOneAsync(FilterDefinition<TDocument> filter, DeleteOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(DeleteOneAsync));

    public virtual Task<DeleteResult> DeleteOneAsync(IClientSessionHandle session, FilterDefinition<TDocument> filter, DeleteOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(DeleteOneAsync));

    public virtual IAsyncCursor<TField> Distinct<TField>(FieldDefinition<TDocument, TField> field, FilterDefinition<TDocument> filter, DistinctOptions options,
                                                         CancellationToken cancellationToken)
        => throw Unsupported(nameof(Distinct));

    public virtual IAsyncCursor<TField> Distinct<TField>(IClientSessionHandle session, FieldDefinition<TDocument, TField> field, FilterDefinition<TDocument> filter,
                                                         DistinctOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(Distinct));

    public virtual Task<IAsyncCursor<TField>> DistinctAsync<TField>(FieldDefinition<TDocument, TField> field, FilterDefinition<TDocument> filter, DistinctOptions options,
                                                                    CancellationToken cancellationToken)
        => throw Unsupported(nameof(DistinctAsync));

    public virtual Task<IAsyncCursor<TField>> DistinctAsync<TField>(IClientSessionHandle session, FieldDefinition<TDocument, TField> field, FilterDefinition<TDocument> filter,
                                                                    DistinctOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(DistinctAsync));

    public virtual IAsyncCursor<TItem> DistinctMany<TItem>(FieldDefinition<TDocument, IEnumerable<TItem>> field, FilterDefinition<TDocument> filter, DistinctOptions options,
                                                           CancellationToken cancellationToken)
        => throw Unsupported(nameof(DistinctMany));

    public virtual IAsyncCursor<TItem> DistinctMany<TItem>(IClientSessionHandle session, FieldDefinition<TDocument, IEnumerable<TItem>> field, FilterDefinition<TDocument> filter,
                                                           DistinctOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(DistinctMany));

    public virtual Task<IAsyncCursor<TItem>> DistinctManyAsync<TItem>(FieldDefinition<TDocument, IEnumerable<TItem>> field, FilterDefinition<TDocument> filter,
                                                                      DistinctOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(DistinctManyAsync));

    public virtual Task<IAsyncCursor<TItem>> DistinctManyAsync<TItem>(IClientSessionHandle session, FieldDefinition<TDocument, IEnumerable<TItem>> field,
                                                                      FilterDefinition<TDocument> filter, DistinctOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(DistinctManyAsync));

    public virtual Int64 EstimatedDocumentCount(EstimatedDocumentCountOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(EstimatedDocumentCount));

    public virtual Task<Int64> EstimatedDocumentCountAsync(EstimatedDocumentCountOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(EstimatedDocumentCountAsync));

    public virtual Task<IAsyncCursor<TProjection>> FindAsync<TProjection>(FilterDefinition<TDocument> filter, FindOptions<TDocument, TProjection> options,
                                                                          CancellationToken cancellationToken)
        => throw Unsupported(nameof(FindAsync));

    public virtual Task<IAsyncCursor<TProjection>> FindAsync<TProjection>(IClientSessionHandle session, FilterDefinition<TDocument> filter,
                                                                          FindOptions<TDocument, TProjection> options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(FindAsync));

    public virtual TProjection FindOneAndDelete<TProjection>(FilterDefinition<TDocument> filter, FindOneAndDeleteOptions<TDocument, TProjection> options,
                                                             CancellationToken cancellationToken)
        => throw Unsupported(nameof(FindOneAndDelete));

    public virtual TProjection FindOneAndDelete<TProjection>(IClientSessionHandle session, FilterDefinition<TDocument> filter,
                                                             FindOneAndDeleteOptions<TDocument, TProjection> options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(FindOneAndDelete));

    public virtual Task<TProjection> FindOneAndDeleteAsync<TProjection>(FilterDefinition<TDocument> filter, FindOneAndDeleteOptions<TDocument, TProjection> options,
                                                                        CancellationToken cancellationToken)
        => throw Unsupported(nameof(FindOneAndDeleteAsync));

    public virtual Task<TProjection> FindOneAndDeleteAsync<TProjection>(IClientSessionHandle session, FilterDefinition<TDocument> filter,
                                                                        FindOneAndDeleteOptions<TDocument, TProjection> options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(FindOneAndDeleteAsync));

    public virtual TProjection FindOneAndReplace<TProjection>(FilterDefinition<TDocument> filter, TDocument replacement, FindOneAndReplaceOptions<TDocument, TProjection> options,
                                                              CancellationToken cancellationToken)
        => throw Unsupported(nameof(FindOneAndReplace));

    public virtual TProjection FindOneAndReplace<TProjection>(IClientSessionHandle session, FilterDefinition<TDocument> filter, TDocument replacement,
                                                              FindOneAndReplaceOptions<TDocument, TProjection> options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(FindOneAndReplace));

    public virtual Task<TProjection> FindOneAndReplaceAsync<TProjection>(FilterDefinition<TDocument> filter, TDocument replacement,
                                                                         FindOneAndReplaceOptions<TDocument, TProjection> options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(FindOneAndReplaceAsync));

    public virtual Task<TProjection> FindOneAndReplaceAsync<TProjection>(IClientSessionHandle session, FilterDefinition<TDocument> filter, TDocument replacement,
                                                                         FindOneAndReplaceOptions<TDocument, TProjection> options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(FindOneAndReplaceAsync));

    public virtual TProjection FindOneAndUpdate<TProjection>(FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update,
                                                             FindOneAndUpdateOptions<TDocument, TProjection> options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(FindOneAndUpdate));

    public virtual TProjection FindOneAndUpdate<TProjection>(IClientSessionHandle session, FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update,
                                                             FindOneAndUpdateOptions<TDocument, TProjection> options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(FindOneAndUpdate));

    public virtual Task<TProjection> FindOneAndUpdateAsync<TProjection>(FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update,
                                                                        FindOneAndUpdateOptions<TDocument, TProjection> options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(FindOneAndUpdateAsync));

    public virtual Task<TProjection> FindOneAndUpdateAsync<TProjection>(IClientSessionHandle session, FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update,
                                                                        FindOneAndUpdateOptions<TDocument, TProjection> options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(FindOneAndUpdateAsync));

    public virtual IAsyncCursor<TProjection> FindSync<TProjection>(FilterDefinition<TDocument> filter, FindOptions<TDocument, TProjection> options,
                                                                   CancellationToken cancellationToken)
        => throw Unsupported(nameof(FindSync));

    public virtual IAsyncCursor<TProjection> FindSync<TProjection>(IClientSessionHandle session, FilterDefinition<TDocument> filter, FindOptions<TDocument, TProjection> options,
                                                                   CancellationToken cancellationToken)
        => throw Unsupported(nameof(FindSync));

    public virtual void InsertMany(IEnumerable<TDocument> documents, InsertManyOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(InsertMany));

    public virtual void InsertMany(IClientSessionHandle session, IEnumerable<TDocument> documents, InsertManyOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(InsertMany));

    public virtual Task InsertManyAsync(IEnumerable<TDocument> documents, InsertManyOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(InsertManyAsync));

    public virtual Task InsertManyAsync(IClientSessionHandle session, IEnumerable<TDocument> documents, InsertManyOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(InsertManyAsync));

    public virtual void InsertOne(TDocument document, InsertOneOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(InsertOne));

    public virtual void InsertOne(IClientSessionHandle session, TDocument document, InsertOneOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(InsertOne));

    public virtual Task InsertOneAsync(TDocument document, CancellationToken cancellationToken)
        => throw Unsupported(nameof(InsertOneAsync));

    public virtual Task InsertOneAsync(TDocument document, InsertOneOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(InsertOneAsync));

    public virtual Task InsertOneAsync(IClientSessionHandle session, TDocument document, InsertOneOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(InsertOneAsync));

    public virtual IAsyncCursor<TResult> MapReduce<TResult>(BsonJavaScript map, BsonJavaScript reduce, MapReduceOptions<TDocument, TResult> options,
                                                            CancellationToken cancellationToken)
        => throw Unsupported(nameof(MapReduce));

    public virtual IAsyncCursor<TResult> MapReduce<TResult>(IClientSessionHandle session, BsonJavaScript map, BsonJavaScript reduce, MapReduceOptions<TDocument, TResult> options,
                                                            CancellationToken cancellationToken)
        => throw Unsupported(nameof(MapReduce));

    public virtual Task<IAsyncCursor<TResult>> MapReduceAsync<TResult>(BsonJavaScript map, BsonJavaScript reduce, MapReduceOptions<TDocument, TResult> options,
                                                                       CancellationToken cancellationToken)
        => throw Unsupported(nameof(MapReduceAsync));

    public virtual Task<IAsyncCursor<TResult>> MapReduceAsync<TResult>(IClientSessionHandle session, BsonJavaScript map, BsonJavaScript reduce,
                                                                       MapReduceOptions<TDocument, TResult> options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(MapReduceAsync));

    public virtual ReplaceOneResult ReplaceOne(FilterDefinition<TDocument> filter, TDocument replacement, ReplaceOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(ReplaceOne));

    public virtual ReplaceOneResult ReplaceOne(FilterDefinition<TDocument> filter, TDocument replacement, UpdateOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(ReplaceOne));

    public virtual ReplaceOneResult ReplaceOne(IClientSessionHandle session, FilterDefinition<TDocument> filter, TDocument replacement, ReplaceOptions options,
                                               CancellationToken cancellationToken)
        => throw Unsupported(nameof(ReplaceOne));

    public virtual ReplaceOneResult ReplaceOne(IClientSessionHandle session, FilterDefinition<TDocument> filter, TDocument replacement, UpdateOptions options,
                                               CancellationToken cancellationToken)
        => throw Unsupported(nameof(ReplaceOne));

    public virtual Task<ReplaceOneResult> ReplaceOneAsync(FilterDefinition<TDocument> filter, TDocument replacement, ReplaceOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(ReplaceOneAsync));

    public virtual Task<ReplaceOneResult> ReplaceOneAsync(FilterDefinition<TDocument> filter, TDocument replacement, UpdateOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(ReplaceOneAsync));

    public virtual Task<ReplaceOneResult> ReplaceOneAsync(IClientSessionHandle session, FilterDefinition<TDocument> filter, TDocument replacement, ReplaceOptions options,
                                                          CancellationToken cancellationToken)
        => throw Unsupported(nameof(ReplaceOneAsync));

    public virtual Task<ReplaceOneResult> ReplaceOneAsync(IClientSessionHandle session, FilterDefinition<TDocument> filter, TDocument replacement, UpdateOptions options,
                                                          CancellationToken cancellationToken)
        => throw Unsupported(nameof(ReplaceOneAsync));

    public virtual UpdateResult UpdateMany(FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update, UpdateOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(UpdateMany));

    public virtual UpdateResult UpdateMany(IClientSessionHandle session, FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update, UpdateOptions options,
                                           CancellationToken cancellationToken)
        => throw Unsupported(nameof(UpdateMany));

    public virtual Task<UpdateResult> UpdateManyAsync(FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update, UpdateOptions options,
                                                      CancellationToken cancellationToken)
        => throw Unsupported(nameof(UpdateManyAsync));

    public virtual Task<UpdateResult> UpdateManyAsync(IClientSessionHandle session, FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update, UpdateOptions options,
                                                      CancellationToken cancellationToken)
        => throw Unsupported(nameof(UpdateManyAsync));

    public virtual UpdateResult UpdateOne(FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update, UpdateOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(UpdateOne));

    public virtual UpdateResult UpdateOne(IClientSessionHandle session, FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update, UpdateOptions options,
                                          CancellationToken cancellationToken)
        => throw Unsupported(nameof(UpdateOne));

    public virtual Task<UpdateResult> UpdateOneAsync(FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update, UpdateOptions options,
                                                     CancellationToken cancellationToken)
        => throw Unsupported(nameof(UpdateOneAsync));

    public virtual Task<UpdateResult> UpdateOneAsync(IClientSessionHandle session, FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update, UpdateOptions options,
                                                     CancellationToken cancellationToken)
        => throw Unsupported(nameof(UpdateOneAsync));

    public virtual IChangeStreamCursor<TResult> Watch<TResult>(PipelineDefinition<ChangeStreamDocument<TDocument>, TResult> pipeline, ChangeStreamOptions options,
                                                               CancellationToken cancellationToken)
        => throw Unsupported(nameof(Watch));

    public virtual IChangeStreamCursor<TResult> Watch<TResult>(IClientSessionHandle session, PipelineDefinition<ChangeStreamDocument<TDocument>, TResult> pipeline,
                                                               ChangeStreamOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(Watch));

    public virtual Task<IChangeStreamCursor<TResult>> WatchAsync<TResult>(PipelineDefinition<ChangeStreamDocument<TDocument>, TResult> pipeline, ChangeStreamOptions options,
                                                                          CancellationToken cancellationToken)
        => throw Unsupported(nameof(WatchAsync));

    public virtual Task<IChangeStreamCursor<TResult>> WatchAsync<TResult>(IClientSessionHandle session, PipelineDefinition<ChangeStreamDocument<TDocument>, TResult> pipeline,
                                                                          ChangeStreamOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(WatchAsync));

    public virtual IMongoCollection<TDocument> WithReadConcern(ReadConcern readConcern)
        => throw Unsupported(nameof(WithReadConcern));

    public virtual IMongoCollection<TDocument> WithReadPreference(ReadPreference readPreference)
        => throw Unsupported(nameof(WithReadPreference));

    public virtual IMongoCollection<TDocument> WithWriteConcern(WriteConcern writeConcern)
        => throw Unsupported(nameof(WithWriteConcern));

    IFilteredMongoCollection<TDerivedDocument> IMongoCollection<TDocument>.OfType<TDerivedDocument>()
        => throw Unsupported("OfType");
}

/// <summary>
/// A test double base for <see cref="IMongoIndexManager{TDocument}"/> that throws
/// <see cref="NotSupportedException"/> for every member; override only the ones a test exercises. Implemented
/// directly, instead of mocked, because Moq's dynamically generated proxy assembly cannot access an internal
/// <typeparamref name="TDocument"/>, such as <see cref="CheckpointDocument{TAggregate}"/>.
/// </summary>
internal abstract class NotSupportedMongoIndexManager<TDocument> : IMongoIndexManager<TDocument>
{
    public virtual CollectionNamespace CollectionNamespace => throw Unsupported(nameof(CollectionNamespace));

    public virtual IBsonSerializer<TDocument> DocumentSerializer => throw Unsupported(nameof(DocumentSerializer));

    public virtual MongoCollectionSettings Settings => throw Unsupported(nameof(Settings));

    private static NotSupportedException Unsupported(String member)
        => new($"Member '{member}' is not supported by the test index manager.");

    public virtual IEnumerable<String> CreateMany(IEnumerable<CreateIndexModel<TDocument>> models, CancellationToken cancellationToken)
        => throw Unsupported(nameof(CreateMany));

    public virtual IEnumerable<String> CreateMany(IEnumerable<CreateIndexModel<TDocument>> models, CreateManyIndexesOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(CreateMany));

    public virtual IEnumerable<String> CreateMany(IClientSessionHandle session, IEnumerable<CreateIndexModel<TDocument>> models, CancellationToken cancellationToken)
        => throw Unsupported(nameof(CreateMany));

    public virtual IEnumerable<String> CreateMany(IClientSessionHandle session, IEnumerable<CreateIndexModel<TDocument>> models, CreateManyIndexesOptions options,
                                                  CancellationToken cancellationToken)
        => throw Unsupported(nameof(CreateMany));

    public virtual Task<IEnumerable<String>> CreateManyAsync(IEnumerable<CreateIndexModel<TDocument>> models, CancellationToken cancellationToken)
        => throw Unsupported(nameof(CreateManyAsync));

    public virtual Task<IEnumerable<String>> CreateManyAsync(IEnumerable<CreateIndexModel<TDocument>> models, CreateManyIndexesOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(CreateManyAsync));

    public virtual Task<IEnumerable<String>> CreateManyAsync(IClientSessionHandle session, IEnumerable<CreateIndexModel<TDocument>> models, CancellationToken cancellationToken)
        => throw Unsupported(nameof(CreateManyAsync));

    public virtual Task<IEnumerable<String>> CreateManyAsync(IClientSessionHandle session, IEnumerable<CreateIndexModel<TDocument>> models, CreateManyIndexesOptions options,
                                                             CancellationToken cancellationToken)
        => throw Unsupported(nameof(CreateManyAsync));

    public virtual String CreateOne(CreateIndexModel<TDocument> model, CreateOneIndexOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(CreateOne));

    public virtual String CreateOne(IndexKeysDefinition<TDocument> keys, CreateIndexOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(CreateOne));

    public virtual String CreateOne(IClientSessionHandle session, IndexKeysDefinition<TDocument> keys, CreateIndexOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(CreateOne));

    public virtual String CreateOne(IClientSessionHandle session, CreateIndexModel<TDocument> model, CreateOneIndexOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(CreateOne));

    public virtual Task<String> CreateOneAsync(CreateIndexModel<TDocument> model, CreateOneIndexOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(CreateOneAsync));

    public virtual Task<String> CreateOneAsync(IndexKeysDefinition<TDocument> keys, CreateIndexOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(CreateOneAsync));

    public virtual Task<String> CreateOneAsync(IClientSessionHandle session, IndexKeysDefinition<TDocument> keys, CreateIndexOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(CreateOneAsync));

    public virtual Task<String> CreateOneAsync(IClientSessionHandle session, CreateIndexModel<TDocument> model, CreateOneIndexOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(CreateOneAsync));

    public virtual void DropAll(DropIndexOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(DropAll));

    public virtual void DropAll(CancellationToken cancellationToken)
        => throw Unsupported(nameof(DropAll));

    public virtual void DropAll(IClientSessionHandle session, CancellationToken cancellationToken)
        => throw Unsupported(nameof(DropAll));

    public virtual void DropAll(IClientSessionHandle session, DropIndexOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(DropAll));

    public virtual Task DropAllAsync(DropIndexOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(DropAllAsync));

    public virtual Task DropAllAsync(CancellationToken cancellationToken)
        => throw Unsupported(nameof(DropAllAsync));

    public virtual Task DropAllAsync(IClientSessionHandle session, CancellationToken cancellationToken)
        => throw Unsupported(nameof(DropAllAsync));

    public virtual Task DropAllAsync(IClientSessionHandle session, DropIndexOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(DropAllAsync));

    public virtual void DropOne(String name, CancellationToken cancellationToken)
        => throw Unsupported(nameof(DropOne));

    public virtual void DropOne(String name, DropIndexOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(DropOne));

    public virtual void DropOne(IClientSessionHandle session, String name, CancellationToken cancellationToken)
        => throw Unsupported(nameof(DropOne));

    public virtual void DropOne(IClientSessionHandle session, String name, DropIndexOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(DropOne));

    public virtual Task DropOneAsync(String name, CancellationToken cancellationToken)
        => throw Unsupported(nameof(DropOneAsync));

    public virtual Task DropOneAsync(String name, DropIndexOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(DropOneAsync));

    public virtual Task DropOneAsync(IClientSessionHandle session, String name, CancellationToken cancellationToken)
        => throw Unsupported(nameof(DropOneAsync));

    public virtual Task DropOneAsync(IClientSessionHandle session, String name, DropIndexOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(DropOneAsync));

    public virtual IAsyncCursor<BsonDocument> List(CancellationToken cancellationToken)
        => throw Unsupported(nameof(List));

    public virtual IAsyncCursor<BsonDocument> List(ListIndexesOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(List));

    public virtual IAsyncCursor<BsonDocument> List(IClientSessionHandle session, CancellationToken cancellationToken)
        => throw Unsupported(nameof(List));

    public virtual IAsyncCursor<BsonDocument> List(IClientSessionHandle session, ListIndexesOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(List));

    public virtual Task<IAsyncCursor<BsonDocument>> ListAsync(CancellationToken cancellationToken)
        => throw Unsupported(nameof(ListAsync));

    public virtual Task<IAsyncCursor<BsonDocument>> ListAsync(ListIndexesOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(ListAsync));

    public virtual Task<IAsyncCursor<BsonDocument>> ListAsync(IClientSessionHandle session, CancellationToken cancellationToken)
        => throw Unsupported(nameof(ListAsync));

    public virtual Task<IAsyncCursor<BsonDocument>> ListAsync(IClientSessionHandle session, ListIndexesOptions options, CancellationToken cancellationToken)
        => throw Unsupported(nameof(ListAsync));
}

/// <summary>
/// A placeholder <see cref="IMongoIndexManager{TDocument}"/> returned from
/// <see cref="IMongoCollection{TDocument}.Indexes"/>. <see cref="CreateOneAsync"/> simulates an index that already
/// exists, which is all the append path's concurrency index check needs; every other member is unsupported.
/// </summary>
internal sealed class PlaceholderMongoIndexManager<TDocument> : NotSupportedMongoIndexManager<TDocument>
{
    public override Task<String> CreateOneAsync(CreateIndexModel<TDocument> model, CreateOneIndexOptions options, CancellationToken cancellationToken)
        => Task.FromResult(model.Options.Name ?? String.Empty);
}
