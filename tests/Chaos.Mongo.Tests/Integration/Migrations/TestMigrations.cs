// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.

// ReSharper disable InconsistentNaming
namespace Chaos.Mongo.Tests.Integration.Migrations;

using Chaos.Mongo.Migrations;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using System.Collections.Concurrent;

internal static class MigrationTestTracker
{
    private static readonly ConcurrentBag<String> _executed = [];
    private static readonly ConcurrentQueue<String> _executionOrder = new();

    public static IReadOnlyCollection<String> ExecutedMigrations => _executed.ToList();

    public static IReadOnlyList<String> ExecutionOrder => _executionOrder.ToList();

    public static void Reset()
    {
        _executed.Clear();
        _executionOrder.Clear();
    }

    public static void Track(String migrationId)
    {
        _executed.Add(migrationId);
        _executionOrder.Enqueue(migrationId);
    }
}

internal class TestDocument
{
    [BsonId]
    public ObjectId Id { get; init; }

    public String Name { get; init; } = String.Empty;
}

internal class AuditLog
{
    public String Action { get; init; } = String.Empty;

    [BsonId]
    public ObjectId Id { get; init; }

    public DateTime Timestamp { get; init; }
}

internal class Migration_001_CreateTestDocumentsIndex : IMongoMigration
{
    public String Description => "Creates an index on TestDocuments.Name";

    public String Id => "001_CreateTestDocumentsIndex";

    public async Task ApplyAsync(IMongoHelper mongoHelper, IClientSessionHandle? session = null, CancellationToken cancellationToken = default)
    {
        MigrationTestTracker.Track(Id);

        var collection = mongoHelper.GetCollection<TestDocument>();
        await collection.Indexes.CreateOneAsync(
            new CreateIndexModel<TestDocument>(
                Builders<TestDocument>.IndexKeys.Ascending(x => x.Name)
            ),
            cancellationToken: cancellationToken
        );
    }
}

internal class Migration_002_CreateAuditLogsIndex : IMongoMigration
{
    public String Description => "Creates an index on AuditLogs.Timestamp";

    public String Id => "002_CreateAuditLogsIndex";

    public async Task ApplyAsync(IMongoHelper mongoHelper, IClientSessionHandle? session = null, CancellationToken cancellationToken = default)
    {
        MigrationTestTracker.Track(Id);

        var collection = mongoHelper.GetCollection<AuditLog>();
        await collection.Indexes.CreateOneAsync(
            new CreateIndexModel<AuditLog>(
                Builders<AuditLog>.IndexKeys.Descending(x => x.Timestamp)
            ),
            cancellationToken: cancellationToken
        );
    }
}

internal class Migration_001_First : IMongoMigration
{
    public String Id => "001_First";

    public Task ApplyAsync(IMongoHelper mongoHelper, IClientSessionHandle? session = null, CancellationToken cancellationToken = default)
    {
        MigrationTestTracker.Track(Id);
        return Task.CompletedTask;
    }
}

internal class Migration_002_Second : IMongoMigration
{
    public String Id => "002_Second";

    public Task ApplyAsync(IMongoHelper mongoHelper, IClientSessionHandle? session = null, CancellationToken cancellationToken = default)
    {
        MigrationTestTracker.Track(Id);
        return Task.CompletedTask;
    }
}

internal class Migration_003_Third : IMongoMigration
{
    public String Id => "003_Third";

    public Task ApplyAsync(IMongoHelper mongoHelper, IClientSessionHandle? session = null, CancellationToken cancellationToken = default)
    {
        MigrationTestTracker.Track(Id);
        return Task.CompletedTask;
    }
}

internal class IdempotentTestMigration : IMongoMigration
{
    public String Description => "A simple idempotent test migration";

    public String Id => "999_IdempotentTest";

    public Task ApplyAsync(IMongoHelper mongoHelper, IClientSessionHandle? session = null, CancellationToken cancellationToken = default)
    {
        MigrationTestTracker.Track(Id);
        return Task.CompletedTask;
    }
}

