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
        FilterDefinition<OutboxMessage>? capturedFilter = null;
        FindOptions<OutboxMessage, OutboxMessage>? capturedOptions = null;
        _collectionMock
            .Setup(c => c.FindAsync(
                       It.IsAny<FilterDefinition<OutboxMessage>>(),
                       It.IsAny<FindOptions<OutboxMessage, OutboxMessage>>(),
                       It.IsAny<CancellationToken>()))
            .Callback<FilterDefinition<OutboxMessage>, FindOptions<OutboxMessage, OutboxMessage>, CancellationToken>((filter, options, _) =>
            {
                capturedFilter = filter;
                capturedOptions = options;
            })
            .ReturnsAsync(CreateCursor<OutboxMessage>());

        var sut = CreateSut();
        var method = GetPrivateMethod("ProcessBatchAsync", typeof(CancellationToken));

        // Act
        var processedCount = await (Task<Int32>)method.Invoke(sut, [CancellationToken.None])!;

        // Assert
        processedCount.Should().Be(0);
        capturedFilter.Should().NotBeNull();
        capturedOptions.Should().NotBeNull();
        capturedOptions!.Limit.Should().Be(_options.BatchSize);
        capturedOptions.Sort.Should().NotBeNull();

        var renderedFilter = Render(capturedFilter!);
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

        Render(capturedOptions.Sort!).Should().BeEquivalentTo(new BsonDocument
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
}
