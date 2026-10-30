# Migrating from 0.x

This page lists every breaking change merged since the v0.9.0 release and what to do about it when upgrading to
1.0. Pair it with the [compatibility policy](compatibility-policy.md), which 1.0 starts honoring, and
[requirements](requirements.md) for the platform and MongoDB server versions 1.0 needs.

## Platform: .NET 10 only, lowered dependency minimums

The packages now target `net10.0` only; the `net8.0` and `net9.0` targets are gone, since both frameworks reach end
of support on 2026-11-10. Retarget consuming projects to `net10.0`.

While doing so, every `src/` dependency minimum was lowered to the lowest version with no known vulnerability that
still provides the APIs in use, including `MongoDB.Driver`; see [requirements](requirements.md#mongodbdriver) for the
current minimum.

## Event Store: stream position and aggregate revision

Every aggregate now tracks two numbers instead of one: `Version`, the stream position, and the new `Revision`, the
number of state-changing events applied. See [Stream Position and Revision](event-store.md#stream-position-and-revision)
for the full model, including the new store-assigned versions, `expectedRevision`-based concurrency and
observational events.

- **`IAggregate` gains `Revision`.** Aggregates deriving from `Aggregate` inherit it; aggregates implementing
  `IAggregate` directly must add `public long Revision { get; set; }`.
- **Per-event `Version`/`Revision` updates during a batch append.** After each state-changing event executes, the
  store updates the aggregate's `Version` and `Revision` before executing the next event. Earlier releases exposed
  the pre-batch `Version` to every event during append. An `Execute` implementation that reads `aggregate.Version`
  can therefore produce different final state even in a batch containing only state-changing events — review such
  implementations when upgrading. The store owns both values: anything an `Execute` or `Validate` implementation
  assigns to them is overwritten.
- **`AppendEventsAsync` gains `expectedRevision` as its second parameter.** Callers that passed `onBeforeCommit`
  positionally must pass it by name (`onBeforeCommit: ...`) or supply `expectedRevision` first.
- **`IAggregateRepository<TAggregate>` gains `GetAtRevisionAsync`.** Custom implementations, including hand-written
  test doubles, must implement it.

## Event Store: class hierarchy registration and discriminator collisions

Earlier versions built every event class map directly on `Event<TAggregate>` and every aggregate class map directly
on `Aggregate`, so members declared on intermediate classes were silently neither stored nor read back. The event
store now registers class maps along the real class hierarchy, so inherited members are stored and read back; see
[Class Hierarchies](event-store.md#class-hierarchies).

- **Registration throws `InvalidOperationException` when two types in an aggregate's event hierarchy share a
  discriminator.** This includes an unregistered intermediate type whose default name matches another event's
  discriminator, such as `OrderPlacedV2 : OrderPlaced` registered as `"OrderPlaced"`. Such configurations previously
  started; register the intermediate type with a distinct discriminator.
- **Newly appended events derived from intermediate classes store longer discriminator arrays.** Events and read
  models stored with inherited members contain elements that earlier versions do not map, so earlier versions fail
  to read them unless extra elements are ignored. Do not run earlier versions against the same collections after
  upgrading. An inherited member marked `[BsonRequired]` makes events, read models and checkpoints stored before the
  upgrade unreadable, because they lack its element — remove the attribute, or rewrite or migrate the affected
  documents, before upgrading.
- **Stored documents are not rewritten.** Events stored with the shorter discriminator array still deserialize, and
  sealed events still verify.
- **Data dropped by earlier versions cannot be recovered.** Inherited event members were never stored, so replay
  cannot restore them, and existing read models and checkpoints regain inherited members only when later events set
  them. Until then, reconstruction from such a checkpoint can differ from a replay that starts at the beginning of
  the stream. Consumers who registered their own class maps before the event store were not affected.

## Event Store: integrity collection naming

`IntegrityVerificationBuilder.WithAnchorCollections(anchors, leaves, evidence, evidenceChunks)` is removed;
`IntegrityAnchoringBuilder` had no renaming method before. Both builders now expose `WithCollectionPrefix(prefix)`
instead, which derives all five module-level collection names from one prefix: `{prefix}Anchors`,
`{prefix}AnchorLeaves`, `{prefix}AnchorEvidence`, `{prefix}AnchorEvidenceChunks` and `{prefix}ChangeStreamState` (the
last was never configurable before). A container that both anchors and verifies must configure the same prefix on
both builders; registration fails otherwise. The default prefix stays `Integrity`, so deployments that never called
`WithAnchorCollections` keep today's collection names with no changes.

Deployments that did call `WithAnchorCollections` with four names that do not fit that single-prefix pattern cannot
reproduce them with `WithCollectionPrefix`: configuring names is no longer independent per collection. Before
upgrading, rename the physical anchors/leaves/evidence/evidence-chunks collections in MongoDB (for example with
`db.collection.renameCollection(...)`) to the names a chosen prefix derives, then configure that prefix. Until the
physical collections are renamed to match, existing anchors, leaves and timestamp evidence are unreachable under the
new names. The change-stream-state collection needs no migration: it was never configurable, and anchoring's
recovery scan repopulates it from scratch if it starts empty.

## Public API trim: Chaos.Mongo

`Chaos.Mongo`'s public surface is now trimmed to its 1.0 contract:

- Default implementations behind documented interfaces (`MongoHelper`, the configurator/migration/hosted-service
  runners, `MongoLock`, the queue publisher/subscription/handler-factory stack, and others) are now `internal`.
  Depend on the documented interfaces (`IMongoHelper`, `IMongoLock`, and so on) instead.
- `MongoHelperExtensions.ExecuteInTransaction` is renamed to `ExecuteInTransactionAsync` (both overloads). Rename
  call sites.
- `MongoOptions` changed from a `sealed record` to a mutable `sealed class`. Code using `with` expressions must
  switch to object-initializer syntax or property assignment.
- `MongoDefaults`'s tunable values (`QueryLimit` and its boolean switches) changed from `const` to `static` get-only
  properties. Code that required a compile-time constant (for example, an attribute argument or a `case` label) must
  read the value at run time instead; ordinary reads are unaffected.
- Persisted types (`MongoQueueItem<T>`, `MongoLockDocument`, `MongoMigrationHistoryItem`) have `internal` state
  setters wherever the BSON mapping allows it. Code that constructed or mutated them directly, instead of through the
  documented APIs, no longer compiles.

## Public API trim: Chaos.Mongo.EventStore

`Chaos.Mongo.EventStore`'s public surface is likewise trimmed, with a few shape changes alongside it:

- Implementation types (`MongoEventStore<T>`, `MongoAggregateRepository<T>`, the configurator/serialization-setup
  types, `CheckpointDocument<T>`, `CheckpointId`) are now `internal`. Depend on `IEventStore<T>` and
  `IAggregateRepository<T>` instead.
- `IEventStore<T>.GetEventStream` is renamed to `GetEventStreamAsync`. Rename call sites.
- `ITimestampAuthority`'s boundary, and the revocation/timestamp types (`GetRevocationListsAsync`,
  `RevocationLists`, `TimestampToken`), use `DateTime` with a `…Utc` suffix instead of `DateTimeOffset`. A custom
  `ITimestampAuthority` implementation must return UTC `DateTime` values; a non-UTC value crossing the boundary now
  throws `ArgumentException`.
- The `.Integrity` result records (`AnchorAuthorityFailure`, `AnchorChainVerificationResult`, `AnchorLeaf`,
  `AnchorReference`, `AnchorTimestamp`, `CheckpointVerification`, `CompletedAnchor`, `CompletenessFinding`,
  `DerivedDataVerificationResult`, `EventAnchorVerificationResult`, `IntegritySealingStatus`,
  `PersonalDataPathStatus`, `StreamVerificationResult`, `TimestampConfirmationFailure`,
  `TimestampVerificationResult`, `TrustedAnchorHead`, `UnrecordedErasure`, `VerifiedEvent`) changed from positional
  records to records with `init`-only properties. Code using positional construction or `Deconstruct` on them must
  switch to object-initializer syntax or named property access. The BSON wire format is unchanged.
- `TimestampAuthorityException` moved to `Chaos.Mongo.EventStore.Errors`; `IntegrityProtectionBuilder<T>` moved to
  `Chaos.Mongo.EventStore.Integrity`. Update `using` directives.
- A new public `IntegrityMetrics` class exposes `MeterName`, `Instruments` and `Tags` for the integrity meter.

## Public API trim: Chaos.Mongo.Outbox

`Chaos.Mongo.Outbox`'s public surface is trimmed to its documented contract:

- `MongoOutbox`, `OutboxConfigurator`, `OutboxHostedService`, `OutboxProcessor`, `OutboxSerializationSetup` and
  `OutboxOptions` are now `internal`. `OutboxBuilder` stays public, but its constructor, `Build()`, `Validate()`,
  `PublisherFactory`, `PublisherLifetime` and `PublisherType` are now `internal` too. Configure an outbox through
  `OutboxBuilder`'s fluent `With*` methods instead of an `OutboxOptions` initializer.
- `OutboxMessage` is `sealed`; every setter/init accessor is `internal`, while every getter stays `public`. Code
  that constructed or mutated `OutboxMessage` directly, instead of through the documented APIs, no longer compiles.
- `IOutbox`, `IOutbox<T>`, `IOutboxProcessor`, `IOutboxProcessor<T>`, `IOutboxPublisher`, `OutboxMessageState` and
  `IOutboxConfiguratorRunner` are unchanged and public.

## Queue and outbox option renames

Queues and the outbox configured the same concepts under different names; both builders now use the same names:

| Concept | Queue (before) | Outbox (before) | Both (after) |
| --- | --- | --- | --- |
| Lock lease | `WithLockLeaseTime` / `LockLeaseTime` | `WithLockTimeout` / `LockTimeout` | `WithLockLeaseTime` / `LockLeaseTime` |
| Items read per poll | `WithQueryLimit` / `QueryLimit` | `WithBatchSize` / `BatchSize` | `WithBatchSize` / `BatchSize` |
| Retention of finished items | `WithClosedItemRetention` / `ClosedItemRetention` | `WithRetentionPeriod` / `RetentionPeriod` | `WithRetentionPeriod` / `RetentionPeriod` |

The queue keeps `WithImmediateDelete`, which the outbox does not have. `MongoDefaults.QueryLimit` is renamed to
`MongoDefaults.QueueBatchSize`, and `MongoDefaults.QueueClosedItemRetention` is renamed to
`MongoDefaults.QueueRetentionPeriod`. Stored document shapes are unchanged; rename call sites to the new builder
methods, properties and defaults.

## Configuration: ambiguous `null` for `AddMongo` configure delegates

`AddMongo` can now configure `MongoOptions` with the `IServiceProvider` (see
[Configuring options with registered services](configuration.md#configuring-options-with-registered-services)).
Passing a literal `null` as `configure` is now ambiguous (`CS0121`) for `AddMongo(null)`, `AddMongo(configure: null)`,
and the three-argument connection-string and `MongoUrl` overloads, because the compiler can no longer pick between
the `Action<MongoOptions>` and `Action<IServiceProvider, MongoOptions>` overloads. Omit the argument, or cast it to
`Action<MongoOptions>`.
