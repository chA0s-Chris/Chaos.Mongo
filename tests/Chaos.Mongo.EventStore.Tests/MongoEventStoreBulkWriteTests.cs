// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests;

using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Search;
using Moq;
using NUnit.Framework;
using System.Reflection;

public class MongoEventStoreBulkWriteTests
{
    [Test]
    public async Task AppendEventsAsync_WhenBulkWriteOptimizationEnabled_UsesOrderedClientBulkWrite()
    {
        var options = new MongoEventStoreOptions<TestAggregate>
        {
            CollectionPrefix = "Orders",
            CheckpointInterval = 3,
            BulkWriteOptimizationEnabled = true
        };
        BootstrapSerialization(options);
        var readModelCollection = MongoCollectionProxy<TestAggregate>.Create(
            options.ReadModelCollectionName,
            CreateCursor<TestAggregate>());
        var eventsCollection = MongoCollectionProxy<Event<TestAggregate>>.Create(options.EventsCollectionName);
        var checkpointCollection = MongoCollectionProxy<CheckpointDocument<TestAggregate>>.Create(options.CheckpointCollectionName);

        var databaseMock = new Mock<IMongoDatabase>(MockBehavior.Strict);
        databaseMock.Setup(d => d.GetCollection<TestAggregate>(options.ReadModelCollectionName, null))
                    .Returns(readModelCollection);
        databaseMock.Setup(d => d.GetCollection<Event<TestAggregate>>(options.EventsCollectionName, null))
                    .Returns(eventsCollection);
        databaseMock.Setup(d => d.GetCollection<CheckpointDocument<TestAggregate>>(options.CheckpointCollectionName, null))
                    .Returns(checkpointCollection);
        databaseMock.Setup(d => d.RunCommandAsync(
                           It.IsAny<Command<BsonDocument>>(),
                           It.IsAny<ReadPreference>(),
                           It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new BsonDocument("versionArray", new BsonArray
                    {
                        8,
                        0,
                        0,
                        0
                    }));

        IReadOnlyList<BulkWriteModel>? capturedModels = null;
        ClientBulkWriteOptions? capturedOptions = null;
        var sessionMock = new Mock<IClientSessionHandle>(MockBehavior.Strict);
        sessionMock.Setup(s => s.WithTransactionAsync(
                              It.IsAny<Func<IClientSessionHandle, CancellationToken, Task<object?>>>(),
                              It.IsAny<TransactionOptions>(),
                              It.IsAny<CancellationToken>()))
                   .Returns<Func<IClientSessionHandle, CancellationToken, Task<object?>>, TransactionOptions?, CancellationToken>(
                       (callback, _, ct) => callback(sessionMock.Object, ct));
        sessionMock.Setup(s => s.Dispose());

        var clientMock = new Mock<IMongoClient>(MockBehavior.Strict);
        clientMock.Setup(c => c.StartSessionAsync(It.IsAny<ClientSessionOptions>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(sessionMock.Object);
        clientMock.Setup(c => c.BulkWriteAsync(
                             sessionMock.Object,
                             It.IsAny<IReadOnlyList<BulkWriteModel>>(),
                             It.IsAny<ClientBulkWriteOptions>(),
                             It.IsAny<CancellationToken>()))
                  .Callback<IClientSessionHandle, IReadOnlyList<BulkWriteModel>, ClientBulkWriteOptions, CancellationToken>(
                      (_, models, bulkWriteOptions, _) =>
                      {
                          capturedModels = models;
                          capturedOptions = bulkWriteOptions;
                      })
                  .ReturnsAsync((ClientBulkWriteResult)null!);

        var mongoHelperMock = new Mock<IMongoHelper>(MockBehavior.Strict);
        mongoHelperMock.Setup(h => h.Client).Returns(clientMock.Object);
        mongoHelperMock.Setup(h => h.Database).Returns(databaseMock.Object);

        var sut = new MongoEventStore<TestAggregate>(mongoHelperMock.Object, options);
        var aggregateId = Guid.NewGuid();

        var aggregate = await sut.AppendEventsAsync(
        [
            new TestCreatedEvent
            {
                Id = Guid.NewGuid(),
                AggregateId = aggregateId,
                Version = 1
            },
            new TestUpdatedEvent
            {
                Id = Guid.NewGuid(),
                AggregateId = aggregateId,
                Version = 2
            },
            new TestCompletedEvent
            {
                Id = Guid.NewGuid(),
                AggregateId = aggregateId,
                Version = 3
            }
        ]);

        aggregate.Version.Should().Be(3);
        capturedOptions.Should().NotBeNull();
        capturedOptions!.IsOrdered.Should().BeTrue();
        capturedModels.Should().NotBeNull();
        capturedModels!.Should().HaveCount(5);
        capturedModels.Select(m => m.GetType()).Should().Equal(
            typeof(BulkWriteInsertOneModel<BsonDocument>),
            typeof(BulkWriteInsertOneModel<BsonDocument>),
            typeof(BulkWriteInsertOneModel<BsonDocument>),
            typeof(BulkWriteReplaceOneModel<BsonDocument>),
            typeof(BulkWriteInsertOneModel<BsonDocument>));
        capturedModels.Select(m => m.Namespace.CollectionName).Should().Equal(
            options.EventsCollectionName,
            options.EventsCollectionName,
            options.EventsCollectionName,
            options.ReadModelCollectionName,
            options.CheckpointCollectionName);

        clientMock.Verify(c => c.BulkWriteAsync(
                              sessionMock.Object,
                              It.IsAny<IReadOnlyList<BulkWriteModel>>(),
                              It.IsAny<ClientBulkWriteOptions>(),
                              It.IsAny<CancellationToken>()),
                          Times.Once);
    }

    [Test]
    public async Task AppendEventsAsync_WhenBulkWriteOptimizationEnabledOnUnsupportedServer_ThrowsNotSupportedException()
    {
        var options = new MongoEventStoreOptions<TestAggregate>
        {
            CollectionPrefix = "Orders",
            BulkWriteOptimizationEnabled = true
        };
        BootstrapSerialization(options);
        var readModelCollection = MongoCollectionProxy<TestAggregate>.Create(
            options.ReadModelCollectionName,
            CreateCursor<TestAggregate>());

        var databaseMock = new Mock<IMongoDatabase>(MockBehavior.Strict);
        databaseMock.Setup(d => d.GetCollection<TestAggregate>(options.ReadModelCollectionName, null))
                    .Returns(readModelCollection);
        databaseMock.Setup(d => d.RunCommandAsync(
                           It.IsAny<Command<BsonDocument>>(),
                           It.IsAny<ReadPreference>(),
                           It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new BsonDocument("versionArray", new BsonArray
                    {
                        7,
                        0,
                        0,
                        0
                    }));

        var clientMock = new Mock<IMongoClient>(MockBehavior.Strict);
        var mongoHelperMock = new Mock<IMongoHelper>(MockBehavior.Strict);
        mongoHelperMock.Setup(h => h.Client).Returns(clientMock.Object);
        mongoHelperMock.Setup(h => h.Database).Returns(databaseMock.Object);

        var sut = new MongoEventStore<TestAggregate>(mongoHelperMock.Object, options);

        var act = () => sut.AppendEventsAsync(
        [
            new TestCreatedEvent
            {
                Id = Guid.NewGuid(),
                AggregateId = Guid.NewGuid(),
                Version = 1
            }
        ]);

        await act.Should().ThrowAsync<NotSupportedException>()
                 .WithMessage("*MongoDB 8.0 or later*7.0*");

        clientMock.Verify(c => c.StartSessionAsync(It.IsAny<ClientSessionOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task AppendEventsAsync_WhenVersionDetectionFails_RetriesVersionLookupOnNextAttempt()
    {
        var options = new MongoEventStoreOptions<TestAggregate>
        {
            CollectionPrefix = "Orders",
            BulkWriteOptimizationEnabled = true
        };
        BootstrapSerialization(options);
        var readModelCollection = MongoCollectionProxy<TestAggregate>.Create(
            options.ReadModelCollectionName,
            CreateCursor<TestAggregate>());
        var eventsCollection = MongoCollectionProxy<Event<TestAggregate>>.Create(options.EventsCollectionName);

        var databaseMock = new Mock<IMongoDatabase>(MockBehavior.Strict);
        databaseMock.Setup(d => d.GetCollection<TestAggregate>(options.ReadModelCollectionName, null))
                    .Returns(readModelCollection);
        databaseMock.Setup(d => d.GetCollection<Event<TestAggregate>>(options.EventsCollectionName, null))
                    .Returns(eventsCollection);
        databaseMock.SetupSequence(d => d.RunCommandAsync(
                                   It.IsAny<Command<BsonDocument>>(),
                                   It.IsAny<ReadPreference>(),
                                   It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new InvalidOperationException("buildInfo failed"))
                    .ReturnsAsync(new BsonDocument("versionArray", new BsonArray
                    {
                        8,
                        0,
                        0,
                        0
                    }));

        var sessionMock = new Mock<IClientSessionHandle>(MockBehavior.Strict);
        sessionMock.Setup(s => s.WithTransactionAsync(
                              It.IsAny<Func<IClientSessionHandle, CancellationToken, Task<object?>>>(),
                              It.IsAny<TransactionOptions>(),
                              It.IsAny<CancellationToken>()))
                   .Returns<Func<IClientSessionHandle, CancellationToken, Task<object?>>, TransactionOptions?, CancellationToken>(
                       (callback, _, ct) => callback(sessionMock.Object, ct));
        sessionMock.Setup(s => s.Dispose());

        var clientMock = new Mock<IMongoClient>(MockBehavior.Strict);
        clientMock.Setup(c => c.StartSessionAsync(It.IsAny<ClientSessionOptions>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(sessionMock.Object);
        clientMock.Setup(c => c.BulkWriteAsync(
                             sessionMock.Object,
                             It.IsAny<IReadOnlyList<BulkWriteModel>>(),
                             It.IsAny<ClientBulkWriteOptions>(),
                             It.IsAny<CancellationToken>()))
                  .ReturnsAsync((ClientBulkWriteResult)null!);

        var mongoHelperMock = new Mock<IMongoHelper>(MockBehavior.Strict);
        mongoHelperMock.Setup(h => h.Client).Returns(clientMock.Object);
        mongoHelperMock.Setup(h => h.Database).Returns(databaseMock.Object);

        var sut = new MongoEventStore<TestAggregate>(mongoHelperMock.Object, options);
        var aggregateId = Guid.NewGuid();

        var failedAttempt = () => sut.AppendEventsAsync(
        [
            new TestCreatedEvent
            {
                Id = Guid.NewGuid(),
                AggregateId = aggregateId,
                Version = 1
            }
        ]);

        await failedAttempt.Should().ThrowAsync<InvalidOperationException>()
                           .WithMessage("buildInfo failed");

        var aggregate = await sut.AppendEventsAsync(
        [
            new TestCreatedEvent
            {
                Id = Guid.NewGuid(),
                AggregateId = Guid.NewGuid(),
                Version = 1
            }
        ]);

        aggregate.Version.Should().Be(1);
        databaseMock.Verify(d => d.RunCommandAsync(
                                It.IsAny<Command<BsonDocument>>(),
                                It.IsAny<ReadPreference>(),
                                It.IsAny<CancellationToken>()),
                            Times.Exactly(2));
        clientMock.Verify(c => c.StartSessionAsync(It.IsAny<ClientSessionOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static IAsyncCursor<TDocument> CreateCursor<TDocument>(params TDocument[] documents)
    {
        var returned = false;
        var cursorMock = new Mock<IAsyncCursor<TDocument>>();
        cursorMock.Setup(c => c.MoveNext(It.IsAny<CancellationToken>()))
                  .Returns(() =>
                  {
                      if (returned)
                      {
                          return false;
                      }

                      returned = true;
                      return documents.Length > 0;
                  });
        cursorMock.Setup(c => c.MoveNextAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(() =>
                  {
                      if (returned)
                      {
                          return false;
                      }

                      returned = true;
                      return documents.Length > 0;
                  });
        cursorMock.Setup(c => c.Current).Returns(documents);
        return cursorMock.Object;
    }

    private static void BootstrapSerialization(MongoEventStoreOptions<TestAggregate> options)
    {
        options.EventTypes[typeof(TestCreatedEvent)] = nameof(TestCreatedEvent);
        options.EventTypes[typeof(TestUpdatedEvent)] = nameof(TestUpdatedEvent);
        options.EventTypes[typeof(TestCompletedEvent)] = nameof(TestCompletedEvent);
        MongoEventStoreSerializationSetup.EnsureGuidSerializer();
        MongoEventStoreSerializationSetup.RegisterClassMaps(options);
    }

    private class MongoCollectionProxy<TDocument> : DispatchProxy
    {
        private static readonly IMongoDatabase Database = Mock.Of<IMongoDatabase>();
        private static readonly IMongoIndexManager<TDocument> IndexManager = Mock.Of<IMongoIndexManager<TDocument>>();
        private static readonly MongoCollectionSettings Settings = new();

        private IMongoCollection<TDocument> _collection = null!;
        private IAsyncCursor<TDocument> _cursor = null!;
        private CollectionNamespace _namespace = null!;

        public static IMongoCollection<TDocument> Create(String collectionName, IAsyncCursor<TDocument>? cursor = null)
        {
            var collection = Create<IMongoCollection<TDocument>, MongoCollectionProxy<TDocument>>();
            var proxy = (MongoCollectionProxy<TDocument>)(Object)collection;
            proxy._collection = collection;
            proxy._cursor = cursor ?? CreateCursor<TDocument>();
            proxy._namespace = new(new DatabaseNamespace("Tests"), collectionName);
            return collection;
        }

        protected override Object? Invoke(MethodInfo? targetMethod, Object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);

            return targetMethod.Name switch
            {
                "FindAsync" when targetMethod.IsGenericMethod => HandleFindAsync(targetMethod.GetGenericArguments()[0], args),
                "FindSync" when targetMethod.IsGenericMethod => HandleFindSync(targetMethod.GetGenericArguments()[0], args),
                "get_CollectionNamespace" => _namespace,
                "get_Database" => Database,
                "get_DocumentSerializer" => BsonSerializer.SerializerRegistry.GetSerializer<TDocument>(),
                "get_Indexes" => IndexManager,
                "get_SearchIndexes" => Mock.Of<IMongoSearchIndexManager>(),
                "get_Settings" => Settings,
                "WithReadConcern" or "WithReadPreference" or "WithWriteConcern" => _collection,
                _ => throw new NotSupportedException($"Method '{targetMethod.Name}' is not supported by the test collection.")
            };
        }

        private Object HandleFindAsync(Type projectionType, Object?[]? args)
        {
            EnsureProjection(projectionType, args);
            return Task.FromResult(_cursor);
        }

        private Object HandleFindSync(Type projectionType, Object?[]? args)
        {
            EnsureProjection(projectionType, args);
            return _cursor;
        }

        private static void EnsureProjection(Type projectionType, Object?[]? args)
        {
            if (projectionType != typeof(TDocument))
            {
                throw new NotSupportedException($"Projection '{projectionType}' is not supported by the test collection.");
            }

            if (args?.Length is not 3 and not 4)
            {
                throw new NotSupportedException("Unexpected Find invocation shape.");
            }
        }
    }

    public sealed class TestAggregate : IAggregate
    {
        public DateTime CreatedUtc { get; set; }
        public Guid Id { get; set; }
        public String Status { get; set; } = String.Empty;
        public Int64 Version { get; set; }
    }

    public sealed class TestCompletedEvent : Event<TestAggregate>
    {
        public override void Execute(TestAggregate aggregate) => aggregate.Status = "Completed";
    }

    public sealed class TestCreatedEvent : Event<TestAggregate>
    {
        public override void Execute(TestAggregate aggregate) => aggregate.Status = "Created";
    }

    public sealed class TestUpdatedEvent : Event<TestAggregate>
    {
        public override void Execute(TestAggregate aggregate) => aggregate.Status = "Updated";
    }
}
