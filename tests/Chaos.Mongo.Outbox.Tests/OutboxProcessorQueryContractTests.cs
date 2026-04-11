// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox.Tests;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Search;
using Moq;
using NUnit.Framework;
using System.Reflection;

public class OutboxProcessorQueryContractTests
{
    private Mock<IMongoCollection<OutboxMessage>> _collectionMock;
    private Mock<IMongoDatabase> _databaseMock;
    private Mock<ILogger<OutboxProcessor>> _loggerMock;
    private Mock<IMongoHelper> _mongoHelperMock;
    private OutboxOptions _options;
    private Mock<IOutboxPublisher> _publisherMock;
    private Mock<IServiceScopeFactory> _scopeFactoryMock;
    private FakeTimeProvider _timeProvider;

    [Test]
    public async Task ProcessBatchAsync_UsesPollingFilterAndSortAlignedWithPollingIndex()
    {
        // Arrange
        var (collection, queryCapture) = CapturingMongoCollectionProxy<OutboxMessage>.Create(CreateCursor<OutboxMessage>());
        var sut = CreateSut(collection);
        var method = GetPrivateMethod("ProcessBatchAsync", typeof(CancellationToken));

        // Act
        var processedCount = await (Task<Int32>)method.Invoke(sut, [CancellationToken.None])!;

        // Assert
        processedCount.Should().Be(0);
        queryCapture.CapturedFilter.Should().NotBeNull();
        queryCapture.CapturedOptions.Should().NotBeNull();
        queryCapture.CapturedOptions!.Sort.Should().NotBeNull();
        queryCapture.CapturedOptions.Limit.Should().Be(_options.BatchSize);

        var renderedFilter = Render(queryCapture.CapturedFilter!);
        ContainsEquality(renderedFilter, nameof(OutboxMessage.State), new BsonInt32((Int32)OutboxMessageState.Pending)).Should().BeTrue();
        ContainsEquality(renderedFilter, nameof(OutboxMessage.IsLocked), BsonBoolean.False).Should().BeTrue();
        ContainsNullEquality(renderedFilter, nameof(OutboxMessage.NextAttemptUtc)).Should().BeTrue();
        ContainsComparison(renderedFilter,
                           nameof(OutboxMessage.NextAttemptUtc),
                           "$lte",
                           new BsonDateTime(_timeProvider.GetUtcNow().UtcDateTime)).Should().BeTrue();
        ContainsComparison(renderedFilter,
                           nameof(OutboxMessage.LockedUtc),
                           "$lte",
                           new BsonDateTime(_timeProvider.GetUtcNow().UtcDateTime - _options.LockTimeout)).Should().BeTrue();

        Render(queryCapture.CapturedOptions.Sort!).Should().BeEquivalentTo(new BsonDocument
        {
            { nameof(OutboxMessage.NextAttemptUtc), 1 },
            { nameof(OutboxMessage.LockedUtc), 1 },
            { "_id", 1 }
        });
    }

