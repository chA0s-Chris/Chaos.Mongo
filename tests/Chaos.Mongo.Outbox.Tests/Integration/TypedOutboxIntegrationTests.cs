// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox.Tests.Integration;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;
using NUnit.Framework;
using System.Collections.Concurrent;
using System.Diagnostics;
using Testcontainers.MongoDb;

public class TypedOutboxIntegrationTests
{
    private MongoDbContainer _container;

    [Test]
    public async Task AddMessageAsync_DestinationRegistry_RejectsUnregisteredPayloadAndRequiresTransaction()
    {
        await using var provider = CreateServices().BuildServiceProvider();
        var helper = provider.GetRequiredService<IMongoHelper>();
        using var session = await helper.Client.StartSessionAsync();
        var first = provider.GetRequiredService<IOutbox<FirstDestination>>();
        var second = provider.GetRequiredService<IOutbox<SecondDestination>>();
        var noTransaction = () => first.AddMessageAsync(session, new AnotherTestPayload());
        await noTransaction.Should().ThrowAsync<InvalidOperationException>().WithMessage("*active MongoDB transaction*");
        await provider.GetRequiredService<IOutboxConfiguratorRunner>().RunAsync();
        session.StartTransaction();
        await first.AddMessageAsync(session, new AnotherTestPayload());
        var wrongDestination = () => second.AddMessageAsync(session, new AnotherTestPayload());
        await wrongDestination.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not registered*");
        await session.CommitTransactionAsync();
        (await helper.Database.GetCollection<OutboxMessage>("First").CountDocumentsAsync(FilterDefinition<OutboxMessage>.Empty)).Should().Be(1);
        (await helper.Database.GetCollection<OutboxMessage>("Second").CountDocumentsAsync(FilterDefinition<OutboxMessage>.Empty)).Should().Be(0);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task AddMessageAsync_SharedTransaction_CommitsOrRollsBackEveryDestination(Boolean commit)
    {
        await using var provider = CreateServices().BuildServiceProvider();
        await provider.GetRequiredService<IOutboxConfiguratorRunner>().RunAsync();
        var helper = provider.GetRequiredService<IMongoHelper>();
        using var session = await helper.Client.StartSessionAsync();
        session.StartTransaction();
        await provider.GetRequiredService<IOutbox<FirstDestination>>().AddMessageAsync(session, new TestPayload
        {
            Name = "Shared"
        }, "transaction");
        await provider.GetRequiredService<IOutbox<SecondDestination>>().AddMessageAsync(session, new TestPayload
        {
            Name = "Shared"
        }, "transaction");
        await provider.GetRequiredService<IOutbox>().AddMessageAsync(session, new TestPayload
        {
            Name = "Shared"
        }, "transaction");
        if (commit)
            await session.CommitTransactionAsync();
        else
            await session.AbortTransactionAsync();

        foreach (var name in new[]
                 {
                     "First",
                     "Second",
                     "Outbox"
                 })
        {
            var messages = await helper.Database.GetCollection<OutboxMessage>(name).Find(FilterDefinition<OutboxMessage>.Empty).ToListAsync();
            messages.Should().HaveCount(commit ? 1 : 0);
            if (commit)
            {
                messages[0].Type.Should().Be(name);
                messages[0].Payload["Name"].AsString.Should().Be("Shared");
                messages[0].CorrelationId.Should().Be("transaction");
                var document = await helper.Database.GetCollection<BsonDocument>(name).Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
                document.Should().NotContain(e => e.Name == "OutboxIdentity");
            }
        }
    }

    [OneTimeSetUp]
    public async Task GetMongoDbContainer() => _container = await MongoDbTestContainer.StartContainerAsync();

    [Test]
    public async Task RunAsync_DifferentRetentionPolicies_CreatesIndependentIndexes()
    {
        await using var provider = CreateServices().BuildServiceProvider();
        await provider.GetRequiredService<IOutboxConfiguratorRunner>().RunAsync();
        var database = provider.GetRequiredService<IMongoHelper>().Database;
        foreach (var (name, days) in new[]
                 {
                     ("First", 1),
                     ("Second", 2),
                     ("Outbox", 0)
                 })
        {
            var indexes = await (await database.GetCollection<OutboxMessage>(name).Indexes.ListAsync()).ToListAsync();
            indexes.Should().Contain(i => i["name"] == "IX_Outbox_Polling");
            var ttl = indexes.Where(i => i.Contains("expireAfterSeconds")).ToArray();
            ttl.Should().HaveCount(days == 0 ? 0 : 2);
            foreach (var index in ttl)
                index["expireAfterSeconds"].ToDouble().Should().Be(TimeSpan.FromDays(days).TotalSeconds);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task StartAsync_BlockedOrFailingDestination_OtherOutboxesProgressIndependently(Boolean fail)
    {
        var services = CreateServices();
        await using var provider = services.BuildServiceProvider();
        var hub = provider.GetRequiredService<TypedPublicationHub>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.Handlers["First"] = async token =>
        {
            using var registration = token.Register(() => canceled.TrySetResult());
            entered.TrySetResult();
            if (fail)
                throw new InvalidOperationException("First destination unavailable");
            await release.Task.WaitAsync(token);
        };
        await provider.GetRequiredService<IOutboxConfiguratorRunner>().RunAsync();
        await EnqueueAsync(provider);
        var first = provider.GetRequiredService<IOutboxProcessor<FirstDestination>>();
        var second = provider.GetRequiredService<IOutboxProcessor<SecondDestination>>();
        var original = provider.GetRequiredService<IOutboxProcessor>();
        await first.StartAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            hub.Publications.Should().NotContain(p => p.Destination != "First");
            await second.StartAsync();
            await original.StartAsync();
            await WaitUntilAsync(async () => await StateAsync(provider, "Second") == OutboxMessageState.Processed &&
                                             await StateAsync(provider, "Outbox") == OutboxMessageState.Processed);
            await second.StopAsync();
            canceled.Task.IsCompleted.Should().BeFalse("stopping Second must not cancel First");
            if (fail)
            {
                await WaitUntilAsync(async () => await StateAsync(provider, "First") == OutboxMessageState.Failed);
                var message = await Collection(provider, "First").Find(FilterDefinition<OutboxMessage>.Empty).SingleAsync();
                message.RetryCount.Should().Be(1);
                message.Error.Should().Be("First destination unavailable");
            }
            else
            {
                (await StateAsync(provider, "First")).Should().Be(OutboxMessageState.Pending);
                release.TrySetResult();
                await WaitUntilAsync(async () => await StateAsync(provider, "First") == OutboxMessageState.Processed);
            }
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(first.StopAsync(), second.StopAsync(), original.StopAsync());
        }
    }

    [Test]
    public async Task StartAsync_DifferentFiltersAndRetryPolicies_KeepEligibilityAndFailuresIsolated()
    {
        await using var provider = CreateServices(filterSecond: true).BuildServiceProvider();
        await provider.GetRequiredService<IOutboxConfiguratorRunner>().RunAsync();
        await EnqueueAsync(provider);
        var hub = provider.GetRequiredService<TypedPublicationHub>();
        hub.Handlers["First"] = _ => throw new InvalidOperationException("Failed");
        hub.Handlers["Outbox"] = _ => throw new InvalidOperationException("Retry");
        var first = provider.GetRequiredService<IOutboxProcessor<FirstDestination>>();
        var second = provider.GetRequiredService<IOutboxProcessor<SecondDestination>>();
        var original = provider.GetRequiredService<IOutboxProcessor>();
        try
        {
            await Task.WhenAll(first.StartAsync(), second.StartAsync(), original.StartAsync());
            await WaitUntilAsync(async () => await StateAsync(provider, "First") == OutboxMessageState.Failed &&
                                             (await Collection(provider, "Outbox").Find(FilterDefinition<OutboxMessage>.Empty).SingleAsync()).RetryCount == 1);
            (await StateAsync(provider, "Second")).Should().Be(OutboxMessageState.Pending);
            var retry = await Collection(provider, "Outbox").Find(FilterDefinition<OutboxMessage>.Empty).SingleAsync();
            retry.State.Should().Be(OutboxMessageState.Pending);
            retry.NextAttemptUtc.Should().BeAfter(DateTime.UtcNow.AddMinutes(20));
            hub.Publications.Should().NotContain(p => p.Destination == "Second");
            // Only Second's filter excludes this correlation ID; changing eligibility lets its own processor proceed.
            await Collection(provider, "Second").UpdateOneAsync(FilterDefinition<OutboxMessage>.Empty,
                                                                Builders<OutboxMessage>.Update.Set(m => m.CorrelationId, "eligible"));
            await WaitUntilAsync(async () => await StateAsync(provider, "Second") == OutboxMessageState.Processed);
        }
        finally
        {
            await Task.WhenAll(first.StopAsync(), second.StopAsync(), original.StopAsync());
        }
    }

    [TestCase(ServiceLifetime.Transient)]
    [TestCase(ServiceLifetime.Scoped)]
    [TestCase(ServiceLifetime.Singleton)]
    public async Task StartAsync_SharedPublisherImplementation_ResolvesPerDestinationAndBatch(ServiceLifetime lifetime)
    {
        await using var provider = CreateServices(lifetime: lifetime).BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true
        });
        await provider.GetRequiredService<IOutboxConfiguratorRunner>().RunAsync();
        var hub = provider.GetRequiredService<TypedPublicationHub>();
        await EnqueueAsync(provider);
        var first = provider.GetRequiredService<IOutboxProcessor<FirstDestination>>();
        var second = provider.GetRequiredService<IOutboxProcessor<SecondDestination>>();
        var original = provider.GetRequiredService<IOutboxProcessor>();
        try
        {
            await Task.WhenAll(first.StartAsync(), second.StartAsync(), original.StartAsync());
            await WaitUntilAsync(() => Task.FromResult(hub.Publications.Count == 3));
            await EnqueueAsync(provider);
            await WaitUntilAsync(() => Task.FromResult(hub.Publications.Count == 6));
        }
        finally
        {
            await Task.WhenAll(first.StopAsync(), second.StopAsync(), original.StopAsync());
        }

        var publications = hub.Publications.ToArray();
        foreach (var destination in new[]
                 {
                     "First",
                     "Second",
                     "Outbox"
                 })
        {
            var own = publications.Where(p => p.Destination == destination).Select(p => p.PublisherId).ToArray();
            own.Should().HaveCount(2);
            own.Distinct().Should().HaveCount(destination == "First" && lifetime != ServiceLifetime.Singleton ? 2 : 1);
            publications.Where(p => p.Destination != destination).Should().NotContain(p => own.Contains(p.PublisherId));
        }

        if (lifetime != ServiceLifetime.Singleton)
            hub.Disposed.Should().Contain(publications.Where(p => p.Destination == "First").Select(p => p.PublisherId));
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task StopAsync_DelayedPublisher_CancelsEveryProcessorBeforeWaiting(Boolean expireShutdown, Boolean blockCallback)
    {
        var services = CreateServices(true);
        using var host = new HostBuilder().ConfigureServices(s =>
        {
            foreach (var descriptor in services)
                s.Add(descriptor);
        }).Build();
        var hub = host.Services.GetRequiredService<TypedPublicationHub>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var callbackRelease = new ManualResetEventSlim();
        hub.Handlers["First"] = async token =>
        {
            using var registration = token.Register(() =>
            {
                if (!blockCallback)
                    return;
                callbackEntered.TrySetResult();
                // Cancellation callbacks are synchronous; hold this one until the test releases it.
                callbackRelease.Wait();
            });
            firstEntered.TrySetResult();
            await release.Task;
        };
        hub.Handlers["Second"] = async token =>
        {
            using var registration = token.Register(() => secondCanceled.TrySetResult());
            secondEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        await host.Services.GetRequiredService<IOutboxConfiguratorRunner>().RunAsync();
        await EnqueueAsync(host.Services);
        await host.StartAsync();
        using var shutdown = new CancellationTokenSource();
        var stopping = Task.CompletedTask;
        try
        {
            await Task.WhenAll(firstEntered.Task, secondEntered.Task).WaitAsync(TimeSpan.FromSeconds(10));
            stopping = host.StopAsync(shutdown.Token);
            await secondCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (blockCallback)
                await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stopping.IsCompleted.Should().BeFalse("First's publisher is still held");
            if (expireShutdown)
            {
                shutdown.Cancel();
                await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            callbackRelease.Set();
            release.TrySetResult();
            await stopping.WaitAsync(TimeSpan.FromSeconds(10));
            // Completion cleanup may outlive the host shutdown deadline.
            await WaitUntilAsync(async () => !(await Collection(host.Services, "First").Find(FilterDefinition<OutboxMessage>.Empty).SingleAsync()).IsLocked);
        }
    }

    private static IMongoCollection<OutboxMessage> Collection(IServiceProvider provider, String name)
        => provider.GetRequiredService<IMongoHelper>().Database.GetCollection<OutboxMessage>(name);

    private static void Configure(OutboxBuilder builder, String name, Boolean autoStart)
    {
        builder.WithCollectionName(name).WithMessage<TestPayload>(name)
               .WithPublisher<TrackedTypedPublisher>(ServiceLifetime.Singleton)
               .WithPollingInterval(TimeSpan.FromMilliseconds(50));
        if (autoStart)
            builder.WithAutoStartProcessor();
    }

    private static async Task EnqueueAsync(IServiceProvider provider)
    {
        using var session = await provider.GetRequiredService<IMongoHelper>().Client.StartSessionAsync();
        session.StartTransaction();
        await provider.GetRequiredService<IOutbox<FirstDestination>>().AddMessageAsync(session, new TestPayload());
        await provider.GetRequiredService<IOutbox<SecondDestination>>().AddMessageAsync(session, new TestPayload());
        await provider.GetRequiredService<IOutbox>().AddMessageAsync(session, new TestPayload());
        await session.CommitTransactionAsync();
    }

    private static async Task<OutboxMessageState> StateAsync(IServiceProvider provider, String name)
        => (await Collection(provider, name).Find(FilterDefinition<OutboxMessage>.Empty).SingleAsync()).State;

    private static async Task WaitUntilAsync(Func<Task<Boolean>> condition)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (await condition())
                return;
            await Task.Delay(20);
        }

        throw new TimeoutException("Outbox state did not converge within ten seconds.");
    }

    private IServiceCollection CreateServices(Boolean autoStart = false, ServiceLifetime lifetime = ServiceLifetime.Singleton, Boolean filterSecond = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TypedPublicationHub>();
        services.AddMongo(_container.GetConnectionString(), $"TypedOutbox_{Guid.NewGuid():N}", o =>
                {
                    o.RunConfiguratorsOnStartup = false;
                    o.ApplyMigrationsOnStartup = false;
                })
                .WithOutbox<FirstDestination>(o =>
                {
                    Configure(o, "First", autoStart);
                    o.WithMessage<AnotherTestPayload>().WithPublisher<TrackedTypedPublisher>(lifetime)
                     .WithRetentionPeriod(TimeSpan.FromDays(1)).WithBatchSize(1).WithMaxRetries(1)
                     .WithLockTimeout(TimeSpan.FromSeconds(20));
                })
                .WithOutbox<SecondDestination>(o =>
                {
                    Configure(o, "Second", autoStart);
                    o.WithRetentionPeriod(TimeSpan.FromDays(2)).WithBatchSize(3).WithLockTimeout(TimeSpan.FromSeconds(60));
                    if (filterSecond)
                        o.WithProcessingFilter(Builders<OutboxMessage>.Filter.Eq(m => m.CorrelationId, "eligible"));
                })
                .WithOutbox(o =>
                {
                    Configure(o, "Outbox", autoStart);
                    o.WithRetryBackoff(TimeSpan.FromMinutes(30), TimeSpan.FromHours(1));
                });
        return services;
    }
}

internal sealed class TypedPublicationHub
{
    public ConcurrentBag<Guid> Disposed { get; } = [];
    public ConcurrentDictionary<String, Func<CancellationToken, Task>> Handlers { get; } = new();
    public ConcurrentBag<(String Destination, Guid PublisherId)> Publications { get; } = [];
}

internal sealed class TrackedTypedPublisher : IOutboxPublisher, IDisposable
{
    private readonly TypedPublicationHub _hub;
    private readonly Guid _id = Guid.NewGuid();

    public TrackedTypedPublisher(TypedPublicationHub hub) => _hub = hub;

    public void Dispose() => _hub.Disposed.Add(_id);

    public async Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        _hub.Publications.Add((message.Type, _id));
        if (_hub.Handlers.TryGetValue(message.Type, out var handler))
            await handler(cancellationToken);
    }
}
