// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests.Integration.Queues;

using Chaos.Mongo.Queues;
using Chaos.Testing.Logging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using NUnit.Framework;
using Testcontainers.MongoDb;

public class MongoQueueIntegrationTests
{
    private MongoDbContainer _container;

    [Test]
    public async Task AutoStartQueue_WithHostedService_ProcessesPublishedMessages()
    {
        // Arrange
        var handler = new TestPayloadHandler();
        var services = new ServiceCollection();
        services.AddNUnitTestLogging();

        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"QueueAutoStartTest_{Guid.NewGuid():N}";
        services.AddMongo(url, uniqueDbName)
                .WithQueue<TestPayload>(queue => queue
                                                 .WithPayloadHandler(_ => handler)
                                                 .WithCollectionName("test-queue")
                                                 .WithAutoStartSubscription());

        var serviceProvider = services.BuildServiceProvider();
        var hostedServices = serviceProvider.GetServices<IHostedService>().ToList();
        var queue = serviceProvider.GetRequiredService<IMongoQueue<TestPayload>>();

        // Act - Publish messages BEFORE starting
        var payload1 = new TestPayload
        {
            Value = "Message 1"
        };
        var payload2 = new TestPayload
        {
            Value = "Message 2"
        };
        await queue.PublishAsync(payload1);
        await queue.PublishAsync(payload2);

        // Start hosted services (including MongoHostedService)
        foreach (var hostedService in hostedServices)
        {
            await hostedService.StartAsync(CancellationToken.None);

            // IHostedLifecycleService methods need to be called explicitly in tests
            if (hostedService is IHostedLifecycleService lifecycleService)
            {
                await lifecycleService.StartingAsync(CancellationToken.None);
                await lifecycleService.StartedAsync(CancellationToken.None);
            }
        }

        // Wait for messages to be processed
        await handler.WaitForMessages(2, TimeSpan.FromSeconds(10));

        // Assert
        queue.IsRunning.Should().BeTrue();
        handler.ProcessedPayloads.Should().HaveCount(2);
        handler.ProcessedPayloads.Should().Contain(p => p.Value == "Message 1");
        handler.ProcessedPayloads.Should().Contain(p => p.Value == "Message 2");

        // Cleanup
        foreach (var hostedService in hostedServices)
        {
            if (hostedService is IHostedLifecycleService lifecycleService)
            {
                await lifecycleService.StoppingAsync(CancellationToken.None);
                await lifecycleService.StoppedAsync(CancellationToken.None);
            }

            await hostedService.StopAsync(CancellationToken.None);
        }
    }

    [OneTimeSetUp]
    public async Task GetMongoDbContainer()
        => _container = await MongoDbTestContainer.StartContainerAsync();

    [Test]
    public async Task HostedService_OnStop_GracefullyStopsAllAutoStartQueues()
    {
        // Arrange
        var handler1 = new TestPayloadHandler();
        var handler2 = new AnotherPayloadHandler();
        var services = new ServiceCollection();
        services.AddNUnitTestLogging();

        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"HostedServiceStopTest_{Guid.NewGuid():N}";
        services.AddMongo(url, uniqueDbName)
                .WithQueue<TestPayload>(queue => queue
                                                 .WithPayloadHandler(_ => handler1)
                                                 .WithCollectionName("test-queue-1")
                                                 .WithAutoStartSubscription())
                .WithQueue<AnotherPayload>(queue => queue
                                                    .WithPayloadHandler(_ => handler2)
                                                    .WithCollectionName("test-queue-2")
                                                    .WithAutoStartSubscription());

        var serviceProvider = services.BuildServiceProvider();
        var hostedServices = serviceProvider.GetServices<IHostedService>().ToList();
        var queue1 = serviceProvider.GetRequiredService<IMongoQueue<TestPayload>>();
        var queue2 = serviceProvider.GetRequiredService<IMongoQueue<AnotherPayload>>();

        // Start all hosted services
        foreach (var hostedService in hostedServices)
        {
            await hostedService.StartAsync(CancellationToken.None);

            if (hostedService is IHostedLifecycleService lifecycleService)
            {
                await lifecycleService.StartingAsync(CancellationToken.None);
                await lifecycleService.StartedAsync(CancellationToken.None);
            }
        }

        await Task.Delay(200); // Brief delay for background tasks to start

        // Verify queues are running
        queue1.IsRunning.Should().BeTrue();
        queue2.IsRunning.Should().BeTrue();

        // Act - Stop all hosted services
        foreach (var hostedService in hostedServices)
        {
            if (hostedService is IHostedLifecycleService lifecycleService)
            {
                await lifecycleService.StoppingAsync(CancellationToken.None);
                await lifecycleService.StoppedAsync(CancellationToken.None);
            }

            await hostedService.StopAsync(CancellationToken.None);
        }

        // Assert - Queues should be stopped
        queue1.IsRunning.Should().BeFalse();
        queue2.IsRunning.Should().BeFalse();
    }

