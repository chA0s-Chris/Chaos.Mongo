// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests.Integration.Queues;

using Chaos.Mongo.Queues;
using Chaos.Testing.Logging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using NUnit.Framework;
using System.Linq.Expressions;
using Testcontainers.MongoDb;
using MongoDefaults = MongoDefaults;

public class MongoQueueRetentionIntegrationTests
{
    private const String ClosedItemTtlIndexName = "IX_Queue_ClosedUtc_TTL";
    private MongoDbContainer _container;

    [OneTimeSetUp]
    public async Task GetMongoDbContainer()
        => _container = await MongoDbTestContainer.StartContainerAsync();

    [Test]
    public async Task Queue_WithImmediateDelete_RemovesProcessedItemAndDropsTtlIndex()
    {
        // Arrange
        var handler = new PassivePayloadHandler();
        var uniqueDbName = $"QueueImmediateDeleteTest_{Guid.NewGuid():N}";
        const String collectionName = "retention-queue";

        await using var serviceProvider = CreateServiceProvider(uniqueDbName,
                                                                collectionName,
                                                                queue => queue.WithPayloadHandler(_ => handler)
                                                                              .WithImmediateDelete()
                                                                              .WithoutAutoStartSubscription());
        var helper = serviceProvider.GetRequiredService<IMongoHelper>();
        var collection = helper.Database.GetCollection<MongoQueueItem<RetentionPayload>>(collectionName);
        var indexCollection = helper.Database.GetCollection<BsonDocument>(collectionName);
        var queue = serviceProvider.GetRequiredService<IMongoQueue<RetentionPayload>>();
        await queue.StartSubscriptionAsync();

        try
        {
            // Act
            await queue.PublishAsync(new()
            {
                Value = "delete-me"
            });

            await WaitUntilAsync(
                async cancellationToken => await collection.CountDocumentsAsync(
                    Builders<MongoQueueItem<RetentionPayload>>.Filter.Empty,
                    cancellationToken: cancellationToken) == 0,
                TimeSpan.FromSeconds(10));
            var ttlIndex = await FindIndexAsync(indexCollection, x => x["name"] == ClosedItemTtlIndexName);

            // Assert
            ttlIndex.Should().BeNull();
        }
        finally
        {
            await queue.StopSubscriptionAsync();
        }
    }

    [Test]
    public async Task Queue_WithoutExplicitRetention_CreatesDefaultTtlIndexAndKeepsClosedItem()
    {
        // Arrange
        var handler = new PassivePayloadHandler();
        var uniqueDbName = $"QueueDefaultRetentionTest_{Guid.NewGuid():N}";
        const String collectionName = "retention-queue";

        await using var serviceProvider = CreateServiceProvider(uniqueDbName,
                                                                collectionName,
                                                                queue => queue.WithPayloadHandler(_ => handler)
                                                                              .WithoutAutoStartSubscription());
        var helper = serviceProvider.GetRequiredService<IMongoHelper>();
        var collection = helper.Database.GetCollection<MongoQueueItem<RetentionPayload>>(collectionName);
        var indexCollection = helper.Database.GetCollection<BsonDocument>(collectionName);
        var queue = serviceProvider.GetRequiredService<IMongoQueue<RetentionPayload>>();
        await queue.StartSubscriptionAsync();

        try
        {
            // Act
            await queue.PublishAsync(new()
            {
                Value = "retain-me"
            });

            var closedItem = await WaitForQueueItemAsync(collection,
                                                         x => x.Payload.Value == "retain-me" && x.IsClosed,
                                                         TimeSpan.FromSeconds(10));
            var ttlIndex = await WaitForIndexAsync(indexCollection,
                                                   x => x["name"] == ClosedItemTtlIndexName,
                                                   TimeSpan.FromSeconds(10));

            // Assert
            closedItem.IsClosed.Should().BeTrue();
            closedItem.IsLocked.Should().BeFalse();
            closedItem.ClosedUtc.Should().NotBeNull();
            ttlIndex["expireAfterSeconds"].ToDouble().Should().Be(MongoDefaults.QueueClosedItemRetention!.Value.TotalSeconds);
            var partialFilter = ttlIndex["partialFilterExpression"].AsBsonDocument;
            partialFilter["IsClosed"].AsBoolean.Should().BeTrue();
            UsesLegacyCompatibleTerminalFilter(partialFilter)
                .Should()
                .BeTrue("the TTL filter must exclude terminal items while still matching legacy queue items without IsTerminal");
        }
        finally
        {
            await queue.StopSubscriptionAsync();
        }
    }