internal class FastTestMigration : IMongoMigration
{
    public String Id => "001_FastTest";

    public Task ApplyAsync(IMongoHelper mongoHelper, IClientSessionHandle? session = null, CancellationToken cancellationToken = default)
    {
        MigrationTestTracker.Track(nameof(FastTestMigration));
        return Task.CompletedTask;
    }
}

internal class CancellingTestMigration : IMongoMigration
{
    private readonly CancellationTokenSource _cancellationTokenSource;

    public CancellingTestMigration(CancellationTokenSource cancellationTokenSource)
    {
        ArgumentNullException.ThrowIfNull(cancellationTokenSource);
        _cancellationTokenSource = cancellationTokenSource;
    }

    public String Description => "Cancels the run from inside a migration";

    public String Id => "002_CancellingTest";

    public async Task ApplyAsync(IMongoHelper mongoHelper, IClientSessionHandle? session = null, CancellationToken cancellationToken = default)
    {
        MigrationTestTracker.Track(nameof(CancellingTestMigration));
        await _cancellationTokenSource.CancelAsync();
        cancellationToken.ThrowIfCancellationRequested();
    }
}

internal class ThirdTestMigration : IMongoMigration
{
    public String Id => "003_ThirdTest";

    public Task ApplyAsync(IMongoHelper mongoHelper, IClientSessionHandle? session = null, CancellationToken cancellationToken = default)
    {
        MigrationTestTracker.Track(nameof(ThirdTestMigration));
        return Task.CompletedTask;
    }
}

internal class ConcurrentTestMigration : IMongoMigration
{
    public String Description => "Migration to test concurrent execution locking";

    public String Id => "888_ConcurrentTest";

    public async Task ApplyAsync(IMongoHelper mongoHelper, IClientSessionHandle? session = null, CancellationToken cancellationToken = default)
    {
        MigrationTestTracker.Track(nameof(ConcurrentTestMigration));
        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
    }
}

internal class VerySlowTestMigration : IMongoMigration
{
    public String Id => "777_VerySlowTest";

    public async Task ApplyAsync(IMongoHelper mongoHelper, IClientSessionHandle? session = null, CancellationToken cancellationToken = default)
    {
        MigrationTestTracker.Track(nameof(VerySlowTestMigration));
        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
    }
}

internal class FailingTestMigration : IMongoMigration
{
    public String Description => "Migration that intentionally fails";

    public String Id => "666_FailingTest";

    public async Task ApplyAsync(IMongoHelper mongoHelper, IClientSessionHandle? session = null, CancellationToken cancellationToken = default)
    {
        MigrationTestTracker.Track(Id);

        var collection = mongoHelper.GetCollection<TestDocument>();

        if (session is not null)
        {
            await collection.InsertOneAsync(
                session,
                new()
                {
                    Id = ObjectId.GenerateNewId(),
                    Name = "Test"
                },
                cancellationToken: cancellationToken);
        }
        else
        {
            await collection.InsertOneAsync(
                new()
                {
                    Id = ObjectId.GenerateNewId(),
                    Name = "Test"
                },
                cancellationToken: cancellationToken);
        }

        throw new InvalidOperationException("Migration intentionally failed");
    }
}

internal class SessionAwareMigration : IMongoMigration
{
    private static Boolean? _sessionWasNull;

    public static Boolean SessionWasNull => _sessionWasNull ?? throw new InvalidOperationException("Migration has not been executed yet");

    public String Description => "Migration that tracks whether it received a session";

    public String Id => "555_SessionAware";

    public static void Reset() => _sessionWasNull = null;

    public Task ApplyAsync(IMongoHelper mongoHelper, IClientSessionHandle? session = null, CancellationToken cancellationToken = default)
    {
        MigrationTestTracker.Track(Id);
        _sessionWasNull = session is null;
        return Task.CompletedTask;
    }
}
