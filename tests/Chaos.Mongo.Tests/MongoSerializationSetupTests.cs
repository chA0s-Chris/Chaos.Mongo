// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests;

using Chaos.Mongo.Queues;
using Chaos.Mongo.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using NUnit.Framework;

/// <summary>
/// Class maps are process-global, so hosts built in parallel register the library's class maps concurrently.
/// </summary>
public class MongoSerializationSetupTests
{
    private const Int32 Hosts = 16;

    [Test]
    [Explicit("Runs only via process isolation from " + nameof(AddMongo_ConcurrentHosts_RegisterEveryClassMapOnce) +
              "; registers process-global class-map state.")]
    public void AddMongo_ConcurrentHosts_IsolatedCase()
    {
        // Act
        var act = () => SimultaneousCalls.Run(Hosts, () => new ServiceCollection().AddMongo()
                                                                                  .WithQueue<ConcurrentRegistrationPayload>(queue => queue
                                                                                          .WithPayloadHandler<PassivePayloadHandler>()));

        // Assert
        act.Should().NotThrow();
        new MongoLockDocument
        {
            Id = "lock-name",
            Holder = "holder-id"
        }.ToBsonDocument().Names.Should().Contain(nameof(MongoLockDocument.Holder));
        new MongoQueueItem<ConcurrentRegistrationPayload>
        {
            Id = ObjectId.GenerateNewId(),
            CreatedUtc = DateTime.UtcNow,
            Payload = new ConcurrentRegistrationPayload()
        }.ToBsonDocument().Names.Should().Contain([nameof(MongoQueueItem.CreatedUtc), nameof(MongoQueueItem<ConcurrentRegistrationPayload>.Payload)]);
    }

    [Test]
    public void AddMongo_ConcurrentHosts_RegisterEveryClassMapOnce()
    {
        // Arrange & Act: the inner case runs in its own freshly started process (see IsolatedConventionTestProcess),
        // because every other test in this shared process may already have registered the library's class maps.
        var result = IsolatedConventionTestProcess.Run(
            $"{typeof(MongoSerializationSetupTests).FullName}.{nameof(AddMongo_ConcurrentHosts_IsolatedCase)}");

        // Assert
        result.ExitCode.Should().Be(0, "the isolated concurrent case must pass; output:\n{0}\n{1}", result.StandardOutput, result.StandardError);
    }

    public sealed class ConcurrentRegistrationPayload;

    private sealed class PassivePayloadHandler : IMongoQueuePayloadHandler<ConcurrentRegistrationPayload>
    {
        public Task HandlePayloadAsync(ConcurrentRegistrationPayload payload, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
