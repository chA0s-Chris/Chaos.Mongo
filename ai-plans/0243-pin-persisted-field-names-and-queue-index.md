# Pin Persisted Field Names and Name the Queue Processing Index

## Rationale

[Issue #243](https://github.com/chA0s-Chris/Chaos.Mongo/issues/243) reports that `docs/compatibility-policy.md` freezes
the stored format at 1.0, but the library's own persisted types still rely on whatever BSON conventions the hosting
application registers globally. None of `MongoQueueItem`/`MongoQueueItem<TPayload>`
(`src/Chaos.Mongo/Queues/MongoQueueItem.cs`), `MongoLockDocument` (`src/Chaos.Mongo/MongoLockDocument.cs`),
`MongoMigrationHistoryItem` (`src/Chaos.Mongo/Migrations/MongoMigrationHistoryItem.cs`), or the outbox's
`OutboxMessage` (`src/Chaos.Mongo.Outbox/OutboxMessage.cs`, mapped by `OutboxSerializationSetup`) pin their element
names today, so a host that registers a global camelCase convention silently renames every stored field (e.g.
`IsClosed` becomes `isClosed`). `Chaos.Mongo.EventStore` already pins a handful of fields this way (`EventIntegrity`,
`IntegritySealingState`, `PersonalDataCommitment`, `PersonalDataErasureEntry`/`PersonalDataErased<TAggregate>`, and
`CheckpointDocument<TAggregate>.ChainHash`, all in `src/Chaos.Mongo.EventStore/MongoEventStoreSerializationSetup.cs`),
but leaves the ordinary members of `Aggregate`, `Event<TAggregate>`, `CheckpointDocument<TAggregate>`, and
`CheckpointId` unpinned, so the same gap exists there for everything except the fields a prior change already
protected.

Two correctness bugs compound the risk: the queue's closed-item TTL partial filter
(`MongoQueueSubscription.cs:135-145`) and the outbox's processed/failed TTL partial filters
(`OutboxConfigurator.cs:60-66,73-79`) build their `PartialFilterExpression` from raw `nameof(...)` strings (a
`BsonDocument` literal for the queue, the string-overloads of `Filter.Exists`/`Filter.Type` for the outbox) instead of
through the driver's serializer, so a renamed element silently stops matching and retention stops deleting closed or
terminal documents without any error. Separately, the queue's processing index
(`MongoQueueSubscription.cs:110-120`) is created with no `CreateIndexOptions`, so MongoDB assigns it a generated name,
and its shape has never been checked against the default FIFO query's `Ascending(x => x.Id)` sort
(`MongoQueuePayloadPrioritizer.cs`).

This change pins every remaining unprotected persisted element name across `Chaos.Mongo`, `Chaos.Mongo.Outbox`, and
`Chaos.Mongo.EventStore`; rebuilds the queue's and the outbox's TTL partial filters from expression-based,
serializer-aware filters instead of literal strings; gives the queue's processing index an explicit, frozen name
chosen after measuring its effect with `explain()` under a representative backlog; and records the frozen index name
in `docs/compatibility-policy.md`. Renaming the processing index from its current generated name to the new explicit
name is handled by the existing `MongoIndexManagerExtensions.CreateOneOrUpdateAsync` conflict resolution (fixed by
#247, already merged), which locates the conflicting index by key specification rather than by name, so no new
migration logic is needed for that part.

## Acceptance Criteria

- [ ] `MongoQueueItem`, `MongoQueueItem<TPayload>`, `MongoLockDocument`, `MongoMigrationHistoryItem`, `OutboxMessage`,
      and the currently-unpinned members of `Aggregate` (including `Version`), `Event<TAggregate>` (including
      `Version`), `CheckpointDocument<TAggregate>`, and `CheckpointId` serialize to the same BSON element names
      whether or not a global camelCase convention pack is registered, verified by asserting the exact rendered
      `BsonDocument` element name of every pinned member of each type, under a process where the convention is
      registered before the type is first used (default-convention and camelCase-convention cases run in separate
      fresh processes, since class-map/convention registration is global and irreversible within a process; see the
      isolation approach in Technical Details).
- [ ] With that camelCase convention registered, the queue's closed-item TTL partial filter and the outbox's
      processed/failed TTL partial filters still match the documents they are meant to expire: inserting documents in
      the relevant states and executing each partial filter as a query returns exactly the expected documents, both
      with and without the convention active.
- [ ] With that camelCase convention registered, a real queue TTL index and real outbox TTL indexes, created through
      `EnsureIndexesAsync`/the outbox configurator exactly as production code builds them, actually expire and delete
      eligible old, successfully closed, non-terminal queue items (`IsClosed: true` and `IsTerminal` false, missing, or
      an explicit `null`) and eligible processed/failed outbox messages, while leaving ineligible documents in
      place — open queue items, terminal queue items (`IsTerminal: true`, retained for dead-letter handling per
      `docs/queues.md`), and pending outbox messages — observed as an eventual outcome bounded for MongoDB's TTL
      background monitor.
- [ ] The queue's processing index (`MongoQueueSubscription.cs:110-120`) has an explicit, version-controlled name.
- [ ] An existing collection whose processing index was created under the old generated name is transparently
      migrated to the new explicit name the next time `EnsureIndexesAsync` runs, without throwing and without leaving
      both indexes present.
- [ ] The PR description records an `explain()` result, captured against a collection seeded with a representative
      backlog (a mix of locked, unlocked, closed, and terminal items), justifying the final key order of the
      processing index, including whether appending the default FIFO sort key (`_id`) avoids an in-memory sort stage.
- [ ] `docs/compatibility-policy.md` lists the processing index's frozen name alongside the existing "default
      collection names, index names, lock names... are contract" statement.
- [ ] The Release build (`dotnet build -c Release Chaos.Mongo.slnx`) produces no warnings, and the complete test
      suite (`bash ./build.sh Test`) passes while maintaining the repository's required merged line coverage.

## Technical Details

### Pinning mechanism: extend the existing `BsonClassMap` pattern, not attributes

The issue asks to "register explicit class maps with fixed element names." `Chaos.Mongo.EventStore` already
establishes this exact mechanism (`BsonClassMap.RegisterClassMap<T>(cm => { cm.AutoMap(); ... })` followed by
`cm.GetMemberMap(x => x.Member).SetElementName(nameof(T.Member))` for each member that must survive a convention
change). This change extends that same mechanism everywhere else instead of introducing a second,
attribute-based (`[BsonElement]`) mechanism, so the codebase has one consistent way to freeze a persisted shape:

- **`Chaos.Mongo`**: add a new internal static class, e.g. `MongoSerializationSetup`, mirroring
  `OutboxSerializationSetup`'s shape (`IsClassMapRegistered` guard, `AutoMap()` plus per-member
  `SetElementName(nameof(...))`), covering `MongoLockDocument` (`AcquisitionToken`, `Holder`, `LeaseUntilUtc`; `Id`
  stays mapped via its existing `[BsonId]` attribute, which `AutoMap()` already honors) and
  `MongoMigrationHistoryItem` (`AppliedUtc`, `Description`, `DurationMs`; `Id` likewise stays `[BsonId]`). Call it
  once from `ServiceCollectionExtensions.AddMongoInternal` (`src/Chaos.Mongo/ServiceCollectionExtensions.cs:187`),
  the single funnel every public `AddMongo(...)` overload already calls through, so registration happens before any
  connection or collection is used regardless of which overload the host called.
- **`MongoQueueItem<TPayload>`** is generic, so its class map must be registered per closed generic type, not once.
  Register it from `MongoQueueBuilder<TPayload>.RegisterQueue()` (`src/Chaos.Mongo/Queues/MongoQueueBuilder.cs:238`),
  guarded by `BsonClassMap.IsClassMapRegistered(typeof(MongoQueueItem<TPayload>))`, mirroring how
  `OutboxSerializationSetup.RegisterClassMaps` already registers one class map per payload type. Also register the
  non-generic `MongoQueueItem` base class map (`ClosedUtc`, `CreatedUtc`, `IsClosed`, `IsLocked`, `IsTerminal`,
  `LockedUtc`, `RetryCount`; unlike `MongoLockDocument`/`MongoMigrationHistoryItem`, `MongoQueueItem.Id` carries no
  `[BsonId]` attribute, so it stays mapped to `_id` through the driver's convention-independent default — `AutoMap()`
  recognizes a member literally named `Id` as the id member regardless of any element-naming convention pack) from
  the same place or from `MongoSerializationSetup`, whichever avoids registering it redundantly once per payload
  type; `MongoQueueItem<TPayload>` itself only adds `Payload` and `PayloadType`.
- **`Chaos.Mongo.Outbox`**: extend the existing `OutboxMessage` class map in `OutboxSerializationSetup.cs:12-24` with
  `SetElementName(nameof(...))` for every member not already pinned by `MapIdMember` (`CorrelationId`, `CreatedUtc`,
  `Error`, `FailedUtc`, `IsLocked`, `LockId`, `LockedUtc`, `NextAttemptUtc`, `Payload`, `ProcessedUtc`, `RetryCount`,
  `State`, `Type`). No new file is needed; the existing `IgnoreIfNull`/`IgnoreIfDefault` fluent chain just grows.
- **`Chaos.Mongo.EventStore`**: extend the existing class maps in `MongoEventStoreSerializationSetup.cs` with
  `SetElementName(nameof(...))` for the members each one currently leaves unpinned: `Aggregate`'s `CreatedUtc`,
  `Revision`, and `Version` (`:93-114`; only `Id` is already pinned, via the GUID serializer call on `MapIdMember` —
  configuring `Id`'s serializer does not pin any other member, so `Version` is otherwise left to `AutoMap()` exactly
  like `CreatedUtc`/`Revision`), `Event<TAggregate>`'s `AggregateId`, `AggregateType`, `CreatedUtc`, `Revision`, and
  `Version` (`:126-136`; only `Integrity` is already pinned, by its explicit `SetElementName`; `AggregateId`'s
  `MapMember`+serializer call pins its value encoding, not its element name, so `AggregateId` needs its own
  `SetElementName` too), `CheckpointDocument<TAggregate>`'s `Revision` and `State` (`:198-224`; `ChainHash` is already
  pinned), and `CheckpointId`'s `AggregateId` and `Version` (`:198-204`; `AggregateId`'s serializer call likewise
  does not pin its element name), since `CheckpointId` is embedded as the composite `_id` and a camelCase convention
  can still rename its own sub-fields even though the top-level member maps to `_id`.