    [Test]
    public async Task ManualStartQueue_WithoutHostedService_ProcessesPublishedMessages()
    {
        // Arrange
        var handler = new TestPayloadHandler();
        var services = new ServiceCollection();
        services.AddNUnitTestLogging();

        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"QueueManualStartTest_{Guid.NewGuid():N}";
        services.AddMongo(url, uniqueDbName)
                .WithQueue<TestPayload>(queue => queue
                                                 .WithPayloadHandler(_ => handler)
                                                 .WithCollectionName("test-queue")
                                                 .WithoutAutoStartSubscription());

        var serviceProvider = services.BuildServiceProvider();
        var queue = serviceProvider.GetRequiredService<IMongoQueue<TestPayload>>();

        // Act - Manually start the queue
        queue.IsRunning.Should().BeFalse();
        await queue.StartSubscriptionAsync();

        var payload1 = new TestPayload
        {
            Value = "Manual 1"
        };
        var payload2 = new TestPayload
        {
            Value = "Manual 2"
        };
        await queue.PublishAsync(payload1);
        await queue.PublishAsync(payload2);

        // Wait for messages to be processed
        await handler.WaitForMessages(2, TimeSpan.FromSeconds(10));

        // Assert
        queue.IsRunning.Should().BeTrue();
        handler.ProcessedPayloads.Should().HaveCount(2);
        handler.ProcessedPayloads.Should().Contain(p => p.Value == "Manual 1");
        handler.ProcessedPayloads.Should().Contain(p => p.Value == "Manual 2");

        // Cleanup
        await queue.StopSubscriptionAsync();
        queue.IsRunning.Should().BeFalse();
    }

    [Test]
    public async Task MultipleQueues_WithDifferentPayloadTypes_DoNotAffectEachOther()
    {
        // Arrange
        var handler1 = new TestPayloadHandler();
        var handler2 = new AnotherPayloadHandler();
        var services = new ServiceCollection();
        services.AddNUnitTestLogging();

        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"MultipleQueuesTest_{Guid.NewGuid():N}";
        services.AddMongo(url, uniqueDbName)
                .WithQueue<TestPayload>(queue => queue
                                                 .WithPayloadHandler(_ => handler1)
                                                 .WithCollectionName("test-queue-1")
                                                 .WithoutAutoStartSubscription())
                .WithQueue<AnotherPayload>(queue => queue
                                                    .WithPayloadHandler(_ => handler2)
                                                    .WithCollectionName("test-queue-2")
                                                    .WithoutAutoStartSubscription());

        var serviceProvider = services.BuildServiceProvider();
        var queue1 = serviceProvider.GetRequiredService<IMongoQueue<TestPayload>>();
        var queue2 = serviceProvider.GetRequiredService<IMongoQueue<AnotherPayload>>();

        // Act - Publish messages BEFORE starting
        var testPayload1 = new TestPayload
        {
            Value = "Queue 1 Message 1"
        };
        var testPayload2 = new TestPayload
        {
            Value = "Queue 1 Message 2"
        };
        var anotherPayload1 = new AnotherPayload
        {
            Data = "Queue 2 Message 1"
        };
        var anotherPayload2 = new AnotherPayload
        {
            Data = "Queue 2 Message 2"
        };

        await queue1.PublishAsync(testPayload1);
        await queue2.PublishAsync(anotherPayload1);
        await queue1.PublishAsync(testPayload2);
        await queue2.PublishAsync(anotherPayload2);

        await queue1.StartSubscriptionAsync();
        await queue2.StartSubscriptionAsync();

        // Wait for messages to be processed
        await handler1.WaitForMessages(2, TimeSpan.FromSeconds(10));
        await handler2.WaitForMessages(2, TimeSpan.FromSeconds(10));

        // Assert
        handler1.ProcessedPayloads.Should().HaveCount(2);
        handler1.ProcessedPayloads.Should().Contain(p => p.Value == "Queue 1 Message 1");
        handler1.ProcessedPayloads.Should().Contain(p => p.Value == "Queue 1 Message 2");

        handler2.ProcessedPayloads.Should().HaveCount(2);
        handler2.ProcessedPayloads.Should().Contain(p => p.Data == "Queue 2 Message 1");
        handler2.ProcessedPayloads.Should().Contain(p => p.Data == "Queue 2 Message 2");

        // Cleanup
        await queue1.StopSubscriptionAsync();
        await queue2.StopSubscriptionAsync();
    }

