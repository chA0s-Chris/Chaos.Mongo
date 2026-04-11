// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests.Integration.Queues;

using Chaos.Mongo.Queues;
using Chaos.Testing.Logging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using NUnit.Framework;
using System.Linq.Expressions;
using Testcontainers.MongoDb;

public class MongoQueueRetryIntegrationTests
{
    private MongoDbContainer _container;

    [OneTimeSetUp]
    public async Task GetMongoDbContainer()
        => _container = await MongoDbTestContainer.StartContainerAsync();

    [Test]
    public async Task QueueHandlerFailure_WithMaxRetries_MarksItemTerminalAfterRetryBudgetIsExhausted()
    {
        // Arrange
        var leaseTime = TimeSpan.FromMilliseconds(750);
        var handler = new AlwaysFailingPayloadHandler();
        var services = new ServiceCollection();
        services.AddNUnitTestLogging();

        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"QueueRetryTerminalTest_{Guid.NewGuid():N}";
        services.AddMongo(url, uniqueDbName)
                .WithQueue<RetryPayload>(queue => queue
                                                  .WithPayloadHandler(_ => handler)
                                                  .WithCollectionName("retry-queue")
                                                  .WithLockLeaseTime(leaseTime)
                                                  .WithMaxRetries(1)
                                                  .WithoutAutoStartSubscription());

        await using var serviceProvider = services.BuildServiceProvider();
        var helper = serviceProvider.GetRequiredService<IMongoHelper>();
        var collection = helper.Database.GetCollection<MongoQueueItem<RetryPayload>>("retry-queue");
        var queue = serviceProvider.GetRequiredService<IMongoQueue<RetryPayload>>();
        await queue.StartSubscriptionAsync();

        try
        {
            // Act
            await queue.PublishAsync(new()
            {
                Value = "poison-message"
            });

            await handler.WaitForAttempts(2, TimeSpan.FromSeconds(10));
            var terminalItem = await WaitForQueueItemAsync(collection,
                                                           x => x.Payload.Value == "poison-message" && x.IsClosed && x.IsTerminal,
                                                           TimeSpan.FromSeconds(10));

            // Assert
            handler.Attempts.Should().Be(2);
            terminalItem.RetryCount.Should().Be(2);
            terminalItem.IsClosed.Should().BeTrue();
            terminalItem.IsTerminal.Should().BeTrue();
            terminalItem.IsLocked.Should().BeFalse();
            terminalItem.LockedUtc.Should().BeNull();
            terminalItem.ClosedUtc.Should().NotBeNull();
        }
        finally
        {
            await queue.StopSubscriptionAsync();
        }
    }

    [Test]
    public async Task QueueHandlerFailure_WithNoRetry_MarksItemTerminalAfterFirstFailure()
    {
        // Arrange
        var leaseTime = TimeSpan.FromMilliseconds(750);
        var handler = new AlwaysFailingPayloadHandler();
        var services = new ServiceCollection();
        services.AddNUnitTestLogging();

        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"QueueNoRetryTerminalTest_{Guid.NewGuid():N}";
        services.AddMongo(url, uniqueDbName)
                .WithQueue<RetryPayload>(queue => queue
                                                  .WithPayloadHandler(_ => handler)
                                                  .WithCollectionName("retry-queue")
                                                  .WithLockLeaseTime(leaseTime)
                                                  .WithNoRetry()
                                                  .WithoutAutoStartSubscription());

        await using var serviceProvider = services.BuildServiceProvider();
        var helper = serviceProvider.GetRequiredService<IMongoHelper>();
        var collection = helper.Database.GetCollection<MongoQueueItem<RetryPayload>>("retry-queue");
        var queue = serviceProvider.GetRequiredService<IMongoQueue<RetryPayload>>();
        await queue.StartSubscriptionAsync();

        try
        {
            // Act
            await queue.PublishAsync(new()
            {
                Value = "single-attempt"
            });

            await handler.WaitForAttempts(1, TimeSpan.FromSeconds(10));
            var terminalItem = await WaitForQueueItemAsync(collection,
                                                           x => x.Payload.Value == "single-attempt" && x.IsClosed && x.IsTerminal,
                                                           TimeSpan.FromSeconds(10));
            await Task.Delay(leaseTime + TimeSpan.FromMilliseconds(500));

            // Assert
            handler.Attempts.Should().Be(1);
            terminalItem.RetryCount.Should().Be(1);
            terminalItem.IsClosed.Should().BeTrue();
            terminalItem.IsTerminal.Should().BeTrue();
        }
        finally
        {
            await queue.StopSubscriptionAsync();
        }
    }

    private static async Task<MongoQueueItem<RetryPayload>> WaitForQueueItemAsync(
        IMongoCollection<MongoQueueItem<RetryPayload>> collection,
        Expression<Func<MongoQueueItem<RetryPayload>, Boolean>> filter,
        TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var queueItem = await collection.Find(filter).FirstOrDefaultAsync(cts.Token);
                if (queueItem is not null)
                {
                    return queueItem;
                }

                await Task.Delay(100, cts.Token);
            }
        }
        catch (OperationCanceledException e) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException("Queue item did not reach the expected retry state.", e);
        }

        throw new TimeoutException("Queue item did not reach the expected retry state.");
    }

    private sealed class AlwaysFailingPayloadHandler : IMongoQueuePayloadHandler<RetryPayload>
    {
        private readonly SemaphoreSlim _attemptSemaphore = new(0);

        public Int32 Attempts { get; private set; }

        public async Task WaitForAttempts(Int32 count, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            for (var i = 0; i < count; i++)
            {
                await _attemptSemaphore.WaitAsync(cts.Token);
            }
        }

        public Task HandlePayloadAsync(RetryPayload payload, CancellationToken cancellationToken = default)
        {
            Attempts++;
            _attemptSemaphore.Release();
            throw new InvalidOperationException("Simulated permanent failure");
        }
    }

    private sealed class RetryPayload
    {
        public String Value { get; init; } = String.Empty;
    }
}