### Rebuilding the TTL partial filters from mapped names

Replace the queue's closed-item TTL filter (`MongoQueueSubscription.cs:135-145`, currently a hand-built `BsonDocument`
keyed by `nameof(MongoQueueItem.IsClosed)` etc.) so every field name it uses is obtained from the registered class
map instead of a raw `nameof(...)` string literal, so the filter keeps matching even if a member is ever pinned to a
name that differs from its member name. `IsClosed`, `IsTerminal`, and `ClosedUtc` are declared on the non-generic
`MongoQueueItem` base class, not on `MongoQueueItem<TPayload>`, and `BsonClassMap.GetMemberMap` only searches members
declared on the class whose map it is called on (it does not search a base map), so the lookup must resolve them
through the base class's own registered map, e.g.
`BsonClassMap.LookupClassMap(typeof(MongoQueueItem)).GetMemberMap(nameof(MongoQueueItem.IsTerminal)).ElementName`
— looking them up via `typeof(MongoQueueItem<TPayload>)` would return `null` and throw on `.ElementName`. Keep the
existing `$in: [false, null]` shape for the non-nullable `IsTerminal` member: it is a documented supported
partial-index operator, and MongoDB's equality-to-`null` semantics already make it match `false`, an explicit BSON
`null`, and a genuinely missing field (the case of documents written before `IsTerminal` existed) in one expression.
Do not replace it with an `$or` of `$eq`/`$not($exists)`: the `$not`/`$exists: false` combination it would need is not
a supported `partialFilterExpression` operator, so a filter built that way can fail at TTL-index creation time
instead of only silently failing to match. `ClosedUtc`'s existing `$exists`/`$type` pair stays the same shape, with
its field name resolved from the base class's map the same way.

