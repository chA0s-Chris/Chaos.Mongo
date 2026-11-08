// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests.Integration.Queues;

using Chaos.Mongo.Queues;
using Chaos.Mongo.TestSupport;
using Chaos.Testing.Logging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Driver;
using NUnit.Framework;
using System.Linq.Expressions;
using Testcontainers.MongoDb;
using MongoDefaults = MongoDefaults;

[Category("Integration")]
public class MongoQueueRetentionIntegrationTests
{
    private const String ClosedItemTtlIndexName = "IX_Queue_ClosedUtc_TTL";
    private MongoDbContainer _container;

    [OneTimeSetUp]
    public async Task GetMongoDbContainer()
        => _container = await MongoDbTestContainer.StartContainerAsync();

    [Test]
    [Explicit("Runs only via process isolation from " +
              nameof(Queue_ClosedItemTtlFilterUnderCamelCaseConvention_MatchesEveryLegacyIsTerminalValueAndExcludesOpenItems) +
              "; registers process-global convention and class-map state.")]
    public async Task Queue_ClosedItemTtlFilterUnderCamelCaseConvention_IsolatedCase()
    {
        // Arrange: register a global camelCase convention for the queue item types before anything in this fresh
        // process has used them, then start (and immediately stop) a subscription only to let production code
        // create the real TTL index under that convention, exactly as the default-convention case above does, so
        // the filter asserted below is the exact partialFilterExpression the server stored for a camelCase-
        // registered host, not a hand-built approximation of it.
        ConventionRegistry.Register(
            nameof(Queue_ClosedItemTtlFilterUnderCamelCaseConvention_IsolatedCase),
            new ConventionPack
            {
                new CamelCaseElementNameConvention()
            },
            type => type == typeof(MongoQueueItem) || type == typeof(MongoQueueItem<RetentionPayload>));

        var container = await MongoDbTestContainer.StartContainerAsync();
        var uniqueDbName = $"QueueCamelCaseTtlFilterTest_{Guid.NewGuid():N}";
        const String collectionName = "camelcase-retention-queue";

        var services = new ServiceCollection();
        services.AddNUnitTestLogging();
        var url = MongoUrl.Create(container.GetConnectionString());
        services.AddMongo(url, uniqueDbName)
                .WithQueue<RetentionPayload>(queue => queue.WithCollectionName(collectionName)
                                                           .WithPayloadHandler(_ => new PassivePayloadHandler())
                                                           .WithoutAutoStartSubscription());
        await using var serviceProvider = services.BuildServiceProvider();
        var helper = serviceProvider.GetRequiredService<IMongoHelper>();
        var indexCollection = helper.Database.GetCollection<BsonDocument>(collectionName);
        var queue = serviceProvider.GetRequiredService<IMongoQueue<RetentionPayload>>();

        BsonDocument ttlIndex;
        await queue.StartSubscriptionAsync();
        try
        {
            ttlIndex = await WaitForIndexAsync(indexCollection, x => x["name"] == ClosedItemTtlIndexName, TimeSpan.FromSeconds(10));
        }
        finally
        {
            await queue.StopSubscriptionAsync();
        }

        // Act: insert raw legacy documents (bypassing the typed model) directly, with the subscription stopped so
        // nothing else touches this collection while the filter below is evaluated. The field names are the
        // library's pinned member names, not camelCase: pinning is convention-independent by design, which is the
        // whole point of this change, so a camelCase-registered host still writes (and must still match) exactly
        // these stored names, not renamed ones. An open, never-closed item is included as a retained control: the
        // partial filter requires IsClosed: true, so it must never be selected.
        var closedUtc = DateTime.UtcNow.AddHours(-1);
        var rawCollection = helper.Database.GetCollection<BsonDocument>(collectionName);
        await rawCollection.InsertManyAsync([
            new BsonDocument
            {
                { "Name", "terminal-false" },
                { "IsClosed", true },
                { "IsTerminal", false },
                { "ClosedUtc", closedUtc }
            },
            new BsonDocument
            {
                { "Name", "terminal-null" },
                { "IsClosed", true },
                { "IsTerminal", BsonNull.Value },
                { "ClosedUtc", closedUtc }
            },
            new BsonDocument
            {
                { "Name", "terminal-missing" },
                { "IsClosed", true },
                { "ClosedUtc", closedUtc }
            },
            new BsonDocument
            {
                { "Name", "terminal-true" },
                { "IsClosed", true },
                { "IsTerminal", true },
                { "ClosedUtc", closedUtc }
            },
            new BsonDocument
            {
                { "Name", "open-item" },
                { "IsClosed", false },
                { "IsTerminal", false }
            }
        ]);

        var matches = await rawCollection.Find(new BsonDocumentFilterDefinition<BsonDocument>(ttlIndex["partialFilterExpression"].AsBsonDocument))
                                         .ToListAsync();

        // Assert
        matches.Select(x => x["Name"].AsString).Should().BeEquivalentTo(["terminal-false", "terminal-null", "terminal-missing"]);
    }

