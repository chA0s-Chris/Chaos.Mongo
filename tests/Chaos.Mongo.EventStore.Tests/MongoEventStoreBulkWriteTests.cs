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

public class MongoEventStoreBulkWriteTests
{
    [Test]
    public async Task AppendEventsAsync_WhenAggregateTypesShareClient_CachesVersionLookupAcrossTypes()
    {
        var firstOptions = new MongoEventStoreOptions<TestAggregate>
        {
            CollectionPrefix = "Orders",
            BulkWriteOptimizationEnabled = true
        };
        var secondOptions = new MongoEventStoreOptions<SecondaryTestAggregate>
        {
            CollectionPrefix = "Invoices",
            BulkWriteOptimizationEnabled = true
        };
        BootstrapSerialization(firstOptions);
        BootstrapSerialization(secondOptions);

        var firstReadModelCollection = MongoCollectionProxy<TestAggregate>.Create(
            firstOptions.ReadModelCollectionName,
            CreateCursor<TestAggregate>());
        var firstEventsCollection = MongoCollectionProxy<Event<TestAggregate>>.Create(firstOptions.EventsCollectionName);
        var secondReadModelCollection = MongoCollectionProxy<SecondaryTestAggregate>.Create(
            secondOptions.ReadModelCollectionName,
            CreateCursor<SecondaryTestAggregate>());
        var secondEventsCollection = MongoCollectionProxy<Event<SecondaryTestAggregate>>.Create(secondOptions.EventsCollectionName);

        var databaseMock = new Mock<IMongoDatabase>(MockBehavior.Strict);
        databaseMock.Setup(d => d.GetCollection<TestAggregate>(firstOptions.ReadModelCollectionName, null))
                    .Returns(firstReadModelCollection);
        databaseMock.Setup(d => d.GetCollection<Event<TestAggregate>>(firstOptions.EventsCollectionName, null))
                    .Returns(firstEventsCollection);
        databaseMock.Setup(d => d.GetCollection<SecondaryTestAggregate>(secondOptions.ReadModelCollectionName, null))
                    .Returns(secondReadModelCollection);
        databaseMock.Setup(d => d.GetCollection<Event<SecondaryTestAggregate>>(secondOptions.EventsCollectionName, null))
                    .Returns(secondEventsCollection);
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

        var sessionMock = new Mock<IClientSessionHandle>(MockBehavior.Strict);
        sessionMock.Setup(s => s.WithTransactionAsync(
                              It.IsAny<Func<IClientSessionHandle, CancellationToken, Task<Object?>>>(),
                              It.IsAny<TransactionOptions>(),
                              It.IsAny<CancellationToken>()))
                   .Returns<Func<IClientSessionHandle, CancellationToken, Task<Object?>>, TransactionOptions?, CancellationToken>((callback, _, ct) =>
                           callback(sessionMock.Object, ct));
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

        var firstStore = new MongoEventStore<TestAggregate>(mongoHelperMock.Object, firstOptions);
        var secondStore = new MongoEventStore<SecondaryTestAggregate>(mongoHelperMock.Object, secondOptions);

        _ = await firstStore.AppendEventsAsync(
        [
            new TestCreatedEvent
            {
                Id = Guid.NewGuid(),
                AggregateId = Guid.NewGuid(),
                Version = 1
            }
        ]);

        _ = await secondStore.AppendEventsAsync(
        [
            new SecondaryTestCreatedEvent
            {
                Id = Guid.NewGuid(),
                AggregateId = Guid.NewGuid(),
                Version = 1
            }
        ]);

        databaseMock.Verify(d => d.RunCommandAsync(
                                It.IsAny<Command<BsonDocument>>(),
                                It.IsAny<ReadPreference>(),
                                It.IsAny<CancellationToken>()),
                            Times.Once);
        clientMock.Verify(c => c.StartSessionAsync(It.IsAny<ClientSessionOptions>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
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
        var eventsCollection = MongoCollectionProxy<Event<TestAggregate>>.Create(options.EventsCollectionName);

        var databaseMock = new Mock<IMongoDatabase>(MockBehavior.Strict);
        databaseMock.Setup(d => d.GetCollection<TestAggregate>(options.ReadModelCollectionName, null))
                    .Returns(readModelCollection);
        databaseMock.Setup(d => d.GetCollection<Event<TestAggregate>>(options.EventsCollectionName, null))
                    .Returns(eventsCollection);
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
                              It.IsAny<Func<IClientSessionHandle, CancellationToken, Task<Object?>>>(),
                              It.IsAny<TransactionOptions>(),
                              It.IsAny<CancellationToken>()))
                   .Returns<Func<IClientSessionHandle, CancellationToken, Task<Object?>>, TransactionOptions?, CancellationToken>((callback, _, ct) =>
                           callback(sessionMock.Object, ct));
        sessionMock.Setup(s => s.Dispose());

        var clientMock = new Mock<IMongoClient>(MockBehavior.Strict);
        clientMock.Setup(c => c.StartSessionAsync(It.IsAny<ClientSessionOptions>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(sessionMock.Object);
        clientMock.Setup(c => c.BulkWriteAsync(
                             sessionMock.Object,
                             It.IsAny<IReadOnlyList<BulkWriteModel>>(),
                             It.IsAny<ClientBulkWriteOptions>(),
                             It.IsAny<CancellationToken>()))
                  .Callback<IClientSessionHandle, IReadOnlyList<BulkWriteModel>, ClientBulkWriteOptions, CancellationToken>((_, models, bulkWriteOptions, _) =>
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
    public async Task AppendEventsAsync_WhenCapabilityWaitIsCanceled_DoesNotPoisonSharedLookup()
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
        var versionLookup = new TaskCompletionSource<BsonDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        var versionLookupStarted = new TaskCompletionSource<Boolean>(TaskCreationOptions.RunContinuationsAsynchronously);

        var databaseMock = new Mock<IMongoDatabase>(MockBehavior.Strict);
        databaseMock.Setup(d => d.GetCollection<TestAggregate>(options.ReadModelCollectionName, null))
                    .Returns(readModelCollection);
        databaseMock.Setup(d => d.GetCollection<Event<TestAggregate>>(options.EventsCollectionName, null))
                    .Returns(eventsCollection);
        databaseMock.Setup(d => d.RunCommandAsync(
                               It.IsAny<Command<BsonDocument>>(),
                               It.IsAny<ReadPreference>(),
                               It.IsAny<CancellationToken>()))
                    .Callback(() => versionLookupStarted.TrySetResult(true))
                    .Returns(versionLookup.Task);

        var sessionMock = new Mock<IClientSessionHandle>(MockBehavior.Strict);
        sessionMock.Setup(s => s.WithTransactionAsync(
                              It.IsAny<Func<IClientSessionHandle, CancellationToken, Task<Object?>>>(),
                              It.IsAny<TransactionOptions>(),
                              It.IsAny<CancellationToken>()))
                   .Returns<Func<IClientSessionHandle, CancellationToken, Task<Object?>>, TransactionOptions?, CancellationToken>((callback, _, ct) =>
                           callback(sessionMock.Object, ct));
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
        using var cancellation = new CancellationTokenSource();
        var canceledAppend = sut.AppendEventsAsync(
            [
                new TestCreatedEvent
                {
                    Id = Guid.NewGuid(),
                    AggregateId = Guid.NewGuid(),
                    Version = 1
                }
            ],
            cancellationToken: cancellation.Token);

        await versionLookupStarted.Task;
        await cancellation.CancelAsync();

        var act = async () => await canceledAppend.WaitAsync(TimeSpan.FromSeconds(1));
        await act.Should().ThrowAsync<OperationCanceledException>();

        versionLookup.SetResult(new BsonDocument("versionArray", new BsonArray
        {
            8,
            0,
            0,
            0
        }));

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
                            Times.Once);
        clientMock.Verify(c => c.StartSessionAsync(It.IsAny<ClientSessionOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task AppendEventsAsync_WhenStoresShareClient_CachesVersionLookupAcrossInstances()
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

        var sessionMock = new Mock<IClientSessionHandle>(MockBehavior.Strict);
        sessionMock.Setup(s => s.WithTransactionAsync(
                              It.IsAny<Func<IClientSessionHandle, CancellationToken, Task<Object?>>>(),
                              It.IsAny<TransactionOptions>(),
                              It.IsAny<CancellationToken>()))
                   .Returns<Func<IClientSessionHandle, CancellationToken, Task<Object?>>, TransactionOptions?, CancellationToken>((callback, _, ct) =>
                           callback(sessionMock.Object, ct));
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

        var firstStore = new MongoEventStore<TestAggregate>(mongoHelperMock.Object, options);
        var secondStore = new MongoEventStore<TestAggregate>(mongoHelperMock.Object, options);

        _ = await firstStore.AppendEventsAsync(
        [
            new TestCreatedEvent
            {
                Id = Guid.NewGuid(),
                AggregateId = Guid.NewGuid(),
                Version = 1
            }
        ]);

        _ = await secondStore.AppendEventsAsync(
        [
            new TestCreatedEvent
            {
                Id = Guid.NewGuid(),
                AggregateId = Guid.NewGuid(),
                Version = 1
            }
        ]);

        databaseMock.Verify(d => d.RunCommandAsync(
                                It.IsAny<Command<BsonDocument>>(),
                                It.IsAny<ReadPreference>(),
                                It.IsAny<CancellationToken>()),
                            Times.Once);
        clientMock.Verify(c => c.StartSessionAsync(It.IsAny<ClientSessionOptions>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
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
                              It.IsAny<Func<IClientSessionHandle, CancellationToken, Task<Object?>>>(),
                              It.IsAny<TransactionOptions>(),
                              It.IsAny<CancellationToken>()))
                   .Returns<Func<IClientSessionHandle, CancellationToken, Task<Object?>>, TransactionOptions?, CancellationToken>((callback, _, ct) =>
                           callback(sessionMock.Object, ct));
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

    private static void BootstrapSerialization(MongoEventStoreOptions<TestAggregate> options)
    {
        options.AddEventType(typeof(TestCreatedEvent), nameof(TestCreatedEvent));
        options.AddEventType(typeof(TestUpdatedEvent), nameof(TestUpdatedEvent));
        options.AddEventType(typeof(TestCompletedEvent), nameof(TestCompletedEvent));
        MongoEventStoreSerializationSetup.EnsureGuidSerializer();
        MongoEventStoreSerializationSetup.RegisterClassMaps(options);
    }

    private static void BootstrapSerialization(MongoEventStoreOptions<SecondaryTestAggregate> options)
    {
        options.AddEventType(typeof(SecondaryTestCreatedEvent), nameof(SecondaryTestCreatedEvent));
        MongoEventStoreSerializationSetup.EnsureGuidSerializer();
        MongoEventStoreSerializationSetup.RegisterClassMaps(options);
    }

    /// <summary>
    /// Creates a cursor without Moq, because Moq's dynamically generated proxy assembly cannot access an internal
    /// <typeparamref name="TDocument"/>, such as <see cref="CheckpointDocument{TAggregate}"/>.
    /// </summary>
    private static IAsyncCursor<TDocument> CreateCursor<TDocument>(params TDocument[] documents)
        => new SingleBatchAsyncCursor<TDocument>(documents);

    public sealed class SecondaryTestAggregate : IAggregate
    {
        public String Status { get; set; } = String.Empty;
        public DateTime CreatedUtc { get; set; }
        public Guid Id { get; set; }
        public Int64 Revision { get; set; }
        public Int64 Version { get; set; }
    }

    public sealed class SecondaryTestCreatedEvent : Event<SecondaryTestAggregate>
    {
        public override void Execute(SecondaryTestAggregate aggregate) => aggregate.Status = "Created";
    }

    public sealed class TestAggregate : IAggregate
    {
        public String Status { get; set; } = String.Empty;
        public DateTime CreatedUtc { get; set; }
        public Guid Id { get; set; }
        public Int64 Revision { get; set; }
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

    /// <summary>
    /// Implemented directly on <see cref="NotSupportedMongoCollection{TDocument}"/>, instead of through
    /// <see cref="System.Reflection.DispatchProxy"/>, because <see cref="System.Reflection.DispatchProxy"/>'s
    /// dynamically generated proxy assembly cannot access an internal <typeparamref name="TDocument"/>, such as
    /// <see cref="CheckpointDocument{TAggregate}"/>.
    /// </summary>
    private sealed class MongoCollectionProxy<TDocument> : NotSupportedMongoCollection<TDocument>
    {
        private readonly IAsyncCursor<TDocument> _cursor;
        private readonly IMongoIndexManager<TDocument> _indexManager = new PlaceholderMongoIndexManager<TDocument>();
        private readonly MongoCollectionSettings _settings = new();

        private MongoCollectionProxy(String collectionName, IAsyncCursor<TDocument> cursor)
        {
            _cursor = cursor;
            CollectionNamespace = new CollectionNamespace(new DatabaseNamespace("Tests"), collectionName);
        }

        public override CollectionNamespace CollectionNamespace { get; }

        public override IMongoDatabase Database { get; } = Mock.Of<IMongoDatabase>();

        public override IBsonSerializer<TDocument> DocumentSerializer => BsonSerializer.SerializerRegistry.GetSerializer<TDocument>();

        public override IMongoIndexManager<TDocument> Indexes => _indexManager;

        public override IMongoSearchIndexManager SearchIndexes { get; } = Mock.Of<IMongoSearchIndexManager>();

        public override MongoCollectionSettings Settings => _settings;

        public static IMongoCollection<TDocument> Create(String collectionName, IAsyncCursor<TDocument>? cursor = null)
            => new MongoCollectionProxy<TDocument>(collectionName, cursor ?? CreateCursor<TDocument>());

        public override Task<IAsyncCursor<TProjection>> FindAsync<TProjection>(
            FilterDefinition<TDocument> filter, FindOptions<TDocument, TProjection> options, CancellationToken cancellationToken)
            => Task.FromResult(HandleFind<TProjection>());

        public override Task<IAsyncCursor<TProjection>> FindAsync<TProjection>(
            IClientSessionHandle session, FilterDefinition<TDocument> filter, FindOptions<TDocument, TProjection> options,
            CancellationToken cancellationToken)
            => Task.FromResult(HandleFind<TProjection>());

        public override IAsyncCursor<TProjection> FindSync<TProjection>(
            FilterDefinition<TDocument> filter, FindOptions<TDocument, TProjection> options, CancellationToken cancellationToken)
            => HandleFind<TProjection>();

        public override IAsyncCursor<TProjection> FindSync<TProjection>(
            IClientSessionHandle session, FilterDefinition<TDocument> filter, FindOptions<TDocument, TProjection> options,
            CancellationToken cancellationToken)
            => HandleFind<TProjection>();

        public override IMongoCollection<TDocument> WithReadConcern(ReadConcern readConcern) => this;

        public override IMongoCollection<TDocument> WithReadPreference(ReadPreference readPreference) => this;

        public override IMongoCollection<TDocument> WithWriteConcern(WriteConcern writeConcern) => this;

        private static NotSupportedException UnsupportedProjection(Type projectionType)
            => new($"Projection '{projectionType}' is not supported by the test collection.");

        /// <summary>
        /// Returns the configured cursor for typed finds and an empty cursor for finds that project
        /// elements into a <see cref="BsonDocument"/>, such as the stream-head read.
        /// </summary>
        private IAsyncCursor<TProjection> HandleFind<TProjection>()
        {
            if (typeof(TProjection) == typeof(TDocument))
            {
                return (IAsyncCursor<TProjection>)_cursor;
            }

            return typeof(TProjection) == typeof(BsonDocument)
                ? (IAsyncCursor<TProjection>)CreateCursor<BsonDocument>()
                : throw UnsupportedProjection(typeof(TProjection));
        }
    }

    /// <summary>
    /// A minimal <see cref="IAsyncCursor{TDocument}"/> that returns its documents in a single batch.
    /// </summary>
    private sealed class SingleBatchAsyncCursor<TDocument> : IAsyncCursor<TDocument>
    {
        private readonly TDocument[] _documents;
        private Boolean _returned;

        public SingleBatchAsyncCursor(TDocument[] documents) => _documents = documents;

        public IEnumerable<TDocument> Current => _documents;

        public Boolean MoveNext(CancellationToken cancellationToken = default)
        {
            if (_returned)
            {
                return false;
            }

            _returned = true;
            return _documents.Length > 0;
        }

        public Task<Boolean> MoveNextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(MoveNext(cancellationToken));

        public void Dispose() { }
    }
}
