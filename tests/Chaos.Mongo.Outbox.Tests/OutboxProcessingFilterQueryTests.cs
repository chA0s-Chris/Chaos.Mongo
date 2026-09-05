// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox.Tests;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using NUnit.Framework;
using System.Reflection;

public class OutboxProcessingFilterQueryTests
{
    [Test]
    public async Task StartAsync_NoProcessingFilter_PreservesSelectionAndClaim()
        => await AssertQueryContractAsync(null);

    [TestCase(false)]
    [TestCase(true)]
    public async Task StartAsync_ProcessingFilter_ConjoinsFilterInSelectionAndClaim(Boolean compound)
    {
        var filters = Builders<OutboxMessage>.Filter;
        var filter = filters.Eq(message => message.Type, "Allowed");
        if (compound)
        {
            filter &= filters.Eq(message => message.CorrelationId, "Selected") |
                      filters.Eq(message => message.RetryCount, 2);
        }

        await AssertQueryContractAsync(filter);
    }

    private static async Task AssertQueryContractAsync(FilterDefinition<OutboxMessage>? filter)
    {
        var instance = DispatchProxy.Create(typeof(IMongoDatabase), typeof(ProcessingFilterDatabase));
        var database = (IMongoDatabase)instance;
        var capture = ((ProcessingFilterDatabase)instance).Capture;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMongoHelper>(new ProcessingFilterMongoHelper(database));
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero));
        services.AddSingleton<TimeProvider>(clock);
        new MongoBuilder(services).WithOutbox(builder =>
        {
            builder.WithPublisher<TestOutboxPublisher>(ServiceLifetime.Singleton)
                   .WithMessage<TestPayload>()
                   .WithBatchSize(3)
                   .WithPollingInterval(TimeSpan.FromHours(1));
            if (filter is not null)
                builder.WithProcessingFilter(filter);
        });
        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<OutboxOptions>();
        options.ProcessingFilter.Should().BeSameAs(filter);
        var processor = provider.GetRequiredService<IOutboxProcessor>();
        try
        {
            await processor.StartAsync();
            await capture.ClaimAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await processor.StopAsync();
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var expected = new BsonDocument
        {
            { "State", (Int32)OutboxMessageState.Pending },
            {
                "$and", new BsonArray
                {
                    new BsonDocument("$or", new BsonArray
                    {
                        new BsonDocument("NextAttemptUtc", BsonNull.Value),
                        new BsonDocument("NextAttemptUtc", new BsonDocument("$lte", now))
                    }),
                    new BsonDocument("$or", new BsonArray
                    {
                        new BsonDocument("IsLocked", false),
                        new BsonDocument("LockedUtc", new BsonDocument("$lte", now - options.LockTimeout))
                    })
                }
            }
        };
        var expectedConjuncts = Conjuncts(expected).ToList();
        if (filter is not null)
            expectedConjuncts.AddRange(Conjuncts(Render(filter)));

        capture.SelectionFilter.Should().NotBeNull();
        capture.ClaimFilter.Should().NotBeNull();
        Conjuncts(Render(capture.SelectionFilter)).Should().BeEquivalentTo(expectedConjuncts);
        expectedConjuncts.Add(new BsonDocument("_id", capture.Message.Id));
        Conjuncts(Render(capture.ClaimFilter)).Should().BeEquivalentTo(expectedConjuncts);
        capture.SelectionOptions.Should().NotBeNull();
        capture.SelectionOptions.Limit.Should().Be(3);
        capture.SelectionOptions.Sort.Render(new RenderArgs<OutboxMessage>(
                                                 BsonSerializer.SerializerRegistry.GetSerializer<OutboxMessage>(), BsonSerializer.SerializerRegistry))
               .Should().Equal(new BsonDocument
               {
                   { "NextAttemptUtc", 1 },
                   { "LockedUtc", 1 },
                   { "_id", 1 }
               });
        ((TestOutboxPublisher)provider.GetRequiredService<IOutboxPublisher>()).PublishedMessages.Should().BeEmpty();
    }

    // Flatten only conjunctions. An application filter nested in a disjunction cannot satisfy this contract.
    private static IEnumerable<BsonDocument> Conjuncts(BsonDocument document)
        => document.Elements.SelectMany(element => element.Name == "$and"
                                            ? element.Value.AsBsonArray.SelectMany(value => Conjuncts(value.AsBsonDocument))
                                            : [new BsonDocument(element)]);

    private static BsonDocument Render(FilterDefinition<OutboxMessage> filter)
        => filter.Render(new RenderArgs<OutboxMessage>(
                             BsonSerializer.SerializerRegistry.GetSerializer<OutboxMessage>(), BsonSerializer.SerializerRegistry));
}

