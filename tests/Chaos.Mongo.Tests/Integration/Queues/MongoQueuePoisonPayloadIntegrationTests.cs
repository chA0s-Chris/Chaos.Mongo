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
using Testcontainers.MongoDb;

[Category("Integration")]
public class MongoQueuePoisonPayloadIntegrationTests
{
    private MongoDbContainer _container;

    [OneTimeSetUp]
    public async Task GetMongoDbContainer()
        => _container = await MongoDbTestContainer.StartContainerAsync();

    [Test]
    public async Task QueueHandlerFailure_UndeserializablePayload_TerminalizesWithoutInvokingHandler()
    {
        // Arrange: a stored payload shape that can never deserialize into PoisonPayload. The acquire's own
        // projection never reads Payload, so this cannot fail the acquire itself; only the secondary typed read
        // fails, which is handled exactly like a handler exception. MaxRetries = 1 allows two total attempts.
        var leaseTime = TimeSpan.FromMilliseconds(750);
        const String collectionName = "poison-queue";
        var handler = new NeverInvokedPayloadHandler();
        var services = new ServiceCollection();
        services.AddNUnitTestLogging();

        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"QueuePoisonPayloadTest_{Guid.NewGuid():N}";
        services.AddMongo(url, uniqueDbName)
                .WithQueue<PoisonPayload>(queue => queue
                                                   .WithPayloadHandler(_ => handler)
                                                   .WithCollectionName(collectionName)
                                                   .WithLockLeaseTime(leaseTime)
                                                   .WithMaxRetries(1)
                                                   .WithoutAutoStartSubscription());

        await using var serviceProvider = services.BuildServiceProvider();
        var helper = serviceProvider.GetRequiredService<IMongoHelper>();
        var bsonCollection = helper.Database.GetCollection<BsonDocument>(collectionName);
        var queue = serviceProvider.GetRequiredService<IMongoQueue<PoisonPayload>>();

        var queueItemId = ObjectId.GenerateNewId();
        var poisonDocument = new BsonDocument
        {
            { "_id", queueItemId },
            { "CreatedUtc", DateTime.UtcNow },
            { "IsClosed", false },
            { "IsLocked", false },
            { "IsTerminal", false },
            // "Value" is a nested document, not the Int32 PoisonPayload.Value expects: this can never
            // deserialize into PoisonPayload, even though it is perfectly valid BSON.
            { "Payload", new BsonDocument("Value", new BsonDocument("Nested", 1)) },
            { "PayloadType", typeof(PoisonPayload).FullName }
        };

        await queue.StartSubscriptionAsync();

        try
        {
            // Act
            await bsonCollection.InsertOneAsync(poisonDocument);
            var terminalDocument = await WaitForTerminalDocumentAsync(bsonCollection, queueItemId, TimeSpan.FromSeconds(10));

            // Assert: exactly MaxRetries + 1 (2) acquire attempts occurred, the handler was never invoked (the
            // undeserializable payload never reaches it), and the item still reached the terminal state, asserted
            // through a BsonDocument query because the typed collection cannot deserialize it even after it turned
            // terminal.
            handler.Invocations.Should().Be(0, "a permanently undeserializable payload must never reach the handler");
            terminalDocument["AttemptCount"].ToInt64().Should().Be(2);
            terminalDocument["IsClosed"].AsBoolean.Should().BeTrue();
            terminalDocument["IsTerminal"].AsBoolean.Should().BeTrue();
            terminalDocument["IsLocked"].AsBoolean.Should().BeFalse();
            terminalDocument.Contains("LockedUtc").Should().BeFalse();
        }
        finally
        {
            await queue.StopSubscriptionAsync();
        }
    }

    private static async Task<BsonDocument> WaitForTerminalDocumentAsync(IMongoCollection<BsonDocument> collection, ObjectId id, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var document = await collection.Find(Builders<BsonDocument>.Filter.Eq("_id", id)).FirstOrDefaultAsync(cts.Token);
                if (document is not null &&
                    document.GetValue("IsTerminal", false).AsBoolean &&
                    document.GetValue("IsClosed", false).AsBoolean)
                {
                    return document;
                }

                await Task.Delay(100, cts.Token);
            }
        }
        catch (OperationCanceledException e) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException("Queue item did not reach the expected terminal state.", e);
        }

        throw new TimeoutException("Queue item did not reach the expected terminal state.");
    }

    private sealed class NeverInvokedPayloadHandler : IMongoQueuePayloadHandler<PoisonPayload>
    {
        public Int32 Invocations { get; private set; }

        public Task HandlePayloadAsync(PoisonPayload payload, CancellationToken cancellationToken = default)
        {
            Invocations++;
            return Task.CompletedTask;
        }
    }

    private sealed class PoisonPayload
    {
        // Only defines the expected deserialization shape; the stored "Value" is always a mismatched nested
        // document, deliberately making every acquire's secondary typed read fail.
        // ReSharper disable once UnusedMember.Local
        public Int32 Value { get; set; }
    }
}
