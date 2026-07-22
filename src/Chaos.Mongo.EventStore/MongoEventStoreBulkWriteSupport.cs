// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore;

using MongoDB.Bson;
using MongoDB.Driver;
using System.Runtime.CompilerServices;

internal static class MongoEventStoreBulkWriteSupport
{
    private static readonly ConditionalWeakTable<IMongoClient, Lazy<Task<Version>>> _serverVersions = new();

    public static async Task EnsureSupportedAsync(
        IMongoClient client,
        IMongoDatabase database,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var cachedVersion = _serverVersions.GetValue(
            client,
            _ => new(() => GetServerVersionAsync(database)));

        Version serverVersion;
        try
        {
            serverVersion = await cachedVersion.Value.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested &&
                                                 !cachedVersion.Value.IsFaulted)
        {
            // This caller's own wait was canceled while the shared lookup is still healthy,
            // so leave the cache entry in place for the other callers awaiting it.
            throw;
        }
        catch
        {
            // The shared lookup itself failed: evict it so the next caller retries. Only remove
            // the entry we awaited, in case a concurrent caller already installed a fresh one.
            if (_serverVersions.TryGetValue(client, out var currentVersion) &&
                ReferenceEquals(currentVersion, cachedVersion))
            {
                _serverVersions.Remove(client);
            }

            throw;
        }

        if (serverVersion.Major < 8)
        {
            throw new NotSupportedException(
                $"MongoDB 8.0 or later is required when EventStore bulk-write optimization is enabled. " +
                $"The connected server reports version {serverVersion}.");
        }
    }

    private static async Task<Version> GetServerVersionAsync(IMongoDatabase database)
    {
        var buildInfo = await database.RunCommandAsync(
            new BsonDocumentCommand<BsonDocument>(new("buildInfo", 1)));
        return ParseServerVersion(buildInfo);
    }

    private static Version ParseServerVersion(BsonDocument buildInfo)
    {
        if (buildInfo.TryGetValue("versionArray", out var versionArrayValue) &&
            versionArrayValue is BsonArray { Count: >= 2 } versionArray &&
            versionArray[0].IsNumeric &&
            versionArray[1].IsNumeric)
        {
            var build = versionArray.Count > 2 && versionArray[2].IsNumeric
                ? versionArray[2].ToInt32()
                : 0;
            return new(versionArray[0].ToInt32(), versionArray[1].ToInt32(), build);
        }

        if (buildInfo.TryGetValue("version", out var versionValue) &&
            TryParseVersion(versionValue.AsString, out var parsedVersion))
        {
            return parsedVersion;
        }

        throw new InvalidOperationException(
            "EventStore bulk-write optimization was enabled, but the MongoDB server version could not be determined.");
    }

    private static Boolean TryParseVersion(String rawVersion, out Version version)
    {
        var parts = rawVersion.Split('.');
        var numericParts = new List<Int32>(parts.Length);

        foreach (var part in parts)
        {
            var digits = new String(part.TakeWhile(Char.IsDigit).ToArray());
            if (digits.Length == 0 || !Int32.TryParse(digits, out var numericPart))
            {
                break;
            }

            numericParts.Add(numericPart);
        }

        if (numericParts.Count >= 2)
        {
            version = numericParts.Count >= 3
                ? new(numericParts[0], numericParts[1], numericParts[2])
                : new(numericParts[0], numericParts[1]);
            return true;
        }

        version = new();
        return false;
    }
}