    [Test]
    public async Task Queue_ClosedItemTtlFilterUnderCamelCaseConvention_MatchesEveryLegacyIsTerminalValueAndExcludesOpenItems()
    {
        // Arrange & Act: the inner case runs in its own freshly started process (see
        // IsolatedConventionTestProcess), because BsonClassMap/ConventionRegistry registration is process-global
        // and irreversible, and every other test in this shared process may already have registered
        // MongoQueueItem's class map under the default convention.
        var result = IsolatedConventionTestProcess.Run(
            $"{typeof(MongoQueueRetentionIntegrationTests).FullName}.{nameof(Queue_ClosedItemTtlFilterUnderCamelCaseConvention_IsolatedCase)}");

        // Assert
        result.ExitCode.Should().Be(0, "the isolated camelCase case must pass; output:\n{0}\n{1}", result.StandardOutput, result.StandardError);
    }

    [Test]
    public async Task Queue_ClosedItemTtlFilter_MatchesEveryLegacyIsTerminalValue()
    {
        // Arrange: start (and then stop) the subscription only to let production code create the real TTL
        // index, so the filter asserted below is the exact partialFilterExpression the server stored, not a
        // hand-built approximation of it.
        var uniqueDbName = $"QueueLegacyTerminalFilterTest_{Guid.NewGuid():N}";
        const String collectionName = "retention-queue";

        await using var serviceProvider = CreateServiceProvider(uniqueDbName,
                                                                collectionName,
                                                                queue => queue.WithPayloadHandler(_ => new PassivePayloadHandler())
                                                                              .WithoutAutoStartSubscription());
        var helper = serviceProvider.GetRequiredService<IMongoHelper>();
        var indexCollection = helper.Database.GetCollection<BsonDocument>(collectionName);
        var queue = serviceProvider.GetRequiredService<IMongoQueue<RetentionPayload>>();

        BsonDocument ttlIndex;
        await queue.StartSubscriptionAsync();
        try
        {
            ttlIndex = await WaitForIndexAsync(indexCollection, x => x["name"] == ClosedItemTtlIndexName, TimeSpan.FromSeconds(10));
        }
        finally
        {
            await queue.StopSubscriptionAsync();
        }

        // Act: insert raw legacy documents (bypassing the typed model, which cannot represent a missing or
        // explicit-null IsTerminal for a non-nullable member) directly, with the subscription stopped so nothing
        // else touches this collection while the filter below is evaluated.
        var closedUtc = DateTime.UtcNow.AddHours(-1);
        var rawCollection = helper.Database.GetCollection<BsonDocument>(collectionName);
        await rawCollection.InsertManyAsync([
            new BsonDocument
            {
                { "Name", "terminal-false" },
                { "IsClosed", true },
                { "IsTerminal", false },
                { "ClosedUtc", closedUtc }
            },
            new BsonDocument
            {
                { "Name", "terminal-null" },
                { "IsClosed", true },
                { "IsTerminal", BsonNull.Value },
                { "ClosedUtc", closedUtc }
            },
            new BsonDocument
            {
                { "Name", "terminal-missing" },
                { "IsClosed", true },
                { "ClosedUtc", closedUtc }
            },
            new BsonDocument
            {
                { "Name", "terminal-true" },
                { "IsClosed", true },
                { "IsTerminal", true },
                { "ClosedUtc", closedUtc }
            },
            new BsonDocument
            {
                { "Name", "not-closed" },
                { "IsClosed", false },
                { "IsTerminal", false },
                { "ClosedUtc", closedUtc }
            },
            new BsonDocument
            {
                { "Name", "closed-utc-missing" },
                { "IsClosed", true },
                { "IsTerminal", false }
            }
        ]);

        var matches = await rawCollection.Find(new BsonDocumentFilterDefinition<BsonDocument>(ttlIndex["partialFilterExpression"].AsBsonDocument))
                                         .ToListAsync();

        // Assert
        matches.Select(x => x["Name"].AsString).Should().BeEquivalentTo(["terminal-false", "terminal-null", "terminal-missing"]);
    }

