// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests;

using Chaos.Mongo.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using NUnit.Framework;

/// <summary>
/// Class maps are process-global, so hosts built in parallel register the event store's class maps concurrently.
/// </summary>
public class ConcurrentRegistrationTests
{
    private const Int32 Hosts = 16;

    [Test]
    [Explicit("Runs only via process isolation from " + nameof(WithEventStore_ConcurrentHosts_RegisterEveryClassMapOnce) +
              "; registers process-global class-map state.")]
    public void WithEventStore_ConcurrentHosts_IsolatedCase()
    {
        // Act: in a fresh process, none of the class maps shared by every aggregate is registered yet either.
        var act = () => SimultaneousCalls.Run(Hosts, () => new MongoBuilder(new ServiceCollection())
                                                  .WithEventStore<ConcurrentRegistrationAggregate>(es => es.WithEvent<ConcurrentRegistrationFirstEvent>("First")
                                                                                                           .WithEvent<ConcurrentRegistrationSecondEvent>("Second")));

        // Assert
        act.Should().NotThrow();
        var document = new ConcurrentRegistrationSecondEvent().ToBsonDocument<Event<ConcurrentRegistrationAggregate>>();
        BsonSerializer.Deserialize<Event<ConcurrentRegistrationAggregate>>(document).Should().BeOfType<ConcurrentRegistrationSecondEvent>();
    }

    [Test]
    public void WithEventStore_ConcurrentHosts_RegisterEveryClassMapOnce()
    {
        // Arrange & Act: the inner case runs in its own freshly started process (see IsolatedConventionTestProcess),
        // because every other test in this shared process may already have registered the shared class maps.
        var result = IsolatedConventionTestProcess.Run(
            $"{typeof(ConcurrentRegistrationTests).FullName}.{nameof(WithEventStore_ConcurrentHosts_IsolatedCase)}");

        // Assert
        result.ExitCode.Should().Be(0, "the isolated concurrent case must pass; output:\n{0}\n{1}", result.StandardOutput, result.StandardError);
    }
}

public sealed class ConcurrentRegistrationAggregate : Aggregate;

public abstract class ConcurrentRegistrationBaseEvent : Event<ConcurrentRegistrationAggregate>
{
    public String? Memo { get; set; }
}

public sealed class ConcurrentRegistrationFirstEvent : ConcurrentRegistrationBaseEvent
{
    public override void Execute(ConcurrentRegistrationAggregate aggregate) { }
}

public sealed class ConcurrentRegistrationSecondEvent : ConcurrentRegistrationBaseEvent
{
    public override void Execute(ConcurrentRegistrationAggregate aggregate) { }
}
