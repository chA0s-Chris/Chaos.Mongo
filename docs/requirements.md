# Requirements

This page states the platform and MongoDB server versions Chaos.Mongo 1.0 needs. For the guarantees these
requirements change under, see the [compatibility policy](compatibility-policy.md); if you are upgrading from 0.x,
see [Migrating from 0.x](migrating-from-0.x.md).

## .NET

.NET 10.

## MongoDB server

The [Event Store](event-store.md) and the [Transactional Outbox](transactional-outbox.md), and any code using
`ExecuteInTransactionAsync` or a transaction started directly, need a replica set or a sharded cluster (a
single-node replica set suffices), because [transactions](transactions.md) require one. Other features, including
migrations and the optional `TryStartTransactionAsync`/`TryStartSessionAsync` fallback, run against a standalone
server too.

The minimum supported server line is **MongoDB 7.0**. Some features need a newer server:

| Feature | Minimum server version |
| --- | --- |
| EventStore `WithBulkWriteOptimization()` | MongoDB 8.0 |
| Tamper-evidence anchoring's change-stream recovery scan | MongoDB 5.1 (already satisfied by the baseline above) |
| `IEventStoreIntegrity<TAggregate>.VerifyDerivedDataAsync`'s snapshot session | MongoDB 5.0 (already satisfied by the baseline above) |

## MongoDB.Driver

The minimum `MongoDB.Driver` version is **3.9.0**: older 3.x releases depend on a vulnerable Snappier or
SharpCompress version. `ClientBulkWriteException`, which the package also uses, has been available since 3.1.0 and
does not independently raise this floor.

## Tested server versions

The test and benchmark suites run against the MongoDB 7.0.x, 8.0.x and 9.0.x lines; CI covers all three.