Replace the outbox's `Filter.Exists(nameof(OutboxMessage.ProcessedUtc))` / `Filter.Type(nameof(...), BsonType.DateTime)`
calls (`OutboxConfigurator.cs:60-66` for `ProcessedUtc`, `:73-79` for `FailedUtc`) with the expression-based
`Filter.Exists(m => m.ProcessedUtc)` / `Filter.Type(m => m.ProcessedUtc, BsonType.DateTime)` overloads (and the
`FailedUtc` equivalents); the surrounding `Filter.Eq(m => m.State, ...)` calls are already expression-based and need
no change.

Note that pinning every element name to exactly `nameof(Member)` (as above) would, by itself, already make the
existing `nameof(...)`-keyed filters keep matching correctly under any convention, since the persisted name and the
member name become permanently identical. The filter rewrite above is still required, because it is what the issue
asks for and because it removes the implicit coupling: the filters no longer depend on the pinned name happening to
equal the member name forever, and will keep resolving correctly even if a future change deliberately pins an element
to a different literal name.

### Naming and measuring the processing index

Add an explicit name for the processing index (`MongoQueueSubscription.cs:110-120`) as a new private constant next to
the existing `ClosedItemTtlIndexName` (`:26`), following this project's `IX_<Collection>_<Purpose>` convention (e.g.
`IX_Outbox_Polling`, `IX_Queue_ClosedUtc_TTL`): `ProcessingIndexName = "IX_Queue_Processing"`. Pass it via
`CreateIndexOptions<MongoQueueItem<TPayload>> { Name = ProcessingIndexName }` on the existing `CreateIndexModel`.

