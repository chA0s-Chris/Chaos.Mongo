// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox.Tests;

using Chaos.Mongo.Configuration;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using NUnit.Framework;
using System.Collections.Concurrent;
using System.Reflection;

public class TypedOutboxLifecycleTests
{
    [Test]
    public async Task ConfigureAsync_CanceledWaiter_DoesNotCancelSuccessfulInitializer()
    {
        var database = DispatchProxy.Create<IMongoDatabase, LifecycleDatabase>();
        var capture = (LifecycleDatabase)database;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        capture.BeforeIndex = (_, token) => release.Task.WaitAsync(token);
        using var host = CreateHost(database, false, false);
        var initialize = host.Services.GetRequiredService<IOutboxConfiguratorRunner>().RunAsync();
        using var cancellation = new CancellationTokenSource();
        var waiting = host.Services.GetRequiredService<IOutboxConfiguratorRunner>().RunAsync(cancellation.Token);
        cancellation.Cancel();
        try
        {
            await ((Func<Task>)(() => waiting)).Should().ThrowAsync<OperationCanceledException>();
        }
        finally
        {
            release.TrySetResult();
        }

        await initialize.WaitAsync(TimeSpan.FromSeconds(5));
        await host.Services.GetRequiredService<IOutboxConfiguratorRunner>().RunAsync();
        capture.Collections.Values.Should().OnlyContain(c => c.IndexCalls == 3);
    }

    [Test]
    public async Task RunAsync_ConcurrentInitialization_WaitsAndSharesSuccessfulCompletion()
    {
        var database = DispatchProxy.Create<IMongoDatabase, LifecycleDatabase>();
        var capture = (LifecycleDatabase)database;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        capture.BeforeIndex = async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        using var host = CreateHost(database, false, false);
        var first = host.Services.GetRequiredService<IOutboxConfiguratorRunner>().RunAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = host.Services.GetRequiredService<IMongoConfiguratorRunner>().RunConfiguratorsAsync();
        try
        {
            second.IsCompleted.Should().BeFalse();
        }
        finally
        {
            release.TrySetResult();
        }

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        capture.Collections.Values.Should().OnlyContain(c => c.IndexCalls == 3 && c.Polls == 0);
    }

    [Test]
    public async Task StartAsync_Diagnostics_IncludeOutboxIdentityAndCollection()
    {
        var database = DispatchProxy.Create<IMongoDatabase, LifecycleDatabase>();
        var logs = new OutboxLogCapture();
        using var host = CreateHost(database, false, false, logs);
        await host.StartAsync();
        await host.StopAsync();
        foreach (var (identity, collection) in new[]
                 {
                     ("FirstDestination", "First"),
                     ("SecondDestination", "Second"),
                     ("Default", "Outbox")
                 })
        {
            logs.Entries.Should().Contain(e => e.Category.EndsWith(nameof(OutboxProcessor)) &&
                                               e.Values.Any(v => v.Key == "OutboxIdentity" && Equals(v.Value, identity)) &&
                                               e.Values.Any(v => v.Key == "CollectionName" && Equals(v.Value, collection)));
            logs.Entries.Should().Contain(e => e.Category.EndsWith(nameof(OutboxHostedService)) &&
                                               e.Values.Any(v => v.Key == "OutboxIdentity" && Equals(v.Value, identity)) &&
                                               e.Values.Any(v => v.Key == "CollectionName" && Equals(v.Value, collection)));
        }
    }

