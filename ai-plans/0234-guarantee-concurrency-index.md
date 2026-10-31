# Guarantee the event store's concurrency index in every setup

## Rationale

[Issue #234](https://github.com/chA0s-Chris/Chaos.Mongo/issues/234): optimistic concurrency in the
event store depends entirely on the unique `(AggregateId, Version)` index
(`IndexNames.AggregateIdWithVersionUnique`, created by
`MongoEventStoreConfigurator<TAggregate>`). `MongoEventStore<TAggregate>.AppendOnceAsync`
detects a concurrency conflict only by catching the `MongoException` that this index raises
on a duplicate key (`src/Chaos.Mongo.EventStore/MongoEventStore.cs:393-412`); without the
index, two concurrent appends at the same version simply both succeed. Configurators —
including this one — only run automatically when `RunConfiguratorsOnStartup` is `true`
(default `false`, `MongoDefaults.cs:73`), and neither `docs/event-store.md`'s quick start
nor `src/Chaos.Mongo.EventStore/README.md` mention enabling them or creating the index any
other way. The result is that following the documented setup leaves a store with no
concurrency protection and no indication of that fact. (The issue also notes that, with
integrity protection enabled, the same missing-index condition would let duplicate
positions fork the hash chain; guaranteeing the index closes that path too, so no separate
fix is needed there.)

The fix makes `AppendOnceAsync` ensure the unique index exists before it relies on it,
independent of `RunConfiguratorsOnStartup`, and gives deployments whose database role can
only `insert`/`find` (as the "Application" role is documented, `docs/event-store.md`
around line 1172) a clear, explicit way to either see the problem or acknowledge it.

## Acceptance Criteria

- [x] A store registered exactly as the quick start in `docs/event-store.md` and
      `src/Chaos.Mongo.EventStore/README.md` shows it (`RunConfiguratorsOnStartup` left at
      its default `false`, no manual configurator run) cannot commit two events at the same
      `(AggregateId, Version)`; the loser gets `MongoConcurrencyException`, not a silent
      duplicate.
- [x] When the connection used for appending lacks permission to create indexes and the
      unique index does not already exist, appending throws a clear, dedicated exception
      that explains the missing protection and how to resolve it, instead of silently
      appending without concurrency protection. An integration test using a connection
      restricted to `insert`/`find` (no `createIndex`) on the events collection verifies
      this.
- [x] An explicit opt-out is available for deployments that provision the index out-of-band
      (e.g. via an elevated-privilege configurator run) and want appends on a
      restricted-privilege connection to skip the automatic index check. An integration test
      verifies that, with this opt-out set and the index already present, appending
      succeeds without attempting index creation.
- [x] A concurrency test reproducing the issue's scenario (many concurrent appends racing
      for the same explicit version) runs in the integration suite for both
      `RunConfiguratorsOnStartup = true` and `= false`, and in both cases exactly one
      append per contested version succeeds while the rest fail with
      `MongoConcurrencyException` — no duplicated positions.
- [x] `docs/event-store.md`'s quick start, its "MongoDB Roles" table, and its "Exception
      Handling" table, plus `src/Chaos.Mongo.EventStore/README.md`'s quick start, correctly
      describe when and how the concurrency index is created, and the restricted-role
      behavior and its opt-out.
- [x] Ensuring the concurrency index in one database does not suppress the check (or its
      failure/opt-out behavior) for a same-named events collection in a different database
      reachable through the same `IMongoClient`. A test verifies this with two databases on
      one client.
- [x] If an index already exists under the concurrency index's name with different keys or
      options, appending fails with the dedicated exception instead of dropping and
      recreating that index; no event is persisted and the existing index is left intact. A
      test verifies this.
- [x] The Release build produces no warnings and the full test suite passes.

## Technical Details

### Ensuring the index before relying on it