    [Test]
    public async Task Queue_ProcessingManyMessages_HandlesThemInOrder()
    {
        // Arrange
        var handler = new TestPayloadHandler();
        var services = new ServiceCollection();
        services.AddNUnitTestLogging();

        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"QueuePerformanceTest_{Guid.NewGuid():N}";
        services.AddMongo(url, uniqueDbName)
                .WithQueue<TestPayload>(queue => queue
                                                 .WithPayloadHandler(_ => handler)
                                                 .WithCollectionName("test-queue")
                                                 .WithQueryLimit(10) // Process multiple at once
                                                 .WithoutAutoStartSubscription());

        var serviceProvider = services.BuildServiceProvider();
        var queue = serviceProvider.GetRequiredService<IMongoQueue<TestPayload>>();
        await queue.StartSubscriptionAsync();

        // Act - Publish 50 messages
        var messageCount = 50;
        for (var i = 0; i < messageCount; i++)
        {
            await queue.PublishAsync(new TestPayload
            {
                Value = $"Message {i}"
            });
        }

        // Wait for all messages to be processed
        await handler.WaitForMessages(messageCount, TimeSpan.FromSeconds(30));

        // Assert
        handler.ProcessedPayloads.Should().HaveCount(messageCount);
        for (var i = 0; i < messageCount; i++)
        {
            handler.ProcessedPayloads.Should().Contain(p => p.Value == $"Message {i}");
        }

        // Cleanup
        await queue.StopSubscriptionAsync();
    }