    [Test]
    [Explicit("Runs only via process isolation from " + nameof(Queue_RetentionUnderCamelCaseConvention_StillFiltersAndDeletesCorrectly) +
              "; registers process-global convention and class-map state.")]
    public async Task Queue_RetentionUnderCamelCaseConvention_IsolatedCase()
    {
        // Arrange: register a global camelCase convention for the queue item types before anything in this fresh
        // process has used them, then start (and immediately stop) a subscription only to let production code
        // create the real TTL index under that convention, exactly as the filter-selection isolated case above
        // does. The container's TTL monitor is sped up so the eventual deletion assertion below stays within a
        // bounded wait.
        ConventionRegistry.Register(
            nameof(Queue_RetentionUnderCamelCaseConvention_IsolatedCase),
            new ConventionPack
            {
                new CamelCaseElementNameConvention()
            },
            type => type == typeof(MongoQueueItem) || type == typeof(MongoQueueItem<RetentionPayload>));

        var container = await MongoDbTestContainer.StartContainerAsync();
        var adminClient = new MongoClient(container.GetConnectionString());
        await adminClient.GetDatabase("admin")
                         .RunCommandAsync<BsonDocument>(new BsonDocument
                         {
                             { "setParameter", 1 },
                             { "ttlMonitorSleepSecs", 1 }
                         });

        var uniqueDbName = $"QueueCamelCaseRetentionTest_{Guid.NewGuid():N}";
        const String collectionName = "camelcase-retention-queue";

        await using var serviceProvider = CreateServiceProvider(uniqueDbName,
                                                                collectionName,
                                                                queue => queue.WithPayloadHandler(_ => new PassivePayloadHandler())
                                                                              .WithRetentionPeriod(TimeSpan.FromSeconds(2))
                                                                              .WithoutAutoStartSubscription());
        var helper = serviceProvider.GetRequiredService<IMongoHelper>();
        var indexCollection = helper.Database.GetCollection<BsonDocument>(collectionName);
        var queue = serviceProvider.GetRequiredService<IMongoQueue<RetentionPayload>>();

        // Act: start (and then stop) the subscription only to let production code create the real TTL index.
        // The subscription is stopped, with nothing published through it, before any document is inserted below:
        // a running subscription would itself pick up and close the raw open-item control inserted next, which
        // must instead reach the assertion below exactly as seeded.
        await queue.StartSubscriptionAsync();
        try
        {
            _ = await WaitForIndexAsync(indexCollection, x => x["name"] == ClosedItemTtlIndexName, TimeSpan.FromSeconds(10));
        }
        finally
        {
            await queue.StopSubscriptionAsync();
        }

        // Insert raw legacy documents (bypassing the typed model, which cannot represent a missing or
        // explicit-null IsTerminal for a non-nullable member) directly: three closed items with IsTerminal
        // false/null/missing, eligible for TTL deletion, plus a closed-and-terminal item and a never-closed
        // "open" item, both ineligible controls. The field names are the library's pinned member names, not
        // camelCase: pinning is convention-independent by design, which is the whole point of this change, so a
        // camelCase-registered host still writes (and must still have TTL match) exactly these stored names, not
        // renamed ones.
        var closedUtc = DateTime.UtcNow.AddHours(-1);
        var rawCollection = helper.Database.GetCollection<BsonDocument>(collectionName);
        var eligibleIds = new[]
        {
            ObjectId.GenerateNewId(),
            ObjectId.GenerateNewId(),
            ObjectId.GenerateNewId()
        };
        var terminalId = ObjectId.GenerateNewId();
        var openId = ObjectId.GenerateNewId();
        await rawCollection.InsertManyAsync([
            new BsonDocument
            {
                { "_id", eligibleIds[0] },
                { "IsClosed", true },
                { "IsTerminal", false },
                { "ClosedUtc", closedUtc }
            },
            new BsonDocument
            {
                { "_id", eligibleIds[1] },
                { "IsClosed", true },
                { "IsTerminal", BsonNull.Value },
                { "ClosedUtc", closedUtc }
            },
            new BsonDocument
            {
                { "_id", eligibleIds[2] },
                { "IsClosed", true },
                { "ClosedUtc", closedUtc }
            },
            new BsonDocument
            {
                { "_id", terminalId },
                { "IsClosed", true },
                { "IsTerminal", true },
                { "ClosedUtc", closedUtc }
            },
            new BsonDocument
            {
                { "_id", openId },
                { "IsClosed", false },
                { "IsTerminal", false }
            }
        ]);

        // Assert: all three eligible closed items are eventually deleted by TTL, while the terminal and open
        // controls, seeded under the camelCase convention's own pinned element names, remain untouched.
        await WaitUntilAsync(
            async cancellationToken => await rawCollection.Find(Builders<BsonDocument>.Filter.In("_id", eligibleIds))
                                                          .CountDocumentsAsync(cancellationToken) == 0,
            TimeSpan.FromSeconds(30));
        (await rawCollection.Find(Builders<BsonDocument>.Filter.In("_id", [terminalId, openId])).CountDocumentsAsync()).Should().Be(2);
    }

