// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox.Tests.Integration;

using Chaos.Mongo.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using NUnit.Framework;
using System.Diagnostics;
using Testcontainers.MongoDb;

[Category("Integration")]
public class OutboxMetricsIntegrationTests
{
    private MongoDbContainer _container;

    [OneTimeSetUp]
    public async Task GetMongoDbContainer()
        => _container = await MongoDbTestContainer.StartContainerAsync();

    [TestCase(false)]
    [TestCase(true)]
    public async Task HostedProcessor_SuccessfulPublish_RecordsPublishedDurationAndAgeForTheCollection(Boolean typed)
    {
        using var metrics = new OutboxMetricsCollector();
        var destination = $"metrics-published-{Guid.NewGuid():N}";
        using var host = CreateHost(destination, typed, new TestOutboxPublisher());

        await host.StartAsync();
        try
        {
            await EnqueueAsync(host.Services, typed);
            await WaitUntilAsync(async () => (await GetMessageAsync(host.Services, destination)).State == OutboxMessageState.Processed);
        }
        finally
        {
            await host.StopAsync();
        }

        var measurements = metrics.For(destination);
        measurements.Select(m => m.InstrumentName).Should().BeEquivalentTo(
            "chaos.mongo.outbox.published",
            "chaos.mongo.outbox.publish.duration",
            "chaos.mongo.outbox.message.age");
        measurements.Should().OnlyContain(m => m.Tags.Count == 1 && m.Tags["outbox.destination"] == destination);
        measurements.Single(m => m.InstrumentName == "chaos.mongo.outbox.published").Value.Should().Be(1);
        var duration = measurements.Single(m => m.InstrumentName == "chaos.mongo.outbox.publish.duration");
        duration.Unit.Should().Be("s");
        duration.Value.Should().BeGreaterThanOrEqualTo(0);
        var age = measurements.Single(m => m.InstrumentName == "chaos.mongo.outbox.message.age");
        age.Unit.Should().Be("s");
        age.Value.Should().BeGreaterThanOrEqualTo(0);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task HostedProcessor_PermanentPublishFailure_RecordsFailedAndTerminalFailedForTheCollection(Boolean typed)
    {
        using var metrics = new OutboxMetricsCollector();
        var destination = $"metrics-terminal-{Guid.NewGuid():N}";
        using var host = CreateHost(destination, typed, new TestOutboxPublisher
        {
            ShouldThrow = true
        }, 0);

        await host.StartAsync();
        try
        {
            await EnqueueAsync(host.Services, typed);
            await WaitUntilAsync(async () => (await GetMessageAsync(host.Services, destination)).State == OutboxMessageState.Failed);
        }
        finally
        {
            await host.StopAsync();
        }

        // MaxRetries 0 allows exactly one attempt, so the first failed publish is also the terminal one.
        var measurements = metrics.For(destination);
        measurements.Select(m => m.InstrumentName).Should().BeEquivalentTo(
            "chaos.mongo.outbox.publish.failed",
            "chaos.mongo.outbox.publish.terminal_failed");
        measurements.Should().OnlyContain(m => m.Unit == null && m.Tags["outbox.destination"] == destination);
        measurements.Should().AllSatisfy(m => m.Value.Should().Be(1));
    }

    private IHost CreateHost(String destination, Boolean typed, IOutboxPublisher publisher, Int32? maxRetries = null)
        => new HostBuilder().ConfigureServices(services =>
        {
            var mongoBuilder = services.AddMongo(_container.GetConnectionString(), TestDatabases.NewName("OutboxMetrics"), o =>
            {
                o.RunConfiguratorsOnStartup = false;
                o.ApplyMigrationsOnStartup = false;
            });

            void Configure(OutboxBuilder builder)
            {
                builder.WithCollectionName(destination)
                       .WithMessage<TestPayload>()
                       .WithPublisher(_ => publisher, ServiceLifetime.Singleton)
                       .WithPollingInterval(TimeSpan.FromMilliseconds(50))
                       .WithAutoStartProcessor();
                if (maxRetries.HasValue)
                    builder.WithMaxRetries(maxRetries.Value);
            }

            if (typed)
                mongoBuilder.WithOutbox<FirstDestination>(Configure);
            else
                mongoBuilder.WithOutbox(Configure);
        }).Build();

    private static async Task EnqueueAsync(IServiceProvider provider, Boolean typed)
    {
        using var session = await provider.GetRequiredService<IMongoHelper>().Client.StartSessionAsync();
        session.StartTransaction();
        if (typed)
            await provider.GetRequiredService<IOutbox<FirstDestination>>().AddMessageAsync(session, new TestPayload());
        else
            await provider.GetRequiredService<IOutbox>().AddMessageAsync(session, new TestPayload());
        await session.CommitTransactionAsync();
    }

    private static async Task<OutboxMessage> GetMessageAsync(IServiceProvider provider, String destination)
        => await provider.GetRequiredService<IMongoHelper>().Database.GetCollection<OutboxMessage>(destination)
                         .Find(FilterDefinition<OutboxMessage>.Empty).SingleAsync();

    private static async Task WaitUntilAsync(Func<Task<Boolean>> condition)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (await condition())
                return;
            await Task.Delay(20);
        }

        throw new TimeoutException("Outbox state did not converge within fifteen seconds.");
    }
}
