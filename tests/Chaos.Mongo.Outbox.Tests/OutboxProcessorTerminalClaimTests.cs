// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox.Tests;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using NUnit.Framework;
using System.Collections.Concurrent;
using System.Reflection;

public class OutboxProcessorTerminalClaimTests
{
    [Test]
    public async Task StartAsync_ClaimTerminalizesDirectly_SkipsPublishingWithoutFurtherWrite()
    {
        // A claim result already in the Failed state means the claim's own terminal branch fired atomically: the
        // message crashed on an earlier granted claim, and its pre-update AttemptCount already exceeded
        // MaxRetries. The processor must then skip publishing and must not write to the message again.
        var instance = DispatchProxy.Create(typeof(IMongoDatabase), typeof(TerminalClaimDatabase));
        var database = (IMongoDatabase)instance;
        var collection = ((TerminalClaimDatabase)instance).Capture;
        var logs = new OutboxLogCapture();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(logs));
        services.AddSingleton<IMongoHelper>(new ProcessingFilterMongoHelper(database));
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero)));
        new MongoBuilder(services).WithOutbox(builder => builder.WithPublisher<TestOutboxPublisher>(ServiceLifetime.Singleton)
                                                                .WithMessage<TestPayload>()
                                                                .WithMaxRetries(5)
                                                                .WithBatchSize(3)
                                                                .WithPollingInterval(TimeSpan.FromHours(1)));
        await using var provider = services.BuildServiceProvider();
        var processor = provider.GetRequiredService<IOutboxProcessor>();

        try
        {
            await processor.StartAsync();
            await collection.ClaimAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            // StopAsync awaits the processing loop, so every call made after the claim is recorded by now.
            await processor.StopAsync();
        }

        ((TestOutboxPublisher)provider.GetRequiredService<IOutboxPublisher>()).PublishedMessages.Should().BeEmpty();
        collection.Calls.Should().Equal("FindAsync", "FindOneAndUpdateAsync");
        logs.Entries.Where(entry => entry.Category == typeof(OutboxProcessor).FullName)
            .SelectMany(entry => entry.Values)
            .Where(value => value.Key == "{OriginalFormat}")
            .Select(value => value.Value as String)
            .Should().Contain(format => format != null && format.Contains("without granting a further attempt"));
    }
}

internal class TerminalClaimCollection : DispatchProxy
{
    public TerminalClaimCollection()
    {
        ClaimAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MessageId = ObjectId.GenerateNewId();
    }

    public ConcurrentQueue<String> Calls { get; } = new();

    public TaskCompletionSource ClaimAttempted { get; }

    public ObjectId MessageId { get; }

    protected override Object? Invoke(MethodInfo? targetMethod, Object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        switch (targetMethod.Name)
        {
            case "get_DocumentSerializer":
                return BsonSerializer.SerializerRegistry.GetSerializer<OutboxMessage>();
            case "get_Settings":
                return new MongoCollectionSettings();
        }

        // Every other call is recorded before it is answered, so an unexpected write is visible to the test
        // even though the processing loop swallows the exception thrown for it.
        Calls.Enqueue(targetMethod.Name);
        switch (targetMethod.Name)
        {
            case "FindAsync":
                return Task.FromResult<IAsyncCursor<OutboxMessage>>(new ProcessingFilterCursor(CreateMessage(OutboxMessageState.Pending, 5)));
            case "FindOneAndUpdateAsync":
                ClaimAttempted.TrySetResult();
                // WithMaxRetries(5) is configured, so an AttemptCount of 6 is the claim's own terminal branch.
                return Task.FromResult<OutboxMessage?>(CreateMessage(OutboxMessageState.Failed, 6));
            default:
                throw new NotSupportedException(targetMethod.Name);
        }
    }

    private OutboxMessage CreateMessage(OutboxMessageState state, Int64 attemptCount)
        => new()
        {
            Id = MessageId,
            State = state,
            Type = "TestPayload",
            Payload = new BsonDocument("Name", "Test"),
            AttemptCount = attemptCount
        };
}

internal class TerminalClaimDatabase : DispatchProxy
{
    public TerminalClaimDatabase()
    {
        var instance = Create(typeof(IMongoCollection<OutboxMessage>), typeof(TerminalClaimCollection));
        Collection = (IMongoCollection<OutboxMessage>)instance;
        Capture = (TerminalClaimCollection)instance;
    }

    public TerminalClaimCollection Capture { get; }

    public IMongoCollection<OutboxMessage> Collection { get; }

    protected override Object Invoke(MethodInfo? targetMethod, Object?[]? args)
        => targetMethod?.Name == "GetCollection" ? Collection : throw new NotSupportedException(targetMethod?.Name);
}
