// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox.Tests.Integration;

using Chaos.Mongo.Configuration;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using NUnit.Framework;
using System.Collections.Concurrent;
using Testcontainers.MongoDb;

public class OutboxIndexContractIntegrationTests
{
    private MongoDbContainer _container;

    [Test]
    public async Task ConfigureAsync_CreatesOutboxIndexesWithExpectedContract()
    {
        // Arrange
        var databaseName = $"OutboxIndexContract_{Guid.NewGuid():N}";
        var retentionPeriod = TimeSpan.FromMinutes(20);

        await using var serviceProvider = CreateServiceProvider(databaseName,
                                                                builder => builder.WithRetentionPeriod(retentionPeriod));
        var helper = serviceProvider.GetRequiredService<IMongoHelper>();
        var collection = helper.Database.GetCollection<BsonDocument>(OutboxOptions.DefaultCollectionName);

        // Act
        await ConfigureAsync(serviceProvider, helper);
        var indexes = await (await collection.Indexes.ListAsync()).ToListAsync();

        // Assert
        var pollingIndex = indexes.Single(x => x["name"] == "IX_Outbox_Polling");
        pollingIndex["key"].AsBsonDocument.Should().BeEquivalentTo(new BsonDocument
        {
            { nameof(OutboxMessage.NextAttemptUtc), 1 },
            { nameof(OutboxMessage.LockedUtc), 1 },
            { "_id", 1 }
        });
        pollingIndex["partialFilterExpression"].AsBsonDocument.Should().BeEquivalentTo(
            new BsonDocument(nameof(OutboxMessage.State), (Int32)OutboxMessageState.Pending));

        var processedTtlIndex = indexes.Single(x => x["name"] == "IX_Outbox_ProcessedUtc_TTL");
        processedTtlIndex["key"].AsBsonDocument.Should().BeEquivalentTo(new BsonDocument(nameof(OutboxMessage.ProcessedUtc), 1));
        processedTtlIndex["expireAfterSeconds"].ToDouble().Should().Be(retentionPeriod.TotalSeconds);
        processedTtlIndex["partialFilterExpression"].AsBsonDocument.Should().BeEquivalentTo(new BsonDocument
        {
            { nameof(OutboxMessage.State), (Int32)OutboxMessageState.Processed },
            {
                nameof(OutboxMessage.ProcessedUtc), new BsonDocument
                {
                    { "$exists", true },
                    { "$type", (Int32)BsonType.DateTime }
                }
            }
        });

        var failedTtlIndex = indexes.Single(x => x["name"] == "IX_Outbox_FailedUtc_TTL");
        failedTtlIndex["key"].AsBsonDocument.Should().BeEquivalentTo(new BsonDocument(nameof(OutboxMessage.FailedUtc), 1));
        failedTtlIndex["expireAfterSeconds"].ToDouble().Should().Be(retentionPeriod.TotalSeconds);
        failedTtlIndex["partialFilterExpression"].AsBsonDocument.Should().BeEquivalentTo(new BsonDocument
        {
            { nameof(OutboxMessage.State), (Int32)OutboxMessageState.Failed },
            {
                nameof(OutboxMessage.FailedUtc), new BsonDocument
                {
                    { "$exists", true },
                    { "$type", (Int32)BsonType.DateTime }
                }
            }
        });
    }

    [OneTimeSetUp]
    public async Task GetMongoDbContainer()
        => _container = await MongoDbTestContainer.StartContainerAsync();