    [Test]
    public async Task ProcessMessageAsync_ClaimsMessagesUsingPollingEligibilityContract()
    {
        // Arrange
        var message = new OutboxMessage
        {
            Id = ObjectId.GenerateNewId(),
            Type = "TestPayload",
            Payload = new("Name", "Claim me"),
            State = OutboxMessageState.Pending
        };
        FilterDefinition<OutboxMessage>? capturedClaimFilter = null;
        _collectionMock
            .Setup(c => c.FindOneAndUpdateAsync(
                       It.IsAny<FilterDefinition<OutboxMessage>>(),
                       It.IsAny<UpdateDefinition<OutboxMessage>>(),
                       It.IsAny<FindOneAndUpdateOptions<OutboxMessage, OutboxMessage>>(),
                       It.IsAny<CancellationToken>()))
            .Callback<FilterDefinition<OutboxMessage>, UpdateDefinition<OutboxMessage>, FindOneAndUpdateOptions<OutboxMessage, OutboxMessage>,
                CancellationToken>((filter, _, _, _) => capturedClaimFilter = filter)
            .ReturnsAsync(message);

        var updateResultMock = new Mock<UpdateResult>();
        updateResultMock.Setup(x => x.ModifiedCount).Returns(1);
        _collectionMock
            .Setup(c => c.UpdateOneAsync(
                       It.IsAny<FilterDefinition<OutboxMessage>>(),
                       It.IsAny<UpdateDefinition<OutboxMessage>>(),
                       It.IsAny<UpdateOptions>(),
                       It.IsAny<CancellationToken>()))
            .ReturnsAsync(updateResultMock.Object);

        _publisherMock.Setup(p => p.PublishAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var sut = CreateSut();
        var method = GetPrivateMethod("ProcessMessageAsync",
                                      typeof(IMongoCollection<OutboxMessage>),
                                      typeof(OutboxMessage),
                                      typeof(IOutboxPublisher),
                                      typeof(CancellationToken));

        // Act
        await (Task)method.Invoke(sut, [_collectionMock.Object, message, _publisherMock.Object, CancellationToken.None])!;

        // Assert
        capturedClaimFilter.Should().NotBeNull();
        var renderedFilter = Render(capturedClaimFilter!);
        ContainsEquality(renderedFilter, "_id", new BsonObjectId(message.Id)).Should().BeTrue();
        ContainsEquality(renderedFilter, nameof(OutboxMessage.State), new BsonInt32((Int32)OutboxMessageState.Pending)).Should().BeTrue();
        ContainsEquality(renderedFilter, nameof(OutboxMessage.IsLocked), BsonBoolean.False).Should().BeTrue();
        ContainsNullEquality(renderedFilter, nameof(OutboxMessage.NextAttemptUtc)).Should().BeTrue();
        ContainsComparison(renderedFilter,
                           nameof(OutboxMessage.NextAttemptUtc),
                           "$lte",
                           new BsonDateTime(_timeProvider.GetUtcNow().UtcDateTime)).Should().BeTrue();
        ContainsComparison(renderedFilter,
                           nameof(OutboxMessage.LockedUtc),
                           "$lte",
                           new BsonDateTime(_timeProvider.GetUtcNow().UtcDateTime - _options.LockTimeout)).Should().BeTrue();
    }

    [SetUp]
    public void SetUp()
    {
        _options = new()
        {
            CollectionName = "TestOutbox",
            BatchSize = 10,
            MaxRetries = 3,
            PollingInterval = TimeSpan.FromMilliseconds(50),
            LockTimeout = TimeSpan.FromMinutes(5),
            RetryBackoffInitialDelay = TimeSpan.FromSeconds(1),
            RetryBackoffMaxDelay = TimeSpan.FromSeconds(30)
        };

        _timeProvider = new(new(2026, 04, 11, 12, 0, 0, TimeSpan.Zero));
        _loggerMock = new();
        _collectionMock = new();
        _databaseMock = new();
        _databaseMock
            .Setup(d => d.GetCollection<OutboxMessage>(_options.CollectionName, null))
            .Returns(_collectionMock.Object);

        _mongoHelperMock = new();
        _mongoHelperMock.Setup(h => h.Database).Returns(_databaseMock.Object);

        _publisherMock = new();
        var serviceScopeMock = new Mock<IServiceScope>();
        var services = new ServiceCollection();
        services.AddSingleton(_publisherMock.Object);
        serviceScopeMock.Setup(s => s.ServiceProvider).Returns(services.BuildServiceProvider());

        _scopeFactoryMock = new();
        _scopeFactoryMock.Setup(f => f.CreateScope()).Returns(serviceScopeMock.Object);
    }

    private static Boolean ContainsComparison(BsonValue value, String field, String comparisonOperator, BsonValue expected)
        => value switch
        {
            BsonDocument document => (document.TryGetValue(field, out var filter) &&
                                      filter is BsonDocument filterDocument &&
                                      filterDocument.TryGetValue(comparisonOperator, out var comparisonValue) &&
                                      comparisonValue == expected) ||
                                     document.Elements.Any(element => ContainsComparison(element.Value, field, comparisonOperator, expected)),
            BsonArray array => array.Any(item => ContainsComparison(item, field, comparisonOperator, expected)),
            _ => false
        };

    private static Boolean ContainsEquality(BsonValue value, String field, BsonValue expected)
        => value switch
        {
            BsonDocument document => (document.TryGetValue(field, out var directValue) &&
                                      directValue == expected) ||
                                     document.Elements.Any(element => ContainsEquality(element.Value, field, expected)),
            BsonArray array => array.Any(item => ContainsEquality(item, field, expected)),
            _ => false
        };

    private static Boolean ContainsNullEquality(BsonValue value, String field)
        => ContainsEquality(value, field, BsonNull.Value);

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

    private static MethodInfo GetPrivateMethod(String name, params Type[] parameterTypes)
        => typeof(OutboxProcessor).GetMethod(name,
                                             BindingFlags.Instance | BindingFlags.NonPublic,
                                             null,
                                             parameterTypes,
                                             null)
           ?? throw new InvalidOperationException($"Could not find method {name}.");

    private static BsonDocument Render(FilterDefinition<OutboxMessage> filter)
    {
        var serializerRegistry = BsonSerializer.SerializerRegistry;
        var documentSerializer = serializerRegistry.GetSerializer<OutboxMessage>();
        return filter.Render(new(documentSerializer, serializerRegistry));
    }

    private static BsonDocument Render(SortDefinition<OutboxMessage> sort)
    {
        var serializerRegistry = BsonSerializer.SerializerRegistry;
        var documentSerializer = serializerRegistry.GetSerializer<OutboxMessage>();
        return sort.Render(new(documentSerializer, serializerRegistry));
    }

    private OutboxProcessor CreateSut()
        => new(
            _mongoHelperMock.Object,
            _options,
            _scopeFactoryMock.Object,
            _timeProvider,
            _loggerMock.Object);

    private OutboxProcessor CreateSut(IMongoCollection<OutboxMessage> collection)
    {
        var databaseMock = new Mock<IMongoDatabase>();
        databaseMock
            .Setup(d => d.GetCollection<OutboxMessage>(_options.CollectionName, null))
            .Returns(collection);

        var mongoHelperMock = new Mock<IMongoHelper>();
        mongoHelperMock.Setup(h => h.Database).Returns(databaseMock.Object);

        return new(
            mongoHelperMock.Object,
            _options,
            _scopeFactoryMock.Object,
            _timeProvider,
            _loggerMock.Object);
    }

    private class CapturingMongoCollectionProxy<TDocument> : DispatchProxy
    {
        private static readonly IMongoDatabase Database = Mock.Of<IMongoDatabase>();
        private static readonly IMongoIndexManager<TDocument> IndexManager = Mock.Of<IMongoIndexManager<TDocument>>();
        private static readonly MongoCollectionSettings Settings = new();

        private IMongoCollection<TDocument> _collection = null!;
        private IAsyncCursor<TDocument> _cursor = null!;

        public FilterDefinition<TDocument>? CapturedFilter { get; private set; }

        public FindOptions<TDocument, TDocument>? CapturedOptions { get; private set; }

        public static (IMongoCollection<TDocument> Collection, CapturingMongoCollectionProxy<TDocument> Proxy) Create(
            IAsyncCursor<TDocument> cursor)
        {
            var collection = Create<IMongoCollection<TDocument>, CapturingMongoCollectionProxy<TDocument>>();
            var proxy = (CapturingMongoCollectionProxy<TDocument>)(Object)collection;
            proxy._collection = collection;
            proxy._cursor = cursor;
            return (collection, proxy);
        }

        protected override Object? Invoke(MethodInfo? targetMethod, Object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);

            return targetMethod.Name switch
            {
                "FindAsync" when targetMethod.IsGenericMethod => HandleFindAsync(targetMethod.GetGenericArguments()[0], args),
                "FindSync" when targetMethod.IsGenericMethod => HandleFindSync(targetMethod.GetGenericArguments()[0], args),
                "get_CollectionNamespace" => new CollectionNamespace(new DatabaseNamespace("Tests"), typeof(TDocument).Name),
                "get_Database" => Database,
                "get_DocumentSerializer" => BsonSerializer.SerializerRegistry.GetSerializer<TDocument>(),
                "get_Indexes" => IndexManager,
                "get_SearchIndexes" => Mock.Of<IMongoSearchIndexManager>(),
                "get_Settings" => Settings,
                "WithReadConcern" or "WithReadPreference" or "WithWriteConcern" => _collection,
                _ => throw new NotSupportedException($"Method '{targetMethod.Name}' is not supported by the capturing test collection.")
            };
        }

        private void CaptureFind(Type projectionType, Object?[]? args, out Int32 filterIndex, out Int32 optionsIndex)
        {
            if (projectionType != typeof(TDocument))
            {
                throw new NotSupportedException($"Projection '{projectionType}' is not supported by the capturing test collection.");
            }

            (filterIndex, optionsIndex) = args?.Length switch
            {
                3 => (0, 1),
                4 => (1, 2),
                _ => throw new NotSupportedException("Unexpected Find invocation shape.")
            };

            CapturedFilter = (FilterDefinition<TDocument>)args![filterIndex]!;
            CapturedOptions = (FindOptions<TDocument, TDocument>?)args[optionsIndex];
        }

        private Object HandleFindAsync(Type projectionType, Object?[]? args)
        {
            CaptureFind(projectionType, args, out _, out _);
            return Task.FromResult(_cursor);
        }

        private Object HandleFindSync(Type projectionType, Object?[]? args)
        {
            CaptureFind(projectionType, args, out _, out _);
            return _cursor;
        }
    }
}