`MongoEventStore<TAggregate>.AppendOnceAsync` (`MongoEventStore.cs:217-413`) already has one
precedent for a "verify a precondition once, cache it, fail clearly if unmet" check:
`EnsureBulkWriteOptimizationSupportedAsync` (`MongoEventStore.cs:442-453`), called before the
transactional insert block, delegating to `MongoEventStoreBulkWriteSupport`
(`MongoEventStoreBulkWriteSupport.cs`), which caches the result per `IMongoClient` in a
`ConditionalWeakTable<IMongoClient, Lazy<Task<Version>>>` and evicts the cache entry on
failure so a transient error doesn't permanently poison the store.

Add an analogous `EnsureConcurrencyIndexAsync` check, invoked at the same point in
`AppendOnceAsync`, before the transactional block. The index is specific to a given events
collection, and a single `IMongoClient` can serve multiple databases
(`MongoConnection.cs:18-23` holds the client and database separately; `MongoHelper.cs`
preserves both, and `MongoEventStore.cs:461-462` resolves the events collection from the
helper's database). Key the cache by `IMongoClient` plus the full collection namespace
(database name and collection name), not the collection name alone, so that two databases on
the same client with identically-named events collections are tracked independently (one
database's successful initialization must not suppress the check, or an opt-out's effect, for
the other). `IEventStore<TAggregate>` is registered `Scoped`
(`MongoBuilderExtensions.cs:67`), so caching on the store instance itself would not persist
across requests; the client remains the right cache anchor, as `MongoEventStoreBulkWriteSupport`
already assumes, with the namespace as part of the key. Reuse the same eviction-on-failure
behavior for transient errors.

The check:

- If `MongoEventStoreOptions<TAggregate>.VerifyConcurrencyIndexOnAppend` (new property,
  default `true`) is `false`, return immediately without contacting MongoDB — the caller has
  asserted the index already exists.
- Otherwise, attempt to create the index via the plain, non-destructive
  `IMongoIndexManager<TDocument>.CreateOneAsync`, using the exact same `CreateIndexModel` that
  `MongoEventStoreConfigurator<TAggregate>.ConfigureAsync` (`MongoEventStoreConfigurator.cs`)
  builds today. Extract that model construction into one shared internal helper (e.g. a static
  method on `IndexNames` or a new internal `EventStoreIndexes` type) used by both the
  configurator and this new check, so the index definition has a single source of truth. Do
  not use `MongoIndexManagerExtensions.CreateOneOrUpdateAsync`
  (`src/Chaos.Mongo/MongoIndexManagerExtensions.cs:11-41`): on an `IndexOptionsConflict` or
  `IndexKeySpecsConflict`, it drops the existing index under that name before recreating it,
  which would let an ordinary append silently destroy a conflicting index that may currently
  be the only concurrency protection in place, with no transactional guarantee that recreation
  succeeds afterward. A name conflict on the append path must fail closed instead: treat
  `IndexOptionsConflict`/`IndexKeySpecsConflict` the same as the authorization failure below
  (do not drop or replace the existing index), surfaced through the same
  `MongoConcurrencyIndexMissingException`, naming the conflicting index and directing the
  caller to reconcile it through an explicit provisioning/configurator run.
- `CreateOneAsync` itself is idempotent for a matching existing index (no-op, no conflict
  exception), so success covers both "index already existed" and "index just created".
- On success, cache the result for the lifetime of the `IMongoClient` so later appends (in
  any scope, for the same database and collection) skip the round trip.
- On failure caused by insufficient privilege (MongoDB's `Unauthorized` error on
  `createIndex`) or by a name conflict as described above, throw a new
  `MongoConcurrencyIndexMissingException` (sealed, extends `MongoEventStoreException`,
  matching the plain `(message, innerException)` constructor convention of its siblings in
  `src/Chaos.Mongo.EventStore/Errors/`). Its message states that the unique concurrency index
  could not be verified or created, that appends are unsafe without it, and that the caller
  should either grant `createIndex` on the events collection, run configurators once with an
  elevated-privilege connection (`RunConfiguratorsOnStartup` or `IMongoConfiguratorRunner`, per
  `docs/configurators.md`), or set `VerifyConcurrencyIndexOnAppend` to `false` once the index
  is confirmed to exist; a conflict message additionally names the existing index so it can be
  reconciled manually. Other failures propagate as-is, matching
  `MongoEventStoreBulkWriteSupport`'s behavior of not swallowing unrelated errors.

Expose the opt-out as `MongoEventStoreBuilder<TAggregate>.WithoutConcurrencyIndexVerification()`,
mirroring the existing opt-out precedent `IntegrityProtectionBuilder<TAggregate>.WithoutSealingSweep()`
(`src/Chaos.Mongo.EventStore/Integrity/IntegrityProtectionBuilder.cs:66`), setting the new
`internal set` option to `false`.

This makes the quick-start setup (configurators never run) safe by default: the first
append on a connection that can create indexes creates it transparently, with no
`RunConfiguratorsOnStartup` dependency. `MongoEventStoreConfigurator<TAggregate>` is
unchanged and remains useful for pre-provisioning the index (and the checkpoint lookup
indexes it also creates) ahead of time, e.g. with an elevated-privilege connection before
switching the application to its restricted role.

### New public API

`MongoConcurrencyIndexMissingException`, `MongoEventStoreOptions<TAggregate>.VerifyConcurrencyIndexOnAppend`,
and `MongoEventStoreBuilder<TAggregate>.WithoutConcurrencyIndexVerification()` are new public
API and must be declared in `src/Chaos.Mongo.EventStore/PublicAPI.Unshipped.txt`.

### Tests

- Restricted-role integration test(s): a dedicated MongoDB Testcontainers instance with
  authentication enabled (the shared `MongoDbTestContainer` in
  `tests/Chaos.Mongo.EventStore.Tests/Integration/` runs without auth, so this needs its own
  container/user bootstrap, not a change to the shared one), with an application user
  granted only `insert` and `find` on the events collection, matching the documented
  "Application" role. Verify: appending without the opt-out throws
  `MongoConcurrencyIndexMissingException`; appending with
  `WithoutConcurrencyIndexVerification()` set, after the index was created out-of-band via
  an elevated connection, succeeds normally.
- Concurrency regression test(s): extend the integration suite (near
  `StoreAssignedAppendIntegrationTests.cs` or a new file) with a scenario similar to the
  issue's reproduction — several explicit-version appends racing for the same version,
  repeated across multiple rounds — parameterized or duplicated for both
  `RunConfiguratorsOnStartup = true` and `= false`. Scale can be smaller than the issue's
  manual 25×8 repro (enough rounds/concurrency to make a false pass implausible) to keep
  integration-suite runtime reasonable; assert exactly one success per round and that every
  other attempt fails with `MongoConcurrencyException`.
- Existing coverage in `EventStoreIndexContractIntegrationTests.cs` (which manually runs
  `IMongoConfigurator.ConfigureAsync` to assert the index shape) continues to pass unchanged
  since the index model itself is not changing, only where it gets created from.
- Cache namespace-isolation test: one `IMongoClient` against two databases that each contain
  an events collection of the same name. Ensuring the index in the first database must not
  mark the second database's check as satisfied; appending in the second database (with the
  index not yet present there, and verification not opted out) must still attempt — and
  require — index creation.
- Conflicting-index test: pre-create an index under the concurrency index's expected name but
  with different keys/options, then append without the opt-out. Appending must fail with
  `MongoConcurrencyIndexMissingException`, persist no new event, and leave the pre-existing
  index unchanged (not dropped or replaced).

### Documentation

- `docs/event-store.md` quick start (around lines 90-131): state that the first append
  creates the unique concurrency index automatically when the connection is permitted to,
  independent of `RunConfiguratorsOnStartup`.
- `docs/event-store.md` "MongoDB Roles" table (around line 1172): note that the
  "Application" role needs `createIndex` on the events collection for this automatic
  creation, or must have the index provisioned out-of-band plus
  `WithoutConcurrencyIndexVerification()` set.
- `docs/event-store.md` "Exception Handling" table: add a row for
  `MongoConcurrencyIndexMissingException` with its cause and recommended action.
- `src/Chaos.Mongo.EventStore/README.md` quick start: add the equivalent short note.