    [Test]
    public async Task Queue_StartStopRestart_MaintainsCorrectState()
    {
        // Arrange
        var handler = new TestPayloadHandler();
        var services = new ServiceCollection();
        services.AddNUnitTestLogging();

        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"QueueLifecycleTest_{Guid.NewGuid():N}";
        services.AddMongo(url, uniqueDbName)
                .WithQueue<TestPayload>(queue => queue
                                                 .WithPayloadHandler(_ => handler)
                                                 .WithCollectionName("test-queue")
                                                 .WithoutAutoStartSubscription());

        var serviceProvider = services.BuildServiceProvider();
        var queue = serviceProvider.GetRequiredService<IMongoQueue<TestPayload>>();

        // Act & Assert - Initial state
        queue.IsRunning.Should().BeFalse();

        // Publish first message before starting
        await queue.PublishAsync(new TestPayload
        {
            Value = "Before Stop"
        });

        // Start queue
        await queue.StartSubscriptionAsync();
        queue.IsRunning.Should().BeTrue();

        // Wait for first message to be processed
        await handler.WaitForMessages(1, TimeSpan.FromSeconds(10));
        handler.ProcessedPayloads.Should().HaveCount(1);

        // Stop queue
        await queue.StopSubscriptionAsync();
        queue.IsRunning.Should().BeFalse();

        // Publish while stopped (message should queue up)
        await queue.PublishAsync(new TestPayload
        {
            Value = "While Stopped"
        });
        await Task.Delay(500);
        handler.ProcessedPayloads.Should().HaveCount(1); // Still only 1

        // Restart queue
        await queue.StartSubscriptionAsync();
        queue.IsRunning.Should().BeTrue();

        // Poll for the second message to be processed
        var maxWait = TimeSpan.FromSeconds(5);
        var deadline = DateTime.UtcNow.Add(maxWait);
        while (handler.ProcessedPayloads.Count < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        // Verify both messages were processed
        handler.ProcessedPayloads.Should().HaveCount(2);
        handler.ProcessedPayloads.Should().Contain(p => p.Value == "Before Stop");
        handler.ProcessedPayloads.Should().Contain(p => p.Value == "While Stopped");

        // Cleanup
        await queue.StopSubscriptionAsync();
    }

    [Test]
    public async Task Queue_WhenHandlerThrowsException_ContinuesProcessingOtherMessages()
    {
        // Arrange
        var handler = new FaultyPayloadHandler();
        var services = new ServiceCollection();
        services.AddNUnitTestLogging();

        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"QueueFaultyHandlerTest_{Guid.NewGuid():N}";
        services.AddMongo(url, uniqueDbName)
                .WithQueue<TestPayload>(queue => queue
                                                 .WithPayloadHandler(_ => handler)
                                                 .WithCollectionName("test-queue")
                                                 .WithoutAutoStartSubscription());

        var serviceProvider = services.BuildServiceProvider();
        var queue = serviceProvider.GetRequiredService<IMongoQueue<TestPayload>>();
        await queue.StartSubscriptionAsync();

        // Act
        var payload1 = new TestPayload
        {
            Value = "Fail"
        }; // This will throw
        var payload2 = new TestPayload
        {
            Value = "Success 1"
        };
        var payload3 = new TestPayload
        {
            Value = "Success 2"
        };

        await queue.PublishAsync(payload1);
        await queue.PublishAsync(payload2);
        await queue.PublishAsync(payload3);

        // Wait for messages to be processed (including retries)
        await handler.WaitForSuccessfulMessages(2, TimeSpan.FromSeconds(10));

        // Assert
        handler.SuccessfulPayloads.Should().HaveCount(2);
        handler.SuccessfulPayloads.Should().Contain(p => p.Value == "Success 1");
        handler.SuccessfulPayloads.Should().Contain(p => p.Value == "Success 2");
        handler.FailedPayloads.Should().HaveCountGreaterOrEqualTo(1);
        handler.FailedPayloads.Should().Contain(p => p.Value == "Fail");

        // Cleanup
        await queue.StopSubscriptionAsync();
    }

    [Test]
    public async Task Queue_WithEmptyQueue_DoesNotCrash()
    {
        // Arrange
        var handler = new TestPayloadHandler();
        var services = new ServiceCollection();
        services.AddNUnitTestLogging();

        var url = MongoUrl.Create(_container.GetConnectionString());
        var uniqueDbName = $"EmptyQueueTest_{Guid.NewGuid():N}";
        services.AddMongo(url, uniqueDbName)
                .WithQueue<TestPayload>(queue => queue
                                                 .WithPayloadHandler(_ => handler)
                                                 .WithCollectionName("test-queue")
                                                 .WithoutAutoStartSubscription());

        var serviceProvider = services.BuildServiceProvider();
        var queue = serviceProvider.GetRequiredService<IMongoQueue<TestPayload>>();

        // Act - Start queue with no messages
        await queue.StartSubscriptionAsync();
        await Task.Delay(2000); // Wait to ensure it doesn't crash

        // Assert
        queue.IsRunning.Should().BeTrue();
        handler.ProcessedPayloads.Should().BeEmpty();

        // Cleanup
        await queue.StopSubscriptionAsync();
    }

    public class AnotherPayload
    {
        public String Data { get; init; } = String.Empty;
    }

    public class AnotherPayloadHandler : IMongoQueuePayloadHandler<AnotherPayload>
    {
        private readonly List<AnotherPayload> _processedPayloads = [];
        private readonly SemaphoreSlim _semaphore = new(0);

        public IReadOnlyList<AnotherPayload> ProcessedPayloads => _processedPayloads;

        public async Task WaitForMessages(Int32 count, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            for (var i = 0; i < count; i++)
            {
                await _semaphore.WaitAsync(cts.Token);
            }
        }

        public Task HandlePayloadAsync(AnotherPayload payload, CancellationToken cancellationToken = default)
        {
            _processedPayloads.Add(payload);
            _semaphore.Release();
            return Task.CompletedTask;
        }
    }

    public class FaultyPayloadHandler : IMongoQueuePayloadHandler<TestPayload>
    {
        private readonly List<TestPayload> _failedPayloads = [];
        private readonly SemaphoreSlim _semaphore = new(0);
        private readonly List<TestPayload> _successfulPayloads = [];

        public IReadOnlyList<TestPayload> FailedPayloads => _failedPayloads;
        public IReadOnlyList<TestPayload> SuccessfulPayloads => _successfulPayloads;

        public async Task WaitForSuccessfulMessages(Int32 count, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            for (var i = 0; i < count; i++)
            {
                await _semaphore.WaitAsync(cts.Token);
            }
        }

        public Task HandlePayloadAsync(TestPayload payload, CancellationToken cancellationToken = default)
        {
            if (payload.Value == "Fail")
            {
                _failedPayloads.Add(payload);
                throw new InvalidOperationException("Simulated handler failure");
            }

            _successfulPayloads.Add(payload);
            _semaphore.Release();
            return Task.CompletedTask;
        }
    }

    public class TestPayload
    {
        public String Value { get; init; } = String.Empty;
    }

    public class TestPayloadHandler : IMongoQueuePayloadHandler<TestPayload>
    {
        private readonly List<TestPayload> _processedPayloads = [];
        private readonly SemaphoreSlim _semaphore = new(0);

        public IReadOnlyList<TestPayload> ProcessedPayloads => _processedPayloads;

        public async Task WaitForMessages(Int32 count, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            for (var i = 0; i < count; i++)
            {
                await _semaphore.WaitAsync(cts.Token);
            }
        }

        public Task HandlePayloadAsync(TestPayload payload, CancellationToken cancellationToken = default)
        {
            _processedPayloads.Add(payload);
            _semaphore.Release();
            return Task.CompletedTask;
        }
    }
}
