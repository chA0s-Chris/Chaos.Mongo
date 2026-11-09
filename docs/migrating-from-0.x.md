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

## Queue: stable collection names for generic payload types

`MongoQueueCollectionNameGenerator` hashed a generic payload type's `Type.FullName` to build the queue's default
collection name. For a closed generic type, `.NET` embeds the assembly-qualified name (including version) of every
type argument in `FullName`, so bumping a referenced package that defines a generic payload's type argument changed
the hash and silently moved the queue to a new, empty collection, stranding its pending items.

The generator now hashes a recursively formatted name that includes the type arguments but never an assembly
identity, so the hash — and therefore the collection name — no longer depends on any type argument's assembly
version. A non-generic payload type's collection name is unchanged.

**Before upgrading a queue with a generic payload type** (for example `MongoQueueBuilder<Envelope<Payload>>`),
either accept the one-time move to the new collection name (any items already queued under the old,
version-dependent name are stranded there and must be migrated or drained manually), or pin the old name explicitly
with `MongoQueueBuilder<TPayload>.WithCollectionName(string)` before upgrading.

## Event Store: generic default collection names and startup collision detection

`CollectionTypeMap.GetCollectionName`'s fallback for an unmapped type used `Type.Name`, which for a closed generic
type is the backtick-arity form with no type arguments (for example `` Envelope`1 ``), so `Envelope<A>` and
`Envelope<B>` resolved to the same default collection name. The fallback now includes the recursively formatted type
arguments (for example `Envelope<A>` and `Envelope<B>`) for a closed generic type; a non-generic type's default name
is unchanged.

Separately, two event-store aggregate types could resolve to the same read-model, events, or checkpoint collection
with no error — for example two differently namespaced aggregate types sharing a simple name, because the default
collection prefix is `typeof(TAggregate).Name`. `WithEventStore<TAggregate>` now throws `InvalidOperationException`,
naming both aggregate types and the shared collection name, as soon as a second aggregate type is registered with a
colliding read-model, events, checkpoint, or (when enabled) integrity-state collection name. This applies regardless
of whether either aggregate has integrity protection enabled, and runs before the two previous integrity-only checks.

**Before upgrading**, confirm no two registered aggregate types already resolve to the same default collection
name; such a configuration now fails startup instead of continuing to silently share data. Pin a distinct name for
one of them in its `WithEventStore<TAggregate>` configure callback, for example:

```csharp
builder.WithEventStore<RenamedAggregate>(es => es.WithCollectionPrefix("OldAggregateName")
                                                  .WithEvent<SomeEvent>());
```

Renaming a mapped class, an aggregate type, or a queue payload type changes its default collection name, because
every default is derived from the CLR type name. Pin the previous name explicitly across a rename with
`MongoOptions.CollectionTypeMap`, `WithEventStore<TAggregate>`'s configure callback
(`MongoEventStoreBuilder<TAggregate>.WithCollectionPrefix(string)`, shown above), or
`MongoQueueBuilder<TPayload>.WithCollectionName(string)`, respectively.

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

## Queue and outbox: `RetryCount` renamed to `AttemptCount`, and `MaxRetries` now means the same thing on both

Both mechanisms previously counted only resolved failures, and disagreed on how many total delivery attempts a
configured `MaxRetries` allowed:

- **The queue's `RetryCount` only increased inside a thrown handler exception.** A process crash, a lock lease
  that expired while a handler was hung, a payload that failed to deserialize, or a DI failure resolving the
  handler never incremented it, so a poison item in any of those situations could cycle through acquire →
  crash/fail-before-handler forever without ever reaching the terminal state `MaxRetries` exists to enforce.
- **The outbox's `RetryCount` had the same gap for a crash between claim and `PublishAsync`/finalization**, and
  `MaxRetries` meant something different: `WithMaxRetries(5)` allowed 6 total delivery attempts on a queue but
  only 5 on the outbox.

Both mechanisms now atomically increment an attempt counter in the same acquire/claim operation that locks the
item for processing, so every attempt is counted the same way regardless of why a previous attempt stopped — a
resolved failure, a crash, a lease-expiry reclaim, a DI failure, or an undeserializable payload. The counter is
renamed from `RetryCount` to `AttemptCount` on both `MongoQueueItem` and `OutboxMessage`, widened from `Int32` to
`Int64`, and now means "the item/message was acquired/claimed for processing," not "processing failed": an item
or message that succeeds on its very first acquisition has `AttemptCount == 1`, where it previously had
`RetryCount == 0`.

`MaxRetries` now means the same thing on both: the number of retries allowed after the first attempt, so a
configured value of `N` allows `N + 1` total delivery attempts before the item/message becomes terminal — the
queue's existing behavior. **The outbox's effective attempt budget for every configured `MaxRetries` increases by
one** (an unconfigured outbox, `WithMaxRetries(5)`, now allows 6 attempts instead of 5); the queue's behavior is
unchanged. If outbox alerting or capacity planning assumed the old, one-attempt-shorter budget, account for the
extra attempt.

**Upgrading with items/messages already in flight:** an item or message that was locked before the upgrade has no
`AttemptCount` field yet. Its next acquisition/claim is counted as attempt 1 regardless of how many attempts it
made before the upgrade — those earlier attempts are not carried over, because they were never recorded under the
old field. The legacy `RetryCount` element, if still present on an old document, is ignored and never read back
into `AttemptCount`.

Rename `RetryCount` references to `AttemptCount` at call sites (`MongoQueueItem.RetryCount`,
`OutboxMessage.RetryCount`), including any `FilterDefinition`/`SortDefinition`/`UpdateDefinition` built against the
old member name. Both packages' `PublicAPI.Shipped.txt`/`PublicAPI.Unshipped.txt` reflect the rename and the
`Int32` → `Int64` widening.

## Outbox: unlimited retries by default, and `Failed` messages excluded from TTL cleanup by default

An unconfigured outbox (no `WithMaxRetries(...)` call) previously gave up on a message permanently after 6 total
delivery attempts (`MaxRetries` defaulted to `5`), which the default exponential backoff reaches after roughly
75 to 95 seconds. A broker outage that outlasted that short window silently lost the message to `Failed`, with no
API to requeue it — in conflict with the outbox's at-least-once delivery purpose. `MaxRetries` is now nullable and
defaults to `null`, meaning unlimited attempts: a message keeps retrying indefinitely, with the existing
exponential backoff still capped at `RetryBackoffMaxDelay`, and it is never marked `Failed` purely for exhausting
an attempt budget.

Separately, if `WithRetentionPeriod(...)` was configured, the outbox also created a TTL index on `FailedUtc`
unconditionally, so a message that did reach `Failed` was deleted after the retention period, leaving only a log
line as a trace. `WithRetentionPeriod(...)` alone no longer does this: the `FailedUtc` TTL index is now only
created when `WithExpireFailedMessages()` is also configured. The `ProcessedUtc` TTL index is unaffected and still
expires `Processed` messages as before.

**Consequence for an outbox already running in production:** without any code change, it no longer gives up on a
message after a short broker outage, and no longer deletes messages that do reach `Failed`. If
`WithRetentionPeriod(...)` was configured, the first startup after upgrading drops the existing
`IX_Outbox_FailedUtc_TTL` index (the same reconciliation `CreateOneOrUpdateAsync`/`DropOneIfExistsAsync` mechanism
already used for every other managed index), unless `WithExpireFailedMessages()` is also configured before
upgrading. No manual migration step is needed, but messages already deleted by that index before upgrading are
not recoverable.

To restore the previous behavior explicitly, configure both:

```csharp
.WithOutbox(o => o
    .WithMaxRetries(5)
    .WithRetentionPeriod(TimeSpan.FromDays(7))
    .WithExpireFailedMessages())
```

## Queue metrics: duration histograms now record seconds

`MongoQueueMetrics.Instruments.ProcessingDuration`, `ProcessingQueueAge` and `LockRecoveryAge` now register unit
`"s"` and record seconds, matching the `IntegrityMetrics` anchoring histograms and OpenTelemetry's convention for
duration instruments. They previously registered unit `"ms"` and recorded milliseconds.

Dashboards, alert thresholds and queries built against the old millisecond values now read 1000x too low against
the same stored/exported data. Rescale them to seconds, or rebuild them against the new unit, before upgrading.

## Configuration: ambiguous `null` for `AddMongo` configure delegates

`AddMongo` can now configure `MongoOptions` with the `IServiceProvider` (see
[Configuring options with registered services](configuration.md#configuring-options-with-registered-services)).
Passing a literal `null` as `configure` is now ambiguous (`CS0121`) for `AddMongo(null)`, `AddMongo(configure: null)`,
and the three-argument connection-string and `MongoUrl` overloads, because the compiler can no longer pick between
the `Action<MongoOptions>` and `Action<IServiceProvider, MongoOptions>` overloads. Omit the argument, or cast it to
`Action<MongoOptions>`.

## Migrations: `RunMigrationsAsync` waits for the lock and returns a result

`IMongoMigrationRunner.RunMigrationsAsync` now returns `Task<MongoMigrationResult>` instead of `Task`. Update call
sites that awaited it as a plain `Task` to use the result, or at least discard it explicitly.

By default, a second instance now waits for the first to finish migrations instead of skipping immediately when the
lock is held, bounded by the new `MongoOptions.MigrationLockWaitTimeout` (30 minutes by default). Set
`MigrationLockWaitTimeout` to `TimeSpan.Zero` to restore the previous "skip immediately if the lock is held"
behavior, or to `null` to wait indefinitely.

`MongoMigrationResult.Skipped` is `true` only when the lock could not be acquired within that wait; it replaces the
previous silent skip. Callers that invoke `RunMigrationsAsync` directly must check `Skipped` themselves and decide
how to react. `MongoHostedService` fails hosted startup instead when migrations are enabled and the run is skipped,
unless `MigrationLockWaitTimeout` is `TimeSpan.Zero`.

The lock's lease is now renewed automatically while migrations run, so a migration (or a batch of migrations) that
runs longer than `MigrationLockLeaseTime` no longer loses the lock on its own; a renewal that is refused or fails
instead signals the in-progress migration for cancellation, leaves it unrecorded, and aborts its transaction, if
any. A migration that observes its cancellation token stops promptly; one that does not cannot be forcibly
interrupted, and any non-transactional effects it already performed cannot be rolled back.

The `MongoMigrationRunner` constructor now throws `ArgumentException` when two or more registered migrations share
the same `Id`, naming the duplicate ID(s), instead of silently applying only one of them.

During hosted startup, migrations now run before configurators instead of after, so a configurator cannot create an
index before a migration that cleans up the data it depends on.

## Distributed Locking: per-acquisition token

Lock documents gain an internal per-acquisition token; release and extension now key off it instead of the
last-known lease expiry. This also fixes a renewal that reached the server but whose response the caller never
observed (including a cancelled renewal on shutdown) from leaving the lock held past disposal, or from refusing a
legitimate retry. A lock already held when upgrading, or taken over by an older instance after its lease expired,
lacks the token but still expires and can be acquired normally. No public API changed (`MongoLock` and
`MongoLockDocument` are internal), so there is nothing for consumers to change beyond this behavior note.

**Boundary: do not run old and new binaries with a shared, configured `HolderId` concurrently against the same
lock.** An older (pre-upgrade) instance's take-over of an expired lock only `$set`s `Holder`/`LeaseUntilUtc`; it has
no `AcquisitionToken` field to write, so a token already on the document (written by a newer instance's earlier
acquisition) survives unchanged. The original newer-version handle still holds that token and can therefore still
release or renew the lease the older instance now believes it owns. This only matters when multiple instances share
one configured `MongoOptions.HolderId`; instances using the default, independently generated `HolderId` are
unaffected because no other instance can ever present a matching `Holder`. Upgrade every instance sharing a
`HolderId` together, or give each instance a unique `HolderId` for the duration of any rollout overlap.
