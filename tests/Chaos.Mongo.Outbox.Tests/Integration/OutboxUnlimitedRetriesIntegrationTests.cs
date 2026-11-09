// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox.Tests.Integration;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Driver;
using NUnit.Framework;
using Testcontainers.MongoDb;

[Category("Integration")]
public class OutboxUnlimitedRetriesIntegrationTests
{
    // Comfortably more attempts than the old default (MaxRetries = 5) allowed before terminalizing a message.
    private const Int64 AttemptsPastOldBudget = 10;
    private MongoDbContainer _container;

    [OneTimeSetUp]
    public async Task GetMongoDbContainer()
        => _container = await MongoDbTestContainer.StartContainerAsync();

    [Test]
    public async Task Processor_DefaultMaxRetries_SurvivesOutageLongerThanOldBudgetAndThenRecovers()
    {
        // Arrange: a publisher that always throws, and an outbox configured with no WithMaxRetries(...) call,
        // so OutboxOptions.MaxRetries defaults to null (unlimited attempts). A FakeTimeProvider lets the test
        // advance well past the old 5-retries/~75-95 second exhaustion budget without actually waiting that
        // long, while still exercising the real exponential backoff schedule (ComputeNextAttemptUtc), which
        // only ever reschedules the next attempt and never terminalizes the message on its own.
        var url = MongoUrl.Create(_container.GetConnectionString());
        var databaseName = $"OutboxUnlimitedRetriesTestDb_{Guid.NewGuid():N}";
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var publisher = new TestOutboxPublisher
        {
            ShouldThrow = true
        };

        var services = new ServiceCollection()
                       .AddLogging()
                       .AddSingleton<TimeProvider>(timeProvider)
                       .AddMongo(url, configure: options =>
                       {
                           options.DefaultDatabase = databaseName;
                           options.RunConfiguratorsOnStartup = false;
                       })
                       .WithOutbox(o => o
                                        .WithPublisher<TestOutboxPublisher>(ServiceLifetime.Singleton)
                                        .WithMessage<TestPayload>("TestPayload")
                                        // Intentionally no WithMaxRetries(...) call: exercises the unlimited default.
                                        .WithPollingInterval(TimeSpan.FromMilliseconds(50))
                                        .WithLockLeaseTime(TimeSpan.FromMinutes(5))
                                        .WithBatchSize(10))
                       .Services
                       .AddSingleton<IOutboxPublisher>(publisher)
                       .BuildServiceProvider();

        await using var serviceProvider = services;
        var mongoHelper = serviceProvider.GetRequiredService<IMongoHelper>();
        var options = serviceProvider.GetRequiredService<OutboxOptions>();
        var collection = mongoHelper.Database.GetCollection<OutboxMessage>(options.CollectionName);

        using var session = await mongoHelper.Client.StartSessionAsync();
        session.StartTransaction();
        await serviceProvider.GetRequiredService<IOutbox>().AddMessageAsync(session, new TestPayload
        {
            Name = "Outage survivor"
        });
        await session.CommitTransactionAsync();

        var processor = serviceProvider.GetRequiredService<IOutboxProcessor>();

        try
        {
            await processor.StartAsync();

            // Act: drive virtual time forward in chunks well beyond the capped backoff delay, interleaved
            // with real queries and short real sleeps, so the processor's own Task.Delay/NextAttemptUtc timers
            // actually get to register and fire as time advances.
            await AdvanceUntilAsync(timeProvider,
                                    async () => (await GetMessageAsync(collection)).AttemptCount >= AttemptsPastOldBudget);

            // Assert: the message survived well past the old exhaustion budget without ever terminalizing.
            var stillRetrying = await GetMessageAsync(collection);
            stillRetrying.State.Should().Be(OutboxMessageState.Pending,
                                            "unlimited retries must never terminalize the message purely for exhausting an attempt budget");
            stillRetrying.AttemptCount.Should().BeGreaterThanOrEqualTo(AttemptsPastOldBudget);
            publisher.PublishedMessages.Should().BeEmpty("the publisher has only been made to throw so far");

            // Act: let the publisher start succeeding, and continue advancing time for the next scheduled attempt.
            publisher.ShouldThrow = false;
            await AdvanceUntilAsync(timeProvider,
                                    async () => (await GetMessageAsync(collection)).State == OutboxMessageState.Processed);
        }
        finally
        {
            await processor.StopAsync();
        }

        // Assert: it recovers once delivery starts succeeding again.
        var finalMessage = await GetMessageAsync(collection);
        finalMessage.State.Should().Be(OutboxMessageState.Processed);
        publisher.PublishedMessages.Should().ContainSingle(m => m.Payload["Name"].AsString == "Outage survivor");
    }

    private static async Task<OutboxMessage> GetMessageAsync(IMongoCollection<OutboxMessage> collection)
        => await collection.Find(FilterDefinition<OutboxMessage>.Empty).FirstAsync();

    private static async Task AdvanceUntilAsync(FakeTimeProvider timeProvider, Func<Task<Boolean>> condition, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
        while (!await condition())
        {
            if (cts.IsCancellationRequested)
            {
                throw new TimeoutException("The expected outbox state was not reached in time.");
            }

            // Yield first so a Task.Delay/timer the processor just registered against the current fake time
            // is observed before the next advance moves past it.
            await Task.Delay(20, cts.Token);
            timeProvider.Advance(TimeSpan.FromMinutes(2));
        }
    }
}
