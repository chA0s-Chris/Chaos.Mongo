// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox.Tests.Integration;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using NUnit.Framework;
using System.Collections.Concurrent;

public class OutboxProcessingFilterIntegrationTests
{
    [Test]
    public async Task Processor_CancellationAfterPublishFailure_LeavesRemainingSelectedMessageUntouched()
    {
        using var cancellation = new CancellationTokenSource();
        Action cancel = cancellation.Cancel;
        var publisher = new ProcessingFilterPublisher
        {
            OnPublish = (_, _) =>
            {
                cancel();
                throw new InvalidOperationException("Publish failed during shutdown");
            }
        };
        var filter = Builders<OutboxMessage>.Filter.Eq(message => message.IsLocked, false);
        await using var services = await CreateServicesAsync($"OutboxFilter_{Guid.NewGuid():N}", filter, publisher);
        var collection = Collection(services);
        var messages = Enumerable.Range(0, 2).Select(_ => new OutboxMessage
                                 {
                                     Id = ObjectId.GenerateNewId(),
                                     Type = "Allowed"
                                 })
                                 .OrderBy(message => message.Id).ToArray();
        await collection.InsertManyAsync(messages);
        var remaining = Builders<OutboxMessage>.Filter.Eq(message => message.Id, messages[1].Id);
        var before = await SnapshotAsync(collection, remaining);
        var processor = services.GetRequiredService<IOutboxProcessor>();
        try
        {
            await processor.StartAsync(cancellation.Token);
            await WaitUntilAsync(async () => await collection.CountDocumentsAsync(message => message.Id == messages[0].Id && message.RetryCount == 1) == 1);
        }
        finally
        {
            await processor.StopAsync();
        }

        publisher.Messages.Select(message => message.Id).Should().Equal(messages[0].Id);
        (await SnapshotAsync(collection, remaining)).Should().BeEquivalentTo(before);
        var failed = await collection.Find(message => message.Id == messages[0].Id).SingleAsync();
        failed.State.Should().Be(OutboxMessageState.Pending);
        failed.NextAttemptUtc.Should().NotBeNull();
        failed.IsLocked.Should().BeFalse();
        failed.LockId.Should().BeNull();
        failed.LockedUtc.Should().BeNull();
    }

    [TestCase("Success")]
    [TestCase("Retry")]
    [TestCase("Failure")]
    [TestCase("Cancellation")]
    public async Task Processor_ClaimChangesFilteredField_FinalizesOwnedClaim(String outcome)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisher = new ProcessingFilterPublisher
        {
            OnPublish = async (_, token) =>
            {
                entered.TrySetResult();
                if (outcome == "Cancellation")
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                if (outcome is "Retry" or "Failure")
                    throw new InvalidOperationException("Publish failed");
            }
        };
        var filter = Builders<OutboxMessage>.Filter.Eq(message => message.IsLocked, false);
        await using var services = await CreateServicesAsync($"OutboxFilter_{Guid.NewGuid():N}", filter, publisher,
                                                             builder => builder.WithMaxRetries(outcome == "Failure" ? 1 : 3)
                                                                               .WithRetryBackoff(TimeSpan.FromHours(1), TimeSpan.FromHours(1)));
        var collection = Collection(services);
        var message = new OutboxMessage
        {
            Type = "Allowed"
        };
        await collection.InsertOneAsync(message);
        var processor = services.GetRequiredService<IOutboxProcessor>();
        try
        {
            await processor.StartAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (outcome != "Cancellation")
            {
                await WaitUntilAsync(async () =>
                {
                    var current = await collection.Find(item => item.Id == message.Id).SingleAsync();
                    return !current.IsLocked && (current.State != OutboxMessageState.Pending || current.RetryCount == 1);
                });
            }
        }
        finally
        {
            await processor.StopAsync();
        }