    [Test]
    public async Task StartSubscription_SameCollectionWithDifferentRetentionPolicies_ReconcilesTtlIndex()
    {
        // Arrange
        var uniqueDbName = $"QueueRetentionPolicySwitchTest_{Guid.NewGuid():N}";
        const String collectionName = "retention-queue";

        await using var firstServiceProvider = CreateServiceProvider(uniqueDbName,
                                                                     collectionName,
                                                                     queue => queue.WithPayloadHandler(_ => new PassivePayloadHandler())
                                                                                   .WithClosedItemRetention(TimeSpan.FromHours(2))
                                                                                   .WithoutAutoStartSubscription());
        var firstHelper = firstServiceProvider.GetRequiredService<IMongoHelper>();
        var firstIndexCollection = firstHelper.Database.GetCollection<BsonDocument>(collectionName);
        var firstQueue = firstServiceProvider.GetRequiredService<IMongoQueue<RetentionPayload>>();

        await using var secondServiceProvider = CreateServiceProvider(uniqueDbName,
                                                                      collectionName,
                                                                      queue => queue.WithPayloadHandler(_ => new PassivePayloadHandler())
                                                                                    .WithClosedItemRetention(TimeSpan.FromMinutes(15))
                                                                                    .WithoutAutoStartSubscription());
        var secondHelper = secondServiceProvider.GetRequiredService<IMongoHelper>();
        var secondIndexCollection = secondHelper.Database.GetCollection<BsonDocument>(collectionName);
        var secondQueue = secondServiceProvider.GetRequiredService<IMongoQueue<RetentionPayload>>();

        await using var thirdServiceProvider = CreateServiceProvider(uniqueDbName,
                                                                     collectionName,
                                                                     queue => queue.WithPayloadHandler(_ => new PassivePayloadHandler())
                                                                                   .WithImmediateDelete()
                                                                                   .WithoutAutoStartSubscription());
        var thirdHelper = thirdServiceProvider.GetRequiredService<IMongoHelper>();
        var thirdIndexCollection = thirdHelper.Database.GetCollection<BsonDocument>(collectionName);
        var thirdQueue = thirdServiceProvider.GetRequiredService<IMongoQueue<RetentionPayload>>();

        // Act & Assert
        await firstQueue.StartSubscriptionAsync();
        try
        {
            var firstTtlIndex = await WaitForIndexAsync(firstIndexCollection,
                                                        x => x["name"] == ClosedItemTtlIndexName,
                                                        TimeSpan.FromSeconds(10));
            firstTtlIndex["expireAfterSeconds"].ToDouble().Should().Be(TimeSpan.FromHours(2).TotalSeconds);
        }
        finally
        {
            await firstQueue.StopSubscriptionAsync();
        }

        await secondQueue.StartSubscriptionAsync();
        try
        {
            var secondTtlIndex = await WaitForIndexAsync(secondIndexCollection,
                                                         x => x["name"] == ClosedItemTtlIndexName,
                                                         TimeSpan.FromSeconds(10));
            secondTtlIndex["expireAfterSeconds"].ToDouble().Should().Be(TimeSpan.FromMinutes(15).TotalSeconds);
        }
        finally
        {
            await secondQueue.StopSubscriptionAsync();
        }

        await thirdQueue.StartSubscriptionAsync();
        try
        {
            var thirdTtlIndex = await FindIndexAsync(thirdIndexCollection, x => x["name"] == ClosedItemTtlIndexName);
            thirdTtlIndex.Should().BeNull();
        }
        finally
        {
            await thirdQueue.StopSubscriptionAsync();
        }
    }

    private static async Task<BsonDocument?> FindIndexAsync(
        IMongoCollection<BsonDocument> collection,
        Func<BsonDocument, Boolean> predicate,
        CancellationToken cancellationToken = default)
    {
        var indexes = await collection.Indexes.ListAsync(cancellationToken);
        return (await indexes.ToListAsync(cancellationToken)).FirstOrDefault(predicate);
    }

    private static Boolean IsTerminalFalseClause(BsonValue clause)
        => clause is BsonDocument clauseDocument &&
           clauseDocument.TryGetValue(nameof(MongoQueueItem.IsTerminal), out var filter) &&
           filter == BsonBoolean.False;