    [Test]
    public async Task Processor_ProcessesEligibleMessagesInPollingIndexOrder()
    {
        // Arrange
        var databaseName = $"OutboxPollingOrder_{Guid.NewGuid():N}";
        const String collectionName = "Outbox";
        await using var serviceProvider = CreateServiceProvider(databaseName);
        var helper = serviceProvider.GetRequiredService<IMongoHelper>();
        var collection = helper.Database.GetCollection<OutboxMessage>(collectionName);
        await ConfigureAsync(serviceProvider, helper);

        var now = DateTime.UtcNow;
        var lowId = ObjectId.GenerateNewId(now.AddMinutes(-5));
        var highId = ObjectId.GenerateNewId(now.AddMinutes(-4));
        await collection.InsertManyAsync(
        [
            new()
            {
                Id = highId,
                Type = "TestPayload",
                Payload = new("Name", "same-attempt-higher-id"),
                State = OutboxMessageState.Pending,
                NextAttemptUtc = now.AddMinutes(-1),
                IsLocked = false
            },
            new()
            {
                Id = lowId,
                Type = "TestPayload",
                Payload = new("Name", "same-attempt-lower-id"),
                State = OutboxMessageState.Pending,
                NextAttemptUtc = now.AddMinutes(-1),
                IsLocked = false
            },
            new()
            {
                Id = ObjectId.GenerateNewId(now.AddMinutes(-3)),
                Type = "TestPayload",
                Payload = new("Name", "earliest-attempt"),
                State = OutboxMessageState.Pending,
                NextAttemptUtc = now.AddMinutes(-2),
                IsLocked = false
            },
            new()
            {
                Id = ObjectId.GenerateNewId(now.AddMinutes(-2)),
                Type = "TestPayload",
                Payload = new("Name", "future-attempt"),
                State = OutboxMessageState.Pending,
                NextAttemptUtc = now.AddMinutes(5),
                IsLocked = false
            },
            new()
            {
                Id = ObjectId.GenerateNewId(now.AddMinutes(-1)),
                Type = "TestPayload",
                Payload = new("Name", "fresh-lock"),
                State = OutboxMessageState.Pending,
                NextAttemptUtc = now.AddMinutes(-3),
                IsLocked = true,
                LockedUtc = now
            }
        ]);

        var publisher = serviceProvider.GetRequiredService<IOutboxPublisher>() as OrderedOutboxPublisher;
        publisher.Should().NotBeNull();

        var processor = serviceProvider.GetRequiredService<IOutboxProcessor>();

        // Act
        var started = false;
        try
        {
            await processor.StartAsync();
            started = true;
            await WaitUntilAsync(() => publisher.PublishedMessages.Count >= 3);
        }
        finally
        {
            if (started)
            {
                await processor.StopAsync();
            }
        }

        // Assert
        publisher.PublishedMessages.Select(x => x.Payload["Name"].AsString).Should().Equal(
        [
            "earliest-attempt",
            "same-attempt-lower-id",
            "same-attempt-higher-id"
        ]);

        var unprocessedMessages = await collection.Find(x => x.State == OutboxMessageState.Pending).ToListAsync();
        unprocessedMessages.Select(x => x.Payload["Name"].AsString).Should().BeEquivalentTo(["future-attempt", "fresh-lock"]);
    }

    private static async Task ConfigureAsync(IServiceProvider serviceProvider, IMongoHelper helper)
    {
        foreach (var configurator in serviceProvider.GetServices<IMongoConfigurator>())
        {
            await configurator.ConfigureAsync(helper);
        }
    }

    private static async Task WaitUntilAsync(Func<Boolean> condition, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                if (condition())
                {
                    return;
                }

                await Task.Delay(50, cts.Token);
            }
        }
        catch (OperationCanceledException e) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException("The expected outbox state was not reached.", e);
        }

        throw new TimeoutException("The expected outbox state was not reached.");
    }

    private ServiceProvider CreateServiceProvider(String databaseName, Action<OutboxBuilder>? configureOutbox = null)
    {
        var services = new ServiceCollection();
        var url = MongoUrl.Create(_container.GetConnectionString());
        services.AddLogging();
        services.AddMongo(url, configure: options =>
                {
                    options.DefaultDatabase = databaseName;
                    options.RunConfiguratorsOnStartup = false;
                })
                .WithOutbox(builder =>
                {
                    builder.WithPublisher<OrderedOutboxPublisher>(ServiceLifetime.Singleton)
                           .WithMessage<TestPayload>("TestPayload")
                           .WithBatchSize(10)
                           .WithPollingInterval(TimeSpan.FromMilliseconds(100))
                           .WithLockTimeout(TimeSpan.FromSeconds(10));
                    configureOutbox?.Invoke(builder);
                });

        return services.BuildServiceProvider();
    }

    private sealed class OrderedOutboxPublisher : IOutboxPublisher
    {
        private readonly ConcurrentQueue<OutboxMessage> _publishedMessages = new();

        public IReadOnlyCollection<OutboxMessage> PublishedMessages => _publishedMessages.ToArray();

        public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken = default)
        {
            _publishedMessages.Enqueue(message);
            return Task.CompletedTask;
        }
    }
}
