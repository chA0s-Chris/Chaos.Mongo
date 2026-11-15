// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox.Tests;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using System.Collections.Concurrent;
using System.Reflection;

internal sealed class ScriptedOutboxPublisher : IOutboxPublisher
{
    public Func<OutboxMessage, CancellationToken, Task> Handler { get; set; } = (_, _) => Task.CompletedTask;

    public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken = default)
        => Handler(message, cancellationToken);
}

/// <summary>
/// A scripted outbox collection that serves a single message and lets a test decide how the claim and every update
/// ends. It signals when the processor reached the scripted point, so a test can stop the processor deterministically.
/// </summary>
internal class MetricsOutboxCollection : DispatchProxy
{
    private Int32 _updateCalls;

    public Func<OutboxMessage, OutboxMessage?> Claim { get; set; } = message => message;

    public TaskCompletionSource ClaimAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Int32 ExpectedUpdates { get; set; } = 1;

    public OutboxMessage Message { get; } = new()
    {
        Id = ObjectId.GenerateNewId(),
        State = OutboxMessageState.Pending,
        Type = "TestPayload",
        Payload = new BsonDocument("Name", "Test"),
        AttemptCount = 1
    };

    public Func<Int32, CancellationToken, Task<UpdateResult>> Update { get; set; } = (_, _) => Task.FromResult(Modified(1));

    public Int32 UpdateCalls => Volatile.Read(ref _updateCalls);

    public TaskCompletionSource UpdateStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource UpdatesReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static UpdateResult Modified(Int64 count) => new UpdateResult.Acknowledged(count, count, null);

    protected override Object? Invoke(MethodInfo? targetMethod, Object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        switch (targetMethod.Name)
        {
            case "get_DocumentSerializer":
                return BsonSerializer.SerializerRegistry.GetSerializer<OutboxMessage>();
            case "get_Settings":
                return new MongoCollectionSettings();
            case "FindAsync":
                return Task.FromResult<IAsyncCursor<OutboxMessage>>(new ProcessingFilterCursor(Message));
            case "FindOneAndUpdateAsync":
                ClaimAttempted.TrySetResult();
                return Task.FromResult(Claim(Message));
            case "UpdateOneAsync" when args is [_, _, _, CancellationToken token]:
                var call = Interlocked.Increment(ref _updateCalls);
                UpdateStarted.TrySetResult();
                if (call >= ExpectedUpdates)
                {
                    UpdatesReached.TrySetResult();
                }

                return Update(call, token);
            default:
                throw new NotSupportedException(targetMethod.Name);
        }
    }
}

internal class MetricsOutboxDatabase : DispatchProxy
{
    private readonly ConcurrentDictionary<String, (IMongoCollection<OutboxMessage> Proxy, MetricsOutboxCollection Capture)> _collections = new();

    public MetricsOutboxCollection Collection(String name)
        => GetOrCreate(name).Capture;

    protected override Object Invoke(MethodInfo? targetMethod, Object?[]? args)
        => targetMethod?.Name == "GetCollection" && args is [String name, _]
            ? GetOrCreate(name).Proxy
            : throw new NotSupportedException(targetMethod?.Name);

    private (IMongoCollection<OutboxMessage> Proxy, MetricsOutboxCollection Capture) GetOrCreate(String name)
        => _collections.GetOrAdd(name, _ =>
        {
            var proxy = Create<IMongoCollection<OutboxMessage>, MetricsOutboxCollection>();
            // DispatchProxy.Create returns an instance of the proxy type, so the cast always succeeds.
            // ReSharper disable once SuspiciousTypeConversion.Global
            return (proxy, (MetricsOutboxCollection)proxy);
        });
}

/// <summary>
/// Wires one outbox processor to scripted collection and publisher doubles and collects its metrics.
/// </summary>
internal sealed class OutboxMetricsFixture : IAsyncDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private readonly ServiceProvider _provider;

    public OutboxMetricsFixture(Int32? maxRetries = 3)
    {
        var database = DispatchProxy.Create<IMongoDatabase, MetricsOutboxDatabase>();
        // DispatchProxy.Create returns an instance of the proxy type, so the cast always succeeds.
        // ReSharper disable once SuspiciousTypeConversion.Global
        var databaseProxy = (MetricsOutboxDatabase)database;
        Destination = $"metrics-{Guid.NewGuid():N}";
        Collection = databaseProxy.Collection(Destination);
        Time = new FakeTimeProvider(Start);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMongoHelper>(new ProcessingFilterMongoHelper(database));
        services.AddSingleton<TimeProvider>(Time);
        new MongoBuilder(services).WithOutbox(builder =>
        {
            builder.WithPublisher(_ => Publisher, ServiceLifetime.Singleton)
                   .WithMessage<TestPayload>()
                   .WithCollectionName(Destination)
                   .WithPollingInterval(TimeSpan.FromHours(1));
            if (maxRetries.HasValue)
            {
                builder.WithMaxRetries(maxRetries.Value);
            }
        });
        _provider = services.BuildServiceProvider();
        Processor = _provider.GetRequiredService<IOutboxProcessor>();
    }

    public MetricsOutboxCollection Collection { get; }

    public String Destination { get; }

    public IReadOnlyList<OutboxMetricMeasurement> Measurements => Metrics.For(Destination);

    public OutboxMetricsCollector Metrics { get; } = new();

    public IOutboxProcessor Processor { get; }

    public ScriptedOutboxPublisher Publisher { get; } = new();

    public FakeTimeProvider Time { get; }

    /// <summary>
    /// Starts the processor, waits for the scripted point and stops it. Stopping awaits the processing loop, so
    /// everything the processor records after reaching that point is observable once this method returns.
    /// </summary>
    public async Task RunUntilAsync(Task reached)
    {
        try
        {
            await Processor.StartAsync();
            await reached.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await Processor.StopAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Metrics.Dispose();
        await _provider.DisposeAsync();
    }
}
