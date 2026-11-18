# Production Checklist

Go through this list before a deployment that uses Chaos.Mongo goes to production. For each package, it states which
indexes are created and how, which data expires or is kept, and which meters to export. It links to the detailed
sections instead of repeating them.

## All packages

- [ ] **Pin collection names.** Default names derive from CLR type names, so a rename moves data to an empty
  collection. Pin every name explicitly; see [Collection names](configuration.md#collection-names).
- [ ] **Decide who creates indexes.** Run configurators at startup with `MongoOptions.RunConfiguratorsOnStartup`
  or in a deployment step with `IMongoConfiguratorRunner.RunConfiguratorsAsync`. Grant the identity that reconciles
  indexes permission to create, list and drop them on the affected collections. To run the application with a role
  that lacks index privileges, provision its indexes before deployment and disable every runtime path that manages
  them. For event-store appends, this includes setting `WithoutConcurrencyIndexVerification()`. For outboxes,
  start the processor manually after provisioning because `WithAutoStartProcessor()` also runs configurators.
  Queue subscriptions always reconcile their own indexes at startup. See [Database Configurators](configurators.md).
- [ ] **Create the indexes your own queries need**, for example on read models and mapped collections, with a
  migration or a configurator. `CreateOneOrUpdateAsync` replaces an index whose definition changed; see
  [Index Management](index-management.md).
- [ ] **Export the meters** listed below to your monitoring backend, for example by adding each meter name to your
  OpenTelemetry `MeterProviderBuilder` with `AddMeter`. Duration histograms record seconds.

## Chaos.Mongo

**Indexes**

| Collection | Index | Created |
|---|---|---|
| Every queue's collection | `IX_Queue_Processing`, which serves acquisition; `IX_Queue_ClosedUtc_TTL` while a retention period is configured | By the queue's subscription when it starts, through `WithAutoStartSubscription()` or `IMongoQueue<TPayload>.StartSubscriptionAsync` |
| Your mapped collections | Whatever your migrations and configurators create | By your migrations and configurators |

Grant each queue worker `createIndex` and `listIndexes` on its queue collection even when the indexes already
exist. Reconciliation also needs `dropIndex` to remove obsolete or conflicting indexes and to remove the TTL
index when immediate deletion is configured.

**Retention**

| Data | Kept | Configure with |
|---|---|---|
| Successfully processed queue items | One hour (`MongoDefaults.QueueRetentionPeriod`), then removed by the TTL index on `ClosedUtc` | `MongoQueueBuilder<TPayload>.WithRetentionPeriod(...)`, or `WithImmediateDelete()` to delete them right away |
| Terminal queue items (attempts exhausted) | Indefinitely, for inspection and dead-letter handling | Inspect and remove them yourself |
| Migration history (`_migrations`) | Indefinitely; it records which migrations ran | — |

**Metrics:** the meter `MongoQueueMetrics.MeterName` (`Chaos.Mongo.Queues`); see [Diagnostics](queues.md#diagnostics).
Alert on a sustained rate of `chaos.mongo.queue.processing.failed`, on any increase of
`chaos.mongo.queue.lock.recovered` (a handler crashed or outlived its lease), and on a high percentile of
`chaos.mongo.queue.processing.queue_age`. Check for terminal items as well; no instrument counts them.

## Chaos.Mongo.EventStore

**Indexes**

| Collection | Index | Created |
|---|---|---|
| `{prefix}_Events` | The unique `(AggregateId, Version)` index, which optimistic concurrency relies on | By configurators, and verified (or created) by the first append on each connection unless `WithoutConcurrencyIndexVerification()` is set; see [Builder Options](event-store.md#builder-options) |
| `{prefix}_Checkpoints` | `(_id.AggregateId, _id.Version)` and `(_id.AggregateId, Revision)` | By configurators when checkpoints are enabled with `WithCheckpoints(...)` |
| `{prefix}` (read model) | None beyond `_id` | Create the indexes that your queries through `IAggregateRepository<TAggregate>.Collection` need |
| Anchors, with [integrity anchoring](event-store.md#anchoring) | `Sequence_Unique` for assigned sequence numbers, `Status`, and the partial indexes `Unnotified_Sequence` and `Unconfirmed_Sequence` | By the anchoring configurator |
| Anchor leaves | `AggregateType_with_AggregateId_with_AnchorSequence_with_Version`, `AnchorId_with_Index_Unique`, and `AggregateId_with_AggregateType` | By the anchoring configurator |

With restricted MongoDB roles, follow [MongoDB Roles](event-store.md#mongodb-roles), which also covers the
`MongoConcurrencyIndexMissingException` an application role without `createIndex` otherwise gets.

**Retention.** The event store retains events, read models and checkpoints indefinitely. Use
[`IPersonalDataEraser<TAggregate>`](event-store.md#erasing-personal-data) to remove personal data.
The anchoring worker updates anchor status and timestamps. Completed anchors, their leaves and their evidence are
retained indefinitely. During cleanup the worker deletes the leaves of discarded attempts and removes attempt
records discarded more than 24 hours ago.

**Metrics.** Export `IntegrityMetrics.MeterName` (`Chaos.Mongo.EventStore.Integrity`) when anchoring or the
retroactive sealing sweep is enabled. Alert on increases in `chaos.mongo.eventstore.anchoring.malformed_candidates_skipped`
for either feature. With anchoring enabled, also alert on increases in `chaos.mongo.eventstore.anchoring.delay_exceeded`,
`chaos.mongo.eventstore.anchoring.revocation_confirmation_delay_exceeded`, and
`chaos.mongo.eventstore.anchoring.revoked_timestamps`. See [Anchoring](event-store.md#anchoring).

## Chaos.Mongo.Outbox

**Indexes**

| Collection | Index | Created |
|---|---|---|
| Every outbox's collection | `IX_Outbox_Polling`; `IX_Outbox_ProcessedUtc_TTL` while a retention period is configured; `IX_Outbox_FailedUtc_TTL` while `WithExpireFailedMessages()` is configured as well | By configurators: `RunConfiguratorsOnStartup`, `IOutboxConfiguratorRunner.RunConfiguratorsAsync`, or `WithAutoStartProcessor()` for the outboxes it starts; see [Processor Startup](transactional-outbox.md#processor-startup) |

**Retention**

| Data | Kept | Configure with |
|---|---|---|
| `Processed` messages | Indefinitely, unless a retention period is configured | `OutboxBuilder.WithRetentionPeriod(...)` |
| `Failed` messages | Indefinitely, even with a retention period | `WithExpireFailedMessages()` together with `WithRetentionPeriod(...)` |

A message only reaches `Failed` with a finite `WithMaxRetries(...)`; by default, delivery is retried indefinitely. See
[Retention and Cleanup](transactional-outbox.md#retention-and-cleanup).

**Metrics:** the meter `OutboxMetrics.MeterName` (`Chaos.Mongo.Outbox`), tagged per outbox with `outbox.destination`;
see [Metrics](transactional-outbox.md#metrics) and set up the [Suggested Alerts](transactional-outbox.md#suggested-alerts).

## Related documentation

- [Configuration](configuration.md)
- [Compatibility Policy](compatibility-policy.md)
- [Requirements](requirements.md)
