// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.TestSupport;

using MongoDB.Driver;
using NUnit.Framework;
using System.Collections.Concurrent;

/// <summary>
/// Names the databases that integration tests create on the shared MongoDB server and drops them when the test that
/// created them ends, so the server does not accumulate thousands of databases over a run. Databases named outside
/// a test, for example in a fixture's <c>[OneTimeSetUp]</c>, are dropped at the end of the run. Drops run in the
/// background, because dropping a database on a replica set takes long enough to slow every test down.
/// </summary>
/// <remarks>
/// <see cref="DropTestDatabasesAttribute"/> does the dropping after each test; the assembly's setup fixture points
/// this class at its server with <see cref="UseServer"/> and calls <see cref="DropRemainingAndFindLeftoversAsync"/>
/// before it stops the server.
/// </remarks>
public static class TestDatabases
{
    private static readonly ConcurrentQueue<String> FailedDrops = new();
    private static readonly ConcurrentDictionary<String, ConcurrentQueue<String>> NamesByOwner = new();
    private static readonly ConcurrentQueue<Task> PendingDrops = new();
    private static readonly String[] ServerDatabases = ["admin", "config", "local"];
    private static IMongoClient? Client;

    /// <summary>
    /// Starts dropping every database registered by the test or suite with the given NUnit id in the background.
    /// </summary>
    public static void DropOwnedBy(String ownerId)
    {
        if (!NamesByOwner.TryRemove(ownerId, out var names) || Client is not { } client)
            return;

        foreach (var name in names.Distinct())
        {
            PendingDrops.Enqueue(Task.Run(() => DropAsync(client, name)));
        }
    }

    /// <summary>
    /// Drops the databases still registered and waits for every drop, then returns the drops that failed together
    /// with the databases left on the server: those were created by a test that did not name them through this
    /// class, or could not be dropped.
    /// </summary>
    public static async Task<TestDatabaseCleanup> DropRemainingAndFindLeftoversAsync()
    {
        foreach (var ownerId in NamesByOwner.Keys)
        {
            DropOwnedBy(ownerId);
        }

        await Task.WhenAll(PendingDrops);
        if (Client is not { } client)
            return new TestDatabaseCleanup([], [.. FailedDrops]);

        var names = await (await client.ListDatabaseNamesAsync()).ToListAsync();
        return new TestDatabaseCleanup(names.Except(ServerDatabases, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(), [.. FailedDrops]);
    }

    /// <summary>
    /// Returns a new database name made of <paramref name="prefix"/> and a random suffix, registered for dropping when
    /// the current test ends.
    /// </summary>
    public static String NewName(String prefix) => Register($"{prefix}_{Guid.NewGuid():N}");

    /// <summary>
    /// Registers <paramref name="databaseName"/> for dropping when the current test ends and returns it.
    /// </summary>
    public static String Register(String databaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

        NamesByOwner.GetOrAdd(TestContext.CurrentContext.Test.ID, _ => new ConcurrentQueue<String>()).Enqueue(databaseName);
        return databaseName;
    }

    /// <summary>
    /// Points the drops at the server of this test process.
    /// </summary>
    public static void UseServer(String connectionString) => Client = new MongoClient(connectionString);

    /// <summary>
    /// Drops one database and records a failure instead of throwing, so one failed drop neither stops the others nor
    /// hides the leftover databases at the end of the run.
    /// </summary>
    private static async Task DropAsync(IMongoClient client, String name)
    {
        try
        {
            await client.DropDatabaseAsync(name);
        }
        catch (Exception exception)
        {
            FailedDrops.Enqueue($"{name} ({exception.Message})");
        }
    }
}

/// <summary>
/// The result of dropping the test databases at the end of a run.
/// </summary>
/// <param name="Leftovers">The databases still on the server, apart from its own.</param>
/// <param name="FailedDrops">The databases whose drop failed, each with the error.</param>
public sealed record TestDatabaseCleanup(IReadOnlyList<String> Leftovers, IReadOnlyList<String> FailedDrops);