        var actual = await collection.Find(item => item.Id == message.Id).SingleAsync();
        actual.IsLocked.Should().BeFalse();
        actual.LockedUtc.Should().BeNull();
        actual.LockId.Should().BeNull();
        actual.State.Should().Be(outcome switch
        {
            "Success" => OutboxMessageState.Processed,
            "Failure" => OutboxMessageState.Failed,
            _ => OutboxMessageState.Pending
        });
        actual.RetryCount.Should().Be(outcome is "Retry" or "Failure" ? 1 : 0);
        if (outcome == "Retry")
            actual.NextAttemptUtc.Should().BeAfter(DateTime.UtcNow);
        else
            actual.NextAttemptUtc.Should().BeNull();
    }

    [Test]
    public async Task Processor_CompoundFilter_PreservesBuiltInEligibilityAndExcludesNonmatches()
    {
        var filters = Builders<OutboxMessage>.Filter;
        var filter = TypeFilter("Allowed") &
                     (filters.Eq(message => message.CorrelationId, "Selected") | filters.Eq(message => message.RetryCount, 2));
        var publisher = new ProcessingFilterPublisher();
        await using var services = await CreateServicesAsync($"OutboxFilter_{Guid.NewGuid():N}", filter, publisher);
        var collection = Collection(services);
        var now = DateTime.UtcNow;
        var excluded = new[]
        {
            new OutboxMessage
            {
                Type = "Excluded",
                CorrelationId = "Selected",
                RetryCount = 2
            },
            new OutboxMessage
            {
                Type = "Allowed",
                CorrelationId = "Other",
                RetryCount = 1
            },
            new OutboxMessage
            {
                Type = "Allowed",
                CorrelationId = "Selected",
                State = OutboxMessageState.Processed
            },
            new OutboxMessage
            {
                Type = "Allowed",
                CorrelationId = "Selected",
                State = OutboxMessageState.Failed
            },
            new OutboxMessage
            {
                Type = "Allowed",
                CorrelationId = "Selected",
                NextAttemptUtc = now.AddHours(1)
            },
            new OutboxMessage
            {
                Type = "Allowed",
                CorrelationId = "Selected",
                IsLocked = true,
                LockedUtc = now,
                LockId = "owner"
            }
        };
        var included = new[]
        {
            new OutboxMessage
            {
                Type = "Allowed",
                CorrelationId = "Selected",
                NextAttemptUtc = now.AddMinutes(-1)
            },
            new OutboxMessage
            {
                Type = "Allowed",
                CorrelationId = "Other",
                RetryCount = 2,
                IsLocked = true,
                LockedUtc = now.AddHours(-1)
            }
        };
        await collection.InsertManyAsync(excluded.Concat(included));
        var excludedIds = filters.In(message => message.Id, excluded.Select(message => message.Id));
        var before = await SnapshotAsync(collection, excludedIds);
        var processor = services.GetRequiredService<IOutboxProcessor>();
        try
        {
            await processor.StartAsync();
            await WaitUntilAsync(async () => await collection.CountDocumentsAsync(
                                     filters.In(message => message.Id, included.Select(message => message.Id)) &
                                     filters.Eq(message => message.State, OutboxMessageState.Processed)) == included.Length);
        }
        finally
        {
            await processor.StopAsync();
        }

        publisher.Messages.Select(message => message.Id).Should().BeEquivalentTo(included.Select(message => message.Id));
        (await SnapshotAsync(collection, excludedIds)).Should().BeEquivalentTo(before);
    }

    [TestCase("Type")]
    [TestCase("State")]
    [TestCase("NextAttemptUtc")]
    [TestCase("IsLocked")]
    public async Task Processor_EligibilityChangedAfterSelection_DoesNotClaimOrPublishChangedMessage(String changedField)
    {
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var messages = Enumerable.Range(0, 3).Select(_ => new OutboxMessage
                                 {
                                     Id = ObjectId.GenerateNewId(),
                                     Type = "Allowed"
                                 })
                                 .OrderBy(message => message.Id).ToArray();
        var publisher = new ProcessingFilterPublisher
        {
            OnPublish = async (message, token) =>
            {
                if (message.Id == messages[0].Id)
                {
                    firstEntered.TrySetResult();
                    await releaseFirst.Task.WaitAsync(token);
                }
            }
        };
        await using var services = await CreateServicesAsync($"OutboxFilter_{Guid.NewGuid():N}", TypeFilter("Allowed"), publisher);
        var collection = Collection(services);
        await collection.InsertManyAsync(messages);
        var processor = services.GetRequiredService<IOutboxProcessor>();
        BsonDocument[] before;
        var changedId = Builders<OutboxMessage>.Filter.Eq(message => message.Id, messages[1].Id);
        try
        {
            await processor.StartAsync();
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var updates = Builders<OutboxMessage>.Update;
            var update = changedField switch
            {
                "Type" => updates.Set(message => message.Type, "Excluded"),
                "State" => updates.Set(message => message.State, OutboxMessageState.Processed),
                "NextAttemptUtc" => updates.Set(message => message.NextAttemptUtc, DateTime.UtcNow.AddHours(1)),
                "IsLocked" => updates.Set(message => message.IsLocked, true)
                                     .Set(message => message.LockedUtc, DateTime.UtcNow)
                                     .Set(message => message.LockId, "another-owner"),
                _ => throw new ArgumentOutOfRangeException(nameof(changedField))
            };
            await collection.UpdateOneAsync(changedId, update);
            before = await SnapshotAsync(collection, changedId);
            releaseFirst.TrySetResult();
            // The third message is fetched in the next batch, proving the earlier batch was traversed completely.
            await WaitUntilAsync(async () => await IsProcessedAsync(collection, messages[2].Id));
        }
        finally
        {
            releaseFirst.TrySetResult();
            await processor.StopAsync();
        }

        publisher.Messages.Select(message => message.Id).Should().Equal(messages[0].Id, messages[2].Id);
        (await SnapshotAsync(collection, changedId)).Should().BeEquivalentTo(before);
    }

    [Test]
    public async Task Processor_ExcludedBacklog_LeavesDocumentsUntouchedAndDeliversThemWithAnotherFilter()
    {
        var databaseName = $"OutboxFilter_{Guid.NewGuid():N}";
        var publisher = new ProcessingFilterPublisher();
        await using var services = await CreateServicesAsync(databaseName, TypeFilter("Allowed"), publisher);
        var collection = Collection(services);
        var excluded = Enumerable.Range(0, 5).Select(index => new OutboxMessage
        {
            Type = "Excluded",
            RetryCount = index,
            NextAttemptUtc = DateTime.UtcNow.AddHours(-2),
            IsLocked = index % 2 == 0,
            LockedUtc = DateTime.UtcNow.AddHours(-3),
            LockId = $"old-lock-{index}"
        }).ToArray();
        var included = new OutboxMessage
        {
            Type = "Allowed",
            NextAttemptUtc = DateTime.UtcNow.AddHours(-1)
        };
        await collection.InsertManyAsync(excluded.Append(included));
        var before = await SnapshotAsync(collection, TypeFilter("Excluded"));
        var processor = services.GetRequiredService<IOutboxProcessor>();
        try
        {
            await processor.StartAsync();
            await WaitUntilAsync(async () => await IsProcessedAsync(collection, included.Id));
        }
        finally
        {
            await processor.StopAsync();
        }

        publisher.Messages.Select(message => message.Id).Should().Equal(included.Id);
        (await SnapshotAsync(collection, TypeFilter("Excluded"))).Should().BeEquivalentTo(before);

        var nextPublisher = new ProcessingFilterPublisher();
        await using var nextServices = await CreateServicesAsync(databaseName, TypeFilter("Excluded"), nextPublisher);
        var nextProcessor = nextServices.GetRequiredService<IOutboxProcessor>();
        try
        {
            await nextProcessor.StartAsync();
            await WaitUntilAsync(async () => await collection.CountDocumentsAsync(
                                     TypeFilter("Excluded") & Builders<OutboxMessage>.Filter.Eq(message => message.State, OutboxMessageState.Processed)) == excluded.Length);
        }
        finally
        {
            await nextProcessor.StopAsync();
        }

        nextPublisher.Messages.Select(message => message.Id).Should().BeEquivalentTo(excluded.Select(message => message.Id));
    }

    private static IMongoCollection<OutboxMessage> Collection(IServiceProvider services)
        => services.GetRequiredService<IMongoHelper>().Database.GetCollection<OutboxMessage>("Outbox");

    private static async Task<ServiceProvider> CreateServicesAsync(String databaseName, FilterDefinition<OutboxMessage> filter,
                                                                   ProcessingFilterPublisher publisher, Action<OutboxBuilder>? configure = null)
    {
        var container = await MongoDbTestContainer.StartContainerAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMongo(MongoUrl.Create(container.GetConnectionString()), configure: options =>
                {
                    options.DefaultDatabase = databaseName;
                    options.RunConfiguratorsOnStartup = false;
                })
                .WithOutbox(builder =>
                {
                    builder.WithPublisher<ProcessingFilterPublisher>(ServiceLifetime.Singleton)
                           .WithMessage<TestPayload>()
                           .WithProcessingFilter(filter)
                           .WithBatchSize(2)
                           .WithPollingInterval(TimeSpan.FromMilliseconds(20));
                    configure?.Invoke(builder);
                });
        services.AddSingleton<IOutboxPublisher>(publisher);
        return services.BuildServiceProvider();
    }

    private static async Task<Boolean> IsProcessedAsync(IMongoCollection<OutboxMessage> collection, ObjectId id)
        => await collection.CountDocumentsAsync(message => message.Id == id && message.State == OutboxMessageState.Processed) == 1;

    private static async Task<BsonDocument[]> SnapshotAsync(IMongoCollection<OutboxMessage> collection, FilterDefinition<OutboxMessage> filter)
        => (await collection.Find(filter).ToListAsync()).Select(message => message.ToBsonDocument()).ToArray();

    private static FilterDefinition<OutboxMessage> TypeFilter(String type)
        => Builders<OutboxMessage>.Filter.Eq(message => message.Type, type);

    private static async Task WaitUntilAsync(Func<Task<Boolean>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await condition())
            await Task.Delay(20, timeout.Token);
    }
}

internal sealed class ProcessingFilterPublisher : IOutboxPublisher
{
    private readonly ConcurrentQueue<OutboxMessage> _messages;

    public ProcessingFilterPublisher() => _messages = new ConcurrentQueue<OutboxMessage>();
    public OutboxMessage[] Messages => _messages.ToArray();

    public Func<OutboxMessage, CancellationToken, Task>? OnPublish { get; init; }

    public async Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        _messages.Enqueue(message);
        if (OnPublish is { } publish)
            await publish(message, cancellationToken);
    }
}
