// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox.Tests;

using Chaos.Mongo.Outbox.Tests.Markers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Driver;
using NUnit.Framework;
using System.Reflection;
using FirstSharedMarkerName = Chaos.Mongo.Outbox.Tests.Markers.FirstNamespace.SharedMarkerName;
using SecondSharedMarkerName = Chaos.Mongo.Outbox.Tests.Markers.SecondNamespace.SharedMarkerName;

public class OutboxMetricsRegistrationTests
{
    [Test]
    public async Task StartAsync_OutboxesWithEqualIdentities_EmitUnderTheirOwnCollectionDestination()
    {
        var prefix = $"metrics-collision-{Guid.NewGuid():N}-";
        var database = DispatchProxy.Create<IMongoDatabase, MetricsOutboxDatabase>();
        // DispatchProxy.Create returns an instance of the proxy type, so the cast always succeeds.
        // ReSharper disable once SuspiciousTypeConversion.Global
        var databaseProxy = (MetricsOutboxDatabase)database;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMongoHelper>(new ProcessingFilterMongoHelper(database));
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero)));
        var builder = new MongoBuilder(services);
        builder.WithOutbox<FirstSharedMarkerName>(o => Configure(o, prefix + "first"))
               .WithOutbox<SecondSharedMarkerName>(o => Configure(o, prefix + "second"))
               .WithOutbox(o => Configure(o, prefix + "untyped"))
               .WithOutbox<Default>(o => Configure(o, prefix + "named-default"));
        await using var provider = services.BuildServiceProvider();
        // The outboxes are only distinguishable by collection, because registration derives equal identities for them.
        provider.GetServices<OutboxRegistration>().Select(r => r.Options.Identity).Should().Equal("SharedMarkerName", "SharedMarkerName", "Default", "Default");
        var processors = new[]
        {
            provider.GetRequiredService<IOutboxProcessor<FirstSharedMarkerName>>(),
            provider.GetRequiredService<IOutboxProcessor<SecondSharedMarkerName>>(),
            provider.GetRequiredService<IOutboxProcessor>(),
            provider.GetRequiredService<IOutboxProcessor<Default>>()
        };
        var destinations = new[]
        {
            prefix + "first",
            prefix + "second",
            prefix + "untyped",
            prefix + "named-default"
        };
        using var metrics = new OutboxMetricsCollector();

        try
        {
            foreach (var processor in processors)
            {
                await processor.StartAsync();
            }

            await Task.WhenAll(destinations.Select(d => databaseProxy.Collection(d).UpdatesReached.Task)).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            foreach (var processor in processors)
            {
                await processor.StopAsync();
            }
        }

        metrics.ForDestinationPrefix(prefix).Select(m => m.Tags["outbox.destination"]).Distinct().Should().BeEquivalentTo(destinations);
        foreach (var destination in destinations)
        {
            metrics.For(destination).Select(m => m.InstrumentName).Should().BeEquivalentTo(
                "chaos.mongo.outbox.published",
                "chaos.mongo.outbox.publish.duration",
                "chaos.mongo.outbox.message.age");
            metrics.For(destination).Single(m => m.InstrumentName == "chaos.mongo.outbox.published").Value.Should().Be(1);
        }
    }

    private static void Configure(OutboxBuilder builder, String collectionName)
        => builder.WithPublisher<TestOutboxPublisher>(ServiceLifetime.Singleton)
                  .WithMessage<TestPayload>()
                  .WithCollectionName(collectionName)
                  .WithPollingInterval(TimeSpan.FromHours(1));
}