The default `IMongoQueuePayloadPrioritizer` sorts by `Ascending(x => x.Id)` (`MongoQueuePayloadPrioritizer.cs`), and
the fetch query filters on `IsClosed`, `IsLocked`, `LockedUtc`, and `IsTerminal`
(`CreateAvailableQueueItemFilter()`, `MongoQueueSubscription.cs:87-98`). Measure with `explain("executionStats")`
against a collection seeded with a representative backlog (a mix of available, locked-but-expired, locked-and-active,
closed, and terminal items) whether appending `.Ascending(x => x.Id)` to the current four-key index lets the server
satisfy the default prioritizer's sort from the index (avoiding an in-memory `SORT` stage) without materially
increasing the index's write cost, and record the winning/rejected plan summary in the PR description. Because
`IMongoQueuePayloadPrioritizer` is a public extension point, a custom prioritizer can request an arbitrary sort the
index cannot serve; frame the index shape decision around the default prioritizer only, and do not attempt to serve
every possible custom sort.

### Index rename on existing databases

`MongoIndexManagerExtensions.CreateOneOrUpdateAsync` (already used by this call site, and fixed by the merged
[#247](https://github.com/chA0s-Chris/Chaos.Mongo/issues/247)) resolves an `IndexOptionsConflict`/`IndexKeySpecsConflict`
by matching the existing index's key specification and options, not by name. This handles the case where only
`Options.Name` is added and the key specification stays exactly the four existing ascending keys: the server raises a
conflict for the same key specification under a different name, and the conflict resolver finds and replaces the
old generated-name index automatically. It does **not** handle the case where the `explain()`-driven measurement also
changes the key specification (e.g. appending `_id`): a different key specification under a new name creates no
conflict, so MongoDB creates the new index alongside the untouched old one instead of replacing it. If the
measurement selects new keys, the call site must also explicitly find and drop the old generated-name index (the
exact unnamed four-key `IsClosed`/`IsLocked`/`LockedUtc`/`IsTerminal` index this library has always created here) once
the new named index exists, tolerating the case where it was already removed by a previous run or a from-scratch
database. Add a regression test alongside this project's existing coverage
(`tests/Chaos.Mongo.Tests/Integration/MongoIndexManagerExtensionsIntegrationTests.cs`) that starts from the exact old
unnamed four-key index (as today), runs `EnsureIndexesAsync` with the final chosen named definition, and asserts the
old index is gone, `IX_Queue_Processing` exists with the final expected keys, and no unrelated index is affected;
cover both the unchanged-key and (if the measurement selects new keys) the changed-key path.

### Compatibility documentation

Add the frozen name `IX_Queue_Processing` to `docs/compatibility-policy.md`'s "Stored data" section, next to the
existing "Default collection names, index names, lock names... are contract" bullet, so the queue's processing index
joins the already-explicitly-named `IX_Queue_ClosedUtc_TTL`, `IX_Outbox_Polling`, `IX_Outbox_ProcessedUtc_TTL`,
`IX_Outbox_FailedUtc_TTL`, and the `IndexNames` constants in `Chaos.Mongo.EventStore` as a documented, frozen name.

### Testing

- Field-name stability: a type-scoped `ConventionRegistry.Register` (the pattern `ReferenceVerifierIntegrationTests.cs:474-481`
  and `PersonalDataErasureRegistrationTests.cs:149-158` already use) only prevents the convention from leaking to
  *other* types; it does not help here, because `BsonClassMap`/`ConventionRegistry` registration is process-global
  and irreversible (see `CheckpointClassMapRegistrationTests.cs`'s own doc comment), so once one test or production
  code path has registered a fixed production type's class map under the default convention, a later camelCase
  convention cannot remap it, and the existing `MongoQueueItemTests.cs`/`MongoLockDocumentTests.cs`/
  `MongoMigrationHistoryItemTests.cs`/`OutboxSerializationSetupTests.cs` already deserialize those exact production
  types elsewhere in the suite. Verify the camelCase-convention case in a separate test process (or process-per-case
  test host) from the default-convention case, registering the camelCase convention for the relevant production
  type(s) before anything in that process registers or uses their class map, then exercise the library's normal
  `AddMongo(...)`/registration path so the assertion covers the actual production registration, not a hand-built
  class map. In each process/case, assert the exact rendered `BsonDocument` element name of every member the
  Acceptance Criteria lists as pinned for that type (a round trip may supplement this but cannot replace asserting
  the exact name, since a consistently-wrong name on both sides of a round trip would still pass). Extend the
  existing per-type unit tests for the default-convention case and add the equivalent camelCase-convention coverage
  as isolated case(s) for `MongoQueueItem`, `MongoQueueItem<TPayload>`, `MongoLockDocument`,
  `MongoMigrationHistoryItem`, `OutboxMessage`, `Aggregate`, `Event<TAggregate>`, `CheckpointDocument<TAggregate>`,
  and `CheckpointId`.
- Retention-filter correctness: with the same camelCase convention active (under the isolation approach above),
  insert documents representing closed non-terminal queue items and processed/failed outbox messages into a real
  collection (via the existing Testcontainers integration fixtures), execute each TTL partial filter as an ordinary
  `Find`, and assert it returns exactly the documents it should (and none of the documents it should not, e.g. open
  queue items, terminal queue items kept for dead-letter handling, or pending outbox messages), both with and without
  the convention registered. This proves filter selection but not actual expiry, so it supplements rather than
  replaces the next point.
- Retention actually deletes: with the same camelCase convention active, create the real queue TTL index and real
  outbox TTL indexes exactly as production code builds them (via `EnsureIndexesAsync`/the outbox configurator, not a
  hand-built index), seed sufficiently old closed non-terminal queue items and processed/failed outbox messages
  alongside ineligible ones — open queue items, terminal queue items (`IsTerminal: true`), and pending outbox
  messages — and assert, within a bound that accounts for MongoDB's asynchronous TTL monitor (which runs
  periodically, not immediately), that the eligible documents are eventually deleted while every ineligible document,
  including terminal queue items, remains. This is the regression coverage for the acceptance criterion requiring
  retention to keep deleting documents under the convention, not just matching them, without deleting terminal items
  kept for dead-letter handling.
- Legacy `IsTerminal` values: since the queue's closed-item TTL filter's `IsTerminal` clause must keep matching
  documents where the field is `false`, an explicit BSON `null`, or missing (pre-#239/#246 documents), cover all
  three terminal values using raw legacy documents inserted directly as `BsonDocument`s (bypassing the typed model,
  which cannot represent "missing" or a null value for a non-nullable member), in addition to covering it as part of
  the TTL-index-creation regression test in the previous point.
- Processing index: the rename regression test described above, plus a test (or the same one) asserting the index's
  `Name` is `IX_Queue_Processing` once `EnsureIndexesAsync` has run on a fresh collection.
