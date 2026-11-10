// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox.Tests.Integration;

using Chaos.Mongo.Configuration;
using Chaos.Mongo.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Driver;
using NUnit.Framework;
using Testcontainers.MongoDb;

/// <summary>
/// Verifies that the outbox's processed/failed TTL retention still expires eligible messages, and leaves
/// ineligible ones in place, when a global camelCase convention pack is registered before <see cref="OutboxMessage"/>
/// is first used.
/// </summary>
[Category("Integration")]
public class OutboxRetentionCamelCaseIntegrationTests
{
    private MongoDbContainer _container;

    [OneTimeSetUp]
    public async Task GetMongoDbContainer()
        => _container = await MongoDbTestContainer.StartContainerAsync();

    [Test]
    [Explicit("Runs only via process isolation from " + nameof(Outbox_RetentionUnderCamelCaseConvention_StillFiltersAndDeletesCorrectly) +
              "; registers process-global convention and class-map state.")]
    public async Task Outbox_RetentionUnderCamelCaseConvention_IsolatedCase()
    {
        // Arrange: register a global camelCase convention for OutboxMessage before anything in this fresh
        // process has used it, then exercise the library's normal AddMongo/WithOutbox registration path. The
        // container's TTL monitor is disabled entirely until after the filter-selection assertions below, so
        // the seeded, already-eligible messages cannot be deleted by a background sweep racing those
        // assertions: a merely slower (default 60-second) interval does not disable the sweep, it only narrows
        // the window.
        ConventionRegistry.Register(
            nameof(Outbox_RetentionUnderCamelCaseConvention_IsolatedCase),
            new ConventionPack
            {
                new CamelCaseElementNameConvention()
            },
            type => type == typeof(OutboxMessage));

        var container = await MongoDbTestContainer.StartContainerAsync();
        var adminClient = new MongoClient(container.GetConnectionString());
        var adminDatabase = adminClient.GetDatabase("admin");
        await adminDatabase.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "setParameter", 1 },
            { "ttlMonitorEnabled", false }
        });

        var databaseName = $"OutboxCamelCaseRetentionTest_{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        var url = MongoUrl.Create(container.GetConnectionString());
        services.AddMongo(url, configure: options =>
                {
                    options.DefaultDatabase = databaseName;
                    options.RunConfiguratorsOnStartup = false;
                })
                .WithOutbox(builder => builder.WithMessage<CamelCaseRetentionPayload>("CamelCaseRetentionPayload")
                                              .WithRetentionPeriod(TimeSpan.FromSeconds(2))
                                              .WithExpireFailedMessages()
                                              .WithPublisher<PassiveOutboxPublisher>());

        await using var serviceProvider = services.BuildServiceProvider();
        var helper = serviceProvider.GetRequiredService<IMongoHelper>();
        foreach (var configurator in serviceProvider.GetServices<IMongoConfigurator>())
        {
            await configurator.ConfigureAsync(helper);
        }

        var collection = helper.Database.GetCollection<OutboxMessage>(OutboxOptions.DefaultCollectionName);
        var now = DateTime.UtcNow;

        // Seed a processed message and a failed message, both eligible for TTL cleanup, and a pending message
        // that must never expire, all through the typed collection, so the driver serializes them with whichever
        // element names the camelCase convention actually produced, exactly as a host application would write
        // them.
        var processedMessageId = ObjectId.GenerateNewId();
        var failedMessageId = ObjectId.GenerateNewId();
        var pendingMessageId = ObjectId.GenerateNewId();
        await collection.InsertOneAsync(new OutboxMessage
        {
            Id = processedMessageId,
            Type = "CamelCaseRetentionPayload",
            Payload = new BsonDocument("Name", "processed-message"),
            State = OutboxMessageState.Processed,
            ProcessedUtc = now.AddMinutes(-10)
        });
        await collection.InsertOneAsync(new OutboxMessage
        {
            Id = failedMessageId,
            Type = "CamelCaseRetentionPayload",
            Payload = new BsonDocument("Name", "failed-message"),
            State = OutboxMessageState.Failed,
            FailedUtc = now.AddMinutes(-10),
            Error = "simulated failure"
        });
        await collection.InsertOneAsync(new OutboxMessage
        {
            Id = pendingMessageId,
            Type = "CamelCaseRetentionPayload",
            Payload = new BsonDocument("Name", "pending-message"),
            State = OutboxMessageState.Pending
        });

        // Act & Assert (filter selection): execute the real, server-stored partialFilterExpression of each TTL
        // index exactly as created by the outbox configurator under the camelCase convention, proving selection
        // before relying on the asynchronous TTL monitor below.
        var rawCollection = helper.Database.GetCollection<BsonDocument>(OutboxOptions.DefaultCollectionName);
        var indexes = await (await rawCollection.Indexes.ListAsync()).ToListAsync();
        var processedTtlFilter = indexes.Single(x => x["name"] == "IX_Outbox_ProcessedUtc_TTL")["partialFilterExpression"].AsBsonDocument;
        var failedTtlFilter = indexes.Single(x => x["name"] == "IX_Outbox_FailedUtc_TTL")["partialFilterExpression"].AsBsonDocument;

        var processedMatches = await rawCollection.Find(new BsonDocumentFilterDefinition<BsonDocument>(processedTtlFilter)).ToListAsync();
        processedMatches.Select(x => x["_id"].AsObjectId).Should().BeEquivalentTo([processedMessageId]);

        var failedMatches = await rawCollection.Find(new BsonDocumentFilterDefinition<BsonDocument>(failedTtlFilter)).ToListAsync();
        failedMatches.Select(x => x["_id"].AsObjectId).Should().BeEquivalentTo([failedMessageId]);

        // Act & Assert (actual expiry): only now re-enable the TTL monitor, with a short sleep interval, so the
        // eventual deletion assertion below stays within a bounded wait without having been able to race the
        // selection assertions above, which ran while the monitor was fully disabled. The processed and failed
        // messages must eventually be deleted by TTL, while the pending message, seeded under the camelCase
        // convention's own element names, must remain untouched.
        await adminDatabase.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "setParameter", 1 },
            { "ttlMonitorSleepSecs", 1 }
        });
        await adminDatabase.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "setParameter", 1 },
            { "ttlMonitorEnabled", true }
        });
        await WaitUntilAsync(
            async () => await collection.Find(x => x.State == OutboxMessageState.Processed || x.State == OutboxMessageState.Failed)
                                        .CountDocumentsAsync() == 0,
            TimeSpan.FromSeconds(30));
        (await collection.Find(x => x.State == OutboxMessageState.Pending).CountDocumentsAsync()).Should().Be(1);
    }

    [Test]
    public async Task Outbox_RetentionUnderCamelCaseConvention_StillFiltersAndDeletesCorrectly()
    {
        // Arrange & Act: the inner case runs in its own freshly started process (see
        // IsolatedConventionTestProcess), because BsonClassMap/ConventionRegistry registration is process-global
        // and irreversible, and every other test in this shared process may already have registered
        // OutboxMessage's class map under the default convention.
        var result = IsolatedConventionTestProcess.Run(
            $"{typeof(OutboxRetentionCamelCaseIntegrationTests).FullName}.{nameof(Outbox_RetentionUnderCamelCaseConvention_IsolatedCase)}");

        // Assert
        result.ExitCode.Should().Be(0, "the isolated camelCase case must pass; output:\n{0}\n{1}", result.StandardOutput, result.StandardError);
    }

    private static async Task WaitUntilAsync(Func<Task<Boolean>> condition, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                if (await condition())
                {
                    return;
                }

                await Task.Delay(100, cts.Token);
            }
        }
        catch (OperationCanceledException e) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException("The expected outbox state was not reached.", e);
        }

        throw new TimeoutException("The expected outbox state was not reached.");
    }

    private sealed class CamelCaseRetentionPayload;

    private sealed class PassiveOutboxPublisher : IOutboxPublisher
    {
        public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