    private static Boolean IsTerminalFalseOrMissingFilter(BsonDocument document)
        => document.TryGetValue("$or", out var orFilter) &&
           orFilter is BsonArray clauses &&
           clauses.Any(IsTerminalFalseClause) &&
           clauses.Any(IsTerminalMissingClause);

    private static Boolean IsTerminalFalseOrNullInFilter(BsonDocument document)
        => document.TryGetValue(nameof(MongoQueueItem.IsTerminal), out var filter) &&
           filter is BsonDocument filterDocument &&
           filterDocument.TryGetValue("$in", out var inValues) &&
           inValues is BsonArray values &&
           values.Contains(BsonBoolean.False) &&
           values.Contains(BsonNull.Value);

    private static Boolean IsTerminalMissingClause(BsonValue clause)
        => clause is BsonDocument clauseDocument &&
           clauseDocument.TryGetValue(nameof(MongoQueueItem.IsTerminal), out var filter) &&
           filter is BsonDocument filterDocument &&
           filterDocument.TryGetValue("$exists", out var existsValue) &&
           existsValue == BsonBoolean.False;

    private static Boolean IsTerminalNotTrueFilter(BsonDocument document)
        => document.TryGetValue(nameof(MongoQueueItem.IsTerminal), out var filter) &&
           filter is BsonDocument filterDocument &&
           filterDocument.TryGetValue("$ne", out var notEqualValue) &&
           notEqualValue == BsonBoolean.True;

    private static Boolean UsesLegacyCompatibleTerminalFilter(BsonValue value)
        => value switch
        {
            BsonDocument document => IsTerminalNotTrueFilter(document) ||
                                     IsTerminalFalseOrNullInFilter(document) ||
                                     IsTerminalFalseOrMissingFilter(document) ||
                                     document.Elements.Any(element => UsesLegacyCompatibleTerminalFilter(element.Value)),
            BsonArray array => array.Any(UsesLegacyCompatibleTerminalFilter),
            _ => false
        };

    private static async Task<BsonDocument> WaitForIndexAsync(
        IMongoCollection<BsonDocument> collection,
        Func<BsonDocument, Boolean> predicate,
        TimeSpan timeout)
    {
        return await WaitUntilAsync(async cancellationToken => await FindIndexAsync(collection, predicate, cancellationToken), timeout)
               ?? throw new TimeoutException("Index did not reach the expected state.");
    }

    private static async Task<MongoQueueItem<RetentionPayload>> WaitForQueueItemAsync(
        IMongoCollection<MongoQueueItem<RetentionPayload>> collection,
        Expression<Func<MongoQueueItem<RetentionPayload>, Boolean>> filter,
        TimeSpan timeout)
    {
        return await WaitUntilAsync(async cancellationToken => await collection.Find(filter).FirstOrDefaultAsync(cancellationToken), timeout)
               ?? throw new TimeoutException("Queue item did not reach the expected state.");
    }

    private static async Task WaitUntilAsync(Func<CancellationToken, Task<Boolean>> predicate, TimeSpan timeout)
        => _ = await WaitUntilAsync(async cancellationToken => await predicate(cancellationToken) ? true : (Boolean?)null, timeout);

    private static async Task<T?> WaitUntilAsync<T>(Func<CancellationToken, Task<T?>> action, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var result = await action(cts.Token);
                if (result is not null)
                {
                    return result;
                }

                await Task.Delay(100, cts.Token);
            }
        }
        catch (OperationCanceledException e) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException("The expected state was not reached.", e);
        }

        throw new TimeoutException("The expected state was not reached.");
    }

    private ServiceProvider CreateServiceProvider(
        String databaseName,
        String collectionName,
        Action<MongoQueueBuilder<RetentionPayload>> configureQueue)
    {
        var services = new ServiceCollection();
        services.AddNUnitTestLogging();

        var url = MongoUrl.Create(_container.GetConnectionString());
        services.AddMongo(url, databaseName)
                .WithQueue<RetentionPayload>(queue =>
                {
                    queue.WithCollectionName(collectionName);
                    configureQueue(queue);
                });

        return services.BuildServiceProvider();
    }

    private sealed class PassivePayloadHandler : IMongoQueuePayloadHandler<RetentionPayload>
    {
        public Task HandlePayloadAsync(RetentionPayload payload, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class RetentionPayload
    {
        public String Value { get; init; } = String.Empty;
    }
}