    [Test]
    public async Task StartAsync_DifferentPollingIntervals_PollsEachDestinationOnItsOwnSchedule()
    {
        var database = DispatchProxy.Create<IMongoDatabase, LifecycleDatabase>();
        var capture = (LifecycleDatabase)database;
        var time = new FakeTimeProvider();
        using var host = CreateHost(database, false, false, timeProvider: time);
        await host.StartAsync();
        try
        {
            time.Advance(TimeSpan.FromHours(1));
            await capture.Collections["First"].SecondPoll.Task.WaitAsync(TimeSpan.FromSeconds(5));
            capture.Collections["Second"].Polls.Should().Be(1);
            capture.Collections["Outbox"].Polls.Should().Be(1);
            time.Advance(TimeSpan.FromHours(1));
            await capture.Collections["Second"].SecondPoll.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await capture.Collections["Outbox"].SecondPoll.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task StartAsync_MultipleOutboxes_InitializesOnceBeforePolling(Boolean generalStartup, Boolean concurrentStartup)
    {
        var database = DispatchProxy.Create<IMongoDatabase, LifecycleDatabase>();
        var capture = (LifecycleDatabase)database;
        using var host = CreateHost(database, generalStartup, concurrentStartup);
        await host.StartAsync();
        try
        {
            foreach (var name in new[]
                     {
                         "First",
                         "Second",
                         "Outbox"
                     })
            {
                capture.Collections[name].IndexCalls.Should().Be(3);
                capture.Collections[name].Polls.Should().Be(1);
                capture.Collections[name].PolledBeforeInitialization.Should().BeFalse();
                capture.Collections[name].BatchSize.Should().Be(name == "First" ? 1 : 3);
                var filter = capture.Collections[name].SelectionFilter;
                filter.Should().NotBeNull();
                var rendered = filter.Render(new RenderArgs<OutboxMessage>(
                                                 BsonSerializer.SerializerRegistry.GetSerializer<OutboxMessage>(), BsonSerializer.SerializerRegistry));
                var lockCutoff = rendered["$and"].AsBsonArray.SelectMany(v => v.AsBsonDocument.Elements)
                                                 .Where(e => e.Name == "$or").SelectMany(e => e.Value.AsBsonArray)
                                                 .Select(v => v.AsBsonDocument).Single(v => v.Contains("LockedUtc"))["LockedUtc"]["$lte"].ToUniversalTime();
                lockCutoff.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(name == "First" ? -1 : -10), TimeSpan.FromSeconds(10));
            }

            capture.Collections.TryGetValue("Manual", out var manual);
            (manual?.Polls ?? 0).Should().Be(0);
            (manual?.IndexCalls ?? 0).Should().Be(generalStartup ? 3 : 0);

            await host.Services.GetRequiredService<IOutboxConfiguratorRunner>().RunAsync();
            await host.Services.GetRequiredService<IMongoConfiguratorRunner>().RunConfiguratorsAsync();
            capture.Collections.Values.Should().OnlyContain(c => c.IndexCalls == 3);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task StartingAsync_FailedOrCanceledInitialization_DoesNotStartAndCanRetry(Boolean cancel)
    {
        var database = DispatchProxy.Create<IMongoDatabase, LifecycleDatabase>();
        var capture = (LifecycleDatabase)database;
        using var cancellation = new CancellationTokenSource();
        capture.BeforeIndex = (_, _) =>
        {
            if (cancel)
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }

            throw new InvalidOperationException("Index initialization failed");
        };
        using var host = CreateHost(database, false, false);
        var service = (OutboxHostedService)host.Services.GetServices<IHostedService>().Single(s => s is OutboxHostedService);
        var start = () => service.StartingAsync(cancellation.Token);
        if (cancel)
            await start.Should().ThrowAsync<OperationCanceledException>();
        else
            await start.Should().ThrowAsync<InvalidOperationException>();
        capture.Collections.Values.Should().OnlyContain(c => c.Polls == 0);
        capture.BeforeIndex = null;
        await service.StartingAsync(CancellationToken.None);
        await service.StartedAsync(CancellationToken.None);
        await service.StoppingAsync(CancellationToken.None);
        capture.Collections["First"].IndexCalls.Should().Be(4);
        capture.Collections["First"].Polls.Should().Be(1);
    }

    private static void Configure(OutboxBuilder builder, String collection, Boolean autoStart)
    {
        builder.WithCollectionName(collection).WithMessage<TestPayload>().WithPublisher<TestOutboxPublisher>()
               .WithPollingInterval(TimeSpan.FromHours(collection == "First" ? 1 : 2))
               .WithBatchSize(collection == "First" ? 1 : 3)
               .WithLockTimeout(TimeSpan.FromMinutes(collection == "First" ? 1 : 10));
        if (autoStart)
            builder.WithAutoStartProcessor();
    }

    private static IHost CreateHost(IMongoDatabase database, Boolean generalStartup, Boolean concurrentStartup, ILoggerProvider? logs = null, TimeProvider? timeProvider = null)
    {
        return new HostBuilder().ConfigureServices(services =>
        {
            if (logs is not null)
                services.AddLogging(b => b.AddProvider(logs));
            services.Configure<HostOptions>(o => o.ServicesStartConcurrently = concurrentStartup);
            services.AddMongo("mongodb://localhost/Unused", configure: o =>
                    {
                        o.RunConfiguratorsOnStartup = generalStartup;
                        o.ApplyMigrationsOnStartup = false;
                    })
                    .WithOutbox<FirstDestination>(o => Configure(o, "First", true))
                    .WithOutbox<SecondDestination>(o => Configure(o, "Second", true))
                    .WithOutbox(o => Configure(o, "Outbox", true))
                    .WithOutbox<ManualDestination>(o => Configure(o, "Manual", false));
            services.AddSingleton<IMongoHelper>(new ProcessingFilterMongoHelper(database));
            if (timeProvider is not null)
                services.AddSingleton(timeProvider);
        }).Build();
    }
}

internal sealed class ManualDestination
{
    private ManualDestination() { }
}

internal class LifecycleDatabase : DispatchProxy
{
    public Func<String, CancellationToken, Task>? BeforeIndex { get; set; }

    public ConcurrentDictionary<String, LifecycleCollection> Collections { get; } = new();

    protected override Object Invoke(MethodInfo? targetMethod, Object?[]? args)
    {
        if (targetMethod?.Name == "GetCollection" && args is [String name, _])
        {
            return Collections.GetOrAdd(name, _ =>
            {
                var collection = Create<IMongoCollection<OutboxMessage>, LifecycleCollection>();
                var capture = (LifecycleCollection)collection;
                capture.BeforeIndex = token => BeforeIndex?.Invoke(name, token) ?? Task.CompletedTask;
                return capture;
            });
        }

        throw new NotSupportedException(targetMethod?.Name);
    }
}

internal class LifecycleCollection : DispatchProxy
{
    private readonly IMongoIndexManager<OutboxMessage> _indexes;
    private Int32 _indexCalls;
    private Int32 _polls;

    public LifecycleCollection()
    {
        _indexes = Create<IMongoIndexManager<OutboxMessage>, LifecycleIndexes>();
        ((LifecycleIndexes)_indexes).OnIndex = async token =>
        {
            Interlocked.Increment(ref _indexCalls);
            if (BeforeIndex is { } beforeIndex)
                await beforeIndex(token);
        };
    }

    public Int32? BatchSize { get; private set; }

    public Func<CancellationToken, Task>? BeforeIndex { get; set; }
    public Int32 IndexCalls => _indexCalls;
    public Boolean PolledBeforeInitialization { get; private set; }
    public Int32 Polls => _polls;
    public TaskCompletionSource SecondPoll { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public FilterDefinition<OutboxMessage>? SelectionFilter { get; private set; }

    protected override Object Invoke(MethodInfo? targetMethod, Object?[]? args)
    {
        switch (targetMethod?.Name)
        {
            case "get_Indexes": return _indexes;
            case "get_DocumentSerializer": return BsonSerializer.SerializerRegistry.GetSerializer<OutboxMessage>();
            case "get_Settings": return new MongoCollectionSettings();
            case "FindAsync" when args is [FilterDefinition<OutboxMessage> filter, FindOptions<OutboxMessage, OutboxMessage> options, _]:
                SelectionFilter = filter;
                BatchSize = options.Limit;
                if (Interlocked.Increment(ref _polls) == 2)
                    SecondPoll.TrySetResult();
                PolledBeforeInitialization |= IndexCalls < 3;
                return Task.FromResult<IAsyncCursor<OutboxMessage>>(new EmptyOutboxCursor());
            default: throw new NotSupportedException(targetMethod?.Name);
        }
    }
}

internal class LifecycleIndexes : DispatchProxy
{
    public Func<CancellationToken, Task>? OnIndex { get; set; }

    protected override Object Invoke(MethodInfo? targetMethod, Object?[]? args)
    {
        var token = args?.LastOrDefault() is CancellationToken cancellation ? cancellation : CancellationToken.None;
        return targetMethod?.Name switch
        {
            "CreateOneAsync" => CreateIndexAsync(token),
            "DropOneAsync" => OnIndex?.Invoke(token) ?? Task.CompletedTask,
            _ => throw new NotSupportedException(targetMethod?.Name)
        };
    }

    private async Task<String> CreateIndexAsync(CancellationToken token)
    {
        if (OnIndex is { } onIndex)
            await onIndex(token);
        return "Index";
    }
}

internal sealed class EmptyOutboxCursor : IAsyncCursor<OutboxMessage>
{
    public IEnumerable<OutboxMessage> Current => [];
    public Boolean MoveNext(CancellationToken cancellationToken = default) => false;
    public Task<Boolean> MoveNextAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    public void Dispose() { }
}

internal sealed class OutboxLogCapture : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();
    public ConcurrentBag<(String Category, List<KeyValuePair<String, Object?>> Values)> Entries { get; } = [];
    public void Dispose() { }
    public ILogger CreateLogger(String categoryName) => new CapturingOutboxLogger(categoryName, this, () => _scopes);
    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;
}

internal sealed class CapturingOutboxLogger : ILogger
{
    private readonly OutboxLogCapture _capture;
    private readonly String _category;
    private readonly Func<IExternalScopeProvider> _scopes;

    public CapturingOutboxLogger(String category, OutboxLogCapture capture, Func<IExternalScopeProvider> scopes)
    {
        _category = category;
        _capture = capture;
        _scopes = scopes;
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => _scopes().Push(state);
    public Boolean IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, String> formatter)
    {
        var values = new List<KeyValuePair<String, Object?>>();
        _scopes().ForEachScope((scope, list) =>
        {
            if (scope is IEnumerable<KeyValuePair<String, Object?>> scopePairs)
                list.AddRange(scopePairs);
        }, values);
        if (state is IEnumerable<KeyValuePair<String, Object?>> pairs)
            values.AddRange(pairs);
        _capture.Entries.Add((_category, values));
    }
}
