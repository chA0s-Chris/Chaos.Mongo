// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.LoadTests;

using MongoDB.Bson;
using MongoDB.Driver;

/// <summary>
/// Measures the oplog bytes written per append, including what the consumer writes to acknowledge candidates.
/// The capped oplog stops growing once it is full, so the entries of the measured timestamp range are summed.
/// </summary>
internal static class OplogGrowthMeasurement
{
    public static async Task<OplogGrowthResult> RunAsync(String connectionString, LoadTestMode mode, Int32 appends)
    {
        await using var store = await LoadTestStore.CreateAsync(connectionString, mode, false);
        var streams = await store.SeedStreamsAsync(100);
        await store.DrainUntilIdleAsync();
        var oplog = store.MongoHelper.Client.GetDatabase("local").GetCollection<BsonDocument>("oplog.rs");

        var from = await LatestTimestampAsync(oplog);
        for (var append = 0; append < appends; append++)
        {
            var aggregateId = streams[append % streams.Count];
            await store.Store.AppendEventsAsync([LoadTestAdjustedEvent.Create(aggregateId, 2 + (append / streams.Count))]);
        }

        if (store.CandidateSource is not null)
        {
            await store.DrainAsync(appends, TimeSpan.FromMinutes(5));
        }

        var to = await LatestTimestampAsync(oplog);
        var totals = await oplog.Aggregate()
                                .Match(new BsonDocument
                                {
                                    {
                                        "ts", new BsonDocument
                                        {
                                            { "$gt", from },
                                            { "$lte", to }
                                        }
                                    },
                                    { "op", new BsonDocument("$ne", "n") }
                                })
                                .Group(new BsonDocument
                                {
                                    { "_id", BsonNull.Value },
                                    { "bytes", new BsonDocument("$sum", new BsonDocument("$bsonSize", "$$ROOT")) },
                                    { "entries", new BsonDocument("$sum", 1) }
                                })
                                .FirstOrDefaultAsync();

        var bytes = totals?["bytes"].ToInt64() ?? 0;
        var entries = totals?["entries"].ToInt64() ?? 0;
        return new OplogGrowthResult((Double)bytes / appends, (Double)entries / appends);
    }

    private static async Task<BsonTimestamp> LatestTimestampAsync(IMongoCollection<BsonDocument> oplog)
    {
        var latest = await oplog.Find(FilterDefinition<BsonDocument>.Empty)
                                .Sort(new BsonDocument("$natural", -1))
                                .Limit(1)
                                .FirstAsync();
        return latest["ts"].AsBsonTimestamp;
    }
}
