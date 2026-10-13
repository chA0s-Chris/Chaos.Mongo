// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox.Tests.Integration;

using Chaos.Mongo.Configuration;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using NUnit.Framework;
using Testcontainers.MongoDb;

public class OutboxShutdownIntegrationTests
{
    private MongoDbContainer _container;

    [OneTimeSetUp]
    public async Task GetMongoDbContainer()
        => _container = await MongoDbTestContainer.StartContainerAsync();

    [Test]
    public async Task StopAsync_PublishCompletesAfterStopWasRequested_PersistsMessageAsProcessed()
    {
        await using var provider = CreateServiceProvider();
        var helper = provider.GetRequiredService<IMongoHelper>();
        foreach (var configurator in provider.GetServices<IMongoConfigurator>())
            await configurator.ConfigureAsync(helper);

        using (var session = await helper.Client.StartSessionAsync())
        {
            session.StartTransaction();
            await provider.GetRequiredService<IOutbox>().AddMessageAsync(session, new TestPayload
            {
                Name = "Held"
            });
            await session.CommitTransactionAsync();
        }

        var publisher = provider.GetRequiredService<IOutboxPublisher>().Should().BeOfType<GatedOutboxPublisher>().Which;
        var processor = provider.GetRequiredService<IOutboxProcessor>();
        await processor.StartAsync();
        Task? stopping = null;
        try
        {
            await publisher.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            stopping = processor.StopAsync();
            stopping.IsCompleted.Should().BeFalse("the publish is still held");
        }
        finally
        {
            publisher.Release.TrySetResult();
            await (stopping ?? processor.StopAsync()).WaitAsync(TimeSpan.FromSeconds(10));
        }

        var message = await helper.Database.GetCollection<OutboxMessage>(OutboxOptions.DefaultCollectionName)
                                  .Find(FilterDefinition<OutboxMessage>.Empty)
                                  .SingleAsync();
        message.State.Should().Be(OutboxMessageState.Processed);
        message.ProcessedUtc.Should().NotBeNull();
        message.IsLocked.Should().BeFalse();
        message.LockId.Should().BeNull();
    }

    private ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMongo(MongoUrl.Create(_container.GetConnectionString()), configure: options =>
                {
                    options.DefaultDatabase = $"OutboxShutdown_{Guid.NewGuid():N}";
                    options.RunConfiguratorsOnStartup = false;
                })
                .WithOutbox(builder => builder
                                       .WithPublisher<GatedOutboxPublisher>(ServiceLifetime.Singleton)
                                       .WithMessage<TestPayload>("TestPayload")
                                       .WithPollingInterval(TimeSpan.FromMilliseconds(50)));
        return services.BuildServiceProvider();
    }
}

internal sealed class GatedOutboxPublisher : IOutboxPublisher
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        Entered.TrySetResult();
        // Ignores the processor token on purpose: the publish completes successfully after the stop was requested.
        await Release.Task;
    }
}