internal class ProcessingFilterCollection : DispatchProxy
{
    public ProcessingFilterCollection()
    {
        ClaimAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Message = new OutboxMessage
        {
            Id = ObjectId.GenerateNewId(),
            Type = "Allowed"
        };
    }

    public TaskCompletionSource ClaimAttempted { get; }
    public FilterDefinition<OutboxMessage>? ClaimFilter { get; private set; }

    public OutboxMessage Message { get; }

    public FilterDefinition<OutboxMessage>? SelectionFilter { get; private set; }
    public FindOptions<OutboxMessage, OutboxMessage>? SelectionOptions { get; private set; }

    protected override Object? Invoke(MethodInfo? targetMethod, Object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        switch (targetMethod.Name)
        {
            case "get_DocumentSerializer":
                return BsonSerializer.SerializerRegistry.GetSerializer<OutboxMessage>();
            case "get_Settings":
                return new MongoCollectionSettings();
            case "FindAsync" when args is [FilterDefinition<OutboxMessage> filter, FindOptions<OutboxMessage, OutboxMessage> options, _]:
                SelectionFilter = filter;
                SelectionOptions = options;
                return Task.FromResult<IAsyncCursor<OutboxMessage>>(new ProcessingFilterCursor(Message));
            case "FindOneAndUpdateAsync" when args is [FilterDefinition<OutboxMessage> filter, _, _, _]:
                ClaimFilter = filter;
                ClaimAttempted.TrySetResult();
                return Task.FromResult<OutboxMessage?>(null);
            default:
                throw new NotSupportedException(targetMethod.Name);
        }
    }
}

internal sealed class ProcessingFilterCursor : IAsyncCursor<OutboxMessage>
{
    private Boolean _returned;

    public ProcessingFilterCursor(OutboxMessage message) => Current = [message];

    public IEnumerable<OutboxMessage> Current { get; }

    public Boolean MoveNext(CancellationToken cancellationToken = default)
    {
        if (_returned)
            return false;
        _returned = true;
        return true;
    }

    public Task<Boolean> MoveNextAsync(CancellationToken cancellationToken = default) => Task.FromResult(MoveNext(cancellationToken));
    public void Dispose() { }
}

internal class ProcessingFilterDatabase : DispatchProxy
{
    public ProcessingFilterDatabase()
    {
        var instance = Create(typeof(IMongoCollection<OutboxMessage>), typeof(ProcessingFilterCollection));
        Collection = (IMongoCollection<OutboxMessage>)instance;
        Capture = (ProcessingFilterCollection)instance;
    }

    public ProcessingFilterCollection Capture { get; }

    public IMongoCollection<OutboxMessage> Collection { get; }

    protected override Object Invoke(MethodInfo? targetMethod, Object?[]? args)
        => targetMethod?.Name == "GetCollection" ? Collection : throw new NotSupportedException(targetMethod?.Name);
}

internal sealed class ProcessingFilterMongoHelper : IMongoHelper
{
    public ProcessingFilterMongoHelper(IMongoDatabase database) => Database = database;
    public IMongoClient Client => throw new NotSupportedException();

    public IMongoDatabase Database { get; }
    public IMongoCollection<TDocument> GetCollection<TDocument>(MongoCollectionSettings? settings = null) => throw new NotSupportedException();

    public Task<IMongoLock?> TryAcquireLockAsync(String lockName, TimeSpan? leaseTime = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