    [Test]
    public async Task Queue_RetentionUnderCamelCaseConvention_StillFiltersAndDeletesCorrectly()
    {
        // Arrange & Act: the inner case runs in its own freshly started process (see
        // IsolatedConventionTestProcess), because BsonClassMap/ConventionRegistry registration is process-global
        // and irreversible, and every other test in this shared process may already have registered
        // MongoQueueItem's class map under the default convention.
        var result = IsolatedConventionTestProcess.Run(
            $"{typeof(MongoQueueRetentionIntegrationTests).FullName}.{nameof(Queue_RetentionUnderCamelCaseConvention_IsolatedCase)}");

        // Assert
        result.ExitCode.Should().Be(0, "the isolated camelCase case must pass; output:\n{0}\n{1}", result.StandardOutput, result.StandardError);
    }

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
            await queue.PublishAsync(new RetentionPayload
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
            await queue.PublishAsync(new RetentionPayload
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
            ttlIndex["expireAfterSeconds"].ToDouble().Should().Be(MongoDefaults.QueueRetentionPeriod!.Value.TotalSeconds);
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
                                                                                   .WithRetentionPeriod(TimeSpan.FromHours(2))
                                                                                   .WithoutAutoStartSubscription());
        var firstHelper = firstServiceProvider.GetRequiredService<IMongoHelper>();
        var firstIndexCollection = firstHelper.Database.GetCollection<BsonDocument>(collectionName);
        var firstQueue = firstServiceProvider.GetRequiredService<IMongoQueue<RetentionPayload>>();

        await using var secondServiceProvider = CreateServiceProvider(uniqueDbName,
                                                                      collectionName,
                                                                      queue => queue.WithPayloadHandler(_ => new PassivePayloadHandler())
                                                                                    .WithRetentionPeriod(TimeSpan.FromMinutes(15))
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
