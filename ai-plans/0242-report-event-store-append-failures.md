# Report event store append failures correctly

## Rationale

[Issue #242](https://github.com/chA0s-Chris/Chaos.Mongo/issues/242) reports three cases where
`MongoEventStore<TAggregate>` (`src/Chaos.Mongo.EventStore/MongoEventStore.cs`) reports the wrong
outcome for a failed append:

1. **Lost events look ignorable.** `AppendOnceAsync`'s duplicate-key handling (lines 394–413)
   classifies any duplicate-key error whose message contains `"index: _id_"` as
   `MongoDuplicateEventException`, regardless of which collection raised it. A duplicate `_id` from
   the read model, a checkpoint, or a collection written by the caller's `onBeforeCommit` callback
   matches the same substring. Because the write runs inside `_mongoHelper.ExecuteInTransactionAsync`,
   the whole transaction aborts — no events are stored — but the caller is told the events already
   exist and is safe to ignore the error, per `MongoDuplicateEventException`'s documented contract.
2. **Idempotent retries get the wrong error.** A caller retries a store-assigned append by
   resubmitting the same event IDs with `Version` left unset (0) — the shape `ValidateBatch`
   requires to classify a batch as store-assigned — rather than reusing the mutated instances whose
   `Version` `AppendOnceAsync` already assigned on the prior attempt. That classification means the
   retry skips `ValidatePositionsAsync`/`CreateStaleVersionExceptionAsync` (lines 238, 613–622),
   which is the lookup that already turns a stale explicit-version append into
   `MongoDuplicateEventException` when its IDs are already stored. When the retry also carries the
   same `expectedRevision` the caller read before the first attempt — the ordinary way to retry
   idempotently — `AppendOnceAsync` now finds `aggregate.Revision` already past `expectedRevision`
   because the first attempt committed, and throws `MongoConcurrencyException` at the
   expected-revision check (line 254) before reaching the insert that would otherwise raise a
   duplicate `_id`. A retry without `expectedRevision` instead reaches `ApplyEvents` (line 269),
   which re-runs `Execute` against the read model the first attempt already updated and can throw
   `MongoEventValidationException` if `Execute` enforces a precondition the first attempt already
   satisfied. Either path reports a conflict or a validation failure instead of the harmless
   duplicate the retry actually is.
3. **Unregistered event types poison the stream.** `AppendEventsAsync` validates the batch's
   structure (`ValidateBatch`) and the `PersonalDataErased` invariant, but never checks that every
   event's runtime type was registered with the store (`MongoEventStoreOptions<TAggregate>.EventTypes`,
   populated by `MongoEventStoreBuilder<TAggregate>.WithEvent<TEvent>()`). An unregistered type
   serializes and inserts without error. Reading it back later (`GetEventStreamAsync`) relies on a
   `BsonClassMap` for that type's discriminator; after a process restart that never called
   `WithEvent<TEvent>()` for it, no such class map exists, and the whole stream fails to deserialize.

This plan makes all three cases fail in a way the caller can act on: a genuine event duplicate is
reported as such and nothing else is, an idempotent retry of a store-assigned append is reported as
a duplicate rather than a conflict, and an unregistered event type is rejected before anything is
written.

## Acceptance Criteria

- [ ] A duplicate-key error raised while committing an append (for example, from `onBeforeCommit`
      inserting into a collection with its own colliding `_id`, or from a checkpoint's `_id`
      colliding) propagates as the original `MongoException` rather than `MongoDuplicateEventException`,
      and no event, read model, or checkpoint from that attempt is stored. This holds even when the
      colliding collection's name contains the events collection's full name as a substring (for
      example an `onBeforeCommit` callback whose own `InsertManyAsync`/client bulk write into
      `"{EventsCollectionName}Outbox"` fails with a duplicate key at the same write-error index and of
      the same exception type that an events-collection conflict would have), and correspondingly when
      a non-concurrency-index duplicate's message happens to contain `IndexNames.AggregateIdWithVersionUnique`
      as a substring.
- [ ] A duplicate `_id` raised on the events collection itself still raises
      `MongoDuplicateEventException` for the typed insert path, and for the serialized-document insert
      path used when `IntegrityProtectionEnabled` is true and `BulkWriteOptimizationEnabled` is false.
- [ ] A duplicate `_id` on an event document raised via the bulk-write-optimization path
      (`BulkWriteOptimizationEnabled` is true, surfaced as `ClientBulkWriteException`) still raises
      `MongoDuplicateEventException`.
- [ ] A store-assigned append that retries with the same event IDs and `Version` left unset (0),
      after a prior attempt with those IDs already committed, raises `MongoDuplicateEventException`
      instead of `MongoConcurrencyException` or `MongoEventValidationException` — both when the
      retry carries the same (now stale) `expectedRevision` as the first attempt, and when it omits
      `expectedRevision`.
- [ ] A store-assigned append whose events are genuinely superseded by a concurrent writer (different
      event IDs) still raises `MongoConcurrencyException` (or retries and succeeds, per
      `MaxAppendRetries`), unchanged from current behavior.
- [ ] `AppendEventsAsync` throws `ArgumentException` when any event's runtime type was not registered
      via `WithEvent<TEvent>()`, without writing any event, read model, or checkpoint. Events of type
      `PersonalDataErased<TAggregate>` are exempt, since the store registers that type's class map
      itself and never requires it to be passed to `WithEvent<TEvent>()`.
- [ ] Automated tests cover all six behavioral criteria above, plus a regression check that an
      already registered, explicit-version append is unaffected.
- [ ] The Release build has no warnings and the full test suite passes.

## Technical Details

### 1. Classify duplicate keys only at the call site that writes the events collection, by the exact failed index name

The current `catch (MongoException ex) when (IsDuplicateKeyException(ex))` block
(`MongoEventStore.cs:394–413`) has two independent defects. First, it wraps the entire
`_mongoHelper.ExecuteInTransactionAsync` delegate — every statement inside it, including the
read-model upsert, the checkpoint insert, and the caller's `onBeforeCommit` callback (which receives
the full `IMongoHelper` and can run arbitrary writes of its own) — so by the time it runs it has lost
which statement actually threw: a `MongoBulkWriteException`/`ClientBulkWriteException` thrown by
`onBeforeCommit`'s own multi-document insert into an unrelated collection (the AC1 test scenario below
additionally picks a look-alike name, `"{EventsCollectionName}Outbox"`, to show this holds even when a
namespace check would also be fooled) has the same .NET exception type and can report a failing write
at the same index (for example index 0) as a genuine events-collection conflict would. Second, and
independently of collection naming, `message.Contains("index: _id_", ...)` and
`message.Contains(IndexNames.AggregateIdWithVersionUnique, ...)` (`MongoEventStore.cs:398,404`) match
the index name as an unanchored substring of the whole duplicate-key message, not as the exact failed
index's name. The events collection may carry a caller-defined unique index of its own — already an
anticipated, documented case (`MongoEventStore.cs:411`, "user-defined index — let it propagate") — and
such an index can be named, for example, `Customer_AggregateId_with_Version_Unique_External`, which
contains `IndexNames.AggregateIdWithVersionUnique` as a substring. Scoping the catch to the events-insert
call alone resolves which *collection* failed but not which *index* on that collection did, so this
second defect survives scoping unless the index-name check itself is also made exact. Both defects must
be fixed together.

Fix the index-name check first, since both call sites below depend on it: add a private helper that
extracts the exact index name from a single `WriteError`'s `Message` — the driver's duplicate-key
message has the fixed shape `... index: <name> dup key: { ... }`; take the substring between
`"index: "` and the following `" dup key:"` — and compare it with `==` (ordinal) to `"_id_"` and to
`IndexNames.AggregateIdWithVersionUnique`. An index name that does not exactly equal either (including
any look-alike custom name, or an index literally named `_id_`-with-extra-characters, which MongoDB's
own `_id_` naming never produces) is unknown and must not be classified; it is handled identically to
today's existing "user-defined index" fallback.

Then scope that check to the specific call that inserts into the events collection, so no other
statement's exception — including the callback's — can ever reach it. A local `catch` placed on a
statement *inside* the transaction delegate cannot itself return a value from `AppendOnceAsync` or
cause the method to throw past the delegate (the delegate's own signature is `Func<..., Task>` with no
usable return channel), so introduce a private, non-public exception type — for example
`EventsCollectionDuplicateKeyConflict : MongoException`, carrying the matched classification (`_id_`
or concurrency-index) and the original exception as `InnerException` — used purely as an internal
signal from a local catch to the existing outer catch. It is never exposed to callers and is not part
of this library's public contract.

- **Non-bulk-write-optimization path:** wrap only the existing events-insert call
  (`eventsCollection.InsertManyAsync(...)` or `GetEventDocumentsCollection().InsertManyAsync(...)`,
  `MongoEventStore.cs:357`/`361`) in its own `try`/`catch (MongoException ex) when
  (IsDuplicateKeyException(ex))`. That call only ever inserts into the events collection, so any
  duplicate key caught there is unambiguously an events-collection conflict. Extract the exact index
  name from `GetDuplicateKeyMessage(ex)` as above; if it equals `"_id_"` or
  `IndexNames.AggregateIdWithVersionUnique`, throw `EventsCollectionDuplicateKeyConflict` carrying that
  classification; otherwise rethrow `ex` unchanged (an unrecognized index on the events collection
  itself, per the existing documented fallback). The read-model upsert, the checkpoint insert, and the
  `onBeforeCommit` callback remain outside this `try`/`catch` and are never classified; their
  exceptions always propagate unchanged.
- **Bulk-write-optimization path:** wrap only the existing `helper.Client.BulkWriteAsync(session,
  models, ...)` call (`MongoEventStore.cs:343–350`) in its own `try`/`catch (ClientBulkWriteException
  ex)`. `models` is built immediately above in the same method invocation, so the index range its
  event-document entries occupy (the `foreach (var document in documents)` loop's positions, before the
  read-model and checkpoint entries) is known at the catch site. Look up `ex.WriteErrors` (keyed by
  request index) for an entry whose key falls in that range; if none exists, rethrow `ex` unchanged (the
  failing write was the read-model replace or the checkpoint insert, never an event document). If one
  exists, extract the exact index name from that specific `WriteError.Message` (not the whole message
  `GetDuplicateKeyMessage` would join across every write error); if it equals `"_id_"` or
  `IndexNames.AggregateIdWithVersionUnique`, throw `EventsCollectionDuplicateKeyConflict` carrying that
  classification; otherwise rethrow `ex` unchanged. `onBeforeCommit` runs after this call, as a separate
  statement outside this `try`/`catch`, so even its own `ClientBulkWriteException` can never be
  misattributed here.

Replace the outer catch's filter and body accordingly: `catch (EventsCollectionDuplicateKeyConflict
ex)` — no longer a generic `MongoException`/`IsDuplicateKeyException` filter — throws
`MongoDuplicateEventException(..., ex.InnerException)` when `ex`'s classification is the `_id_` index,
or returns `AppendAttempt.Conflicted(new MongoConcurrencyException(..., ex.InnerException))` when it is
the concurrency index, exactly as today. Any other exception — a raw duplicate-key `MongoException`
from the read model, the checkpoint, or `onBeforeCommit`, or an unrecognized-index duplicate rethrown
unchanged by either local catch above — is not an `EventsCollectionDuplicateKeyConflict` and is
therefore never caught by this narrowed outer catch; it propagates out of `AppendOnceAsync` unchanged,
satisfying AC1.

### 2. Detect an idempotent store-assigned retry before the revision/concurrency/validation checks

For a store-assigned append (`storeAssigned == true`), `AppendOnceAsync` must check whether the
batch's event IDs are already stored before evaluating the read-model/head revision conflict
(line 247), the `expectedRevision` check (line 254), and `ApplyEvents` (line 269). All three can
report the retry as a conflict or a validation failure — the `expectedRevision` check throws
`MongoConcurrencyException` synchronously when the retry still carries the `expectedRevision` the
caller read before the first attempt, and `ApplyEvents` can throw `MongoEventValidationException`
when it re-runs `Execute` against the read model the first attempt already updated — and none of
them ever reaches the insert that would otherwise reveal the duplicate `_id`. The check mirrors the
existing lookup in `CreateStaleVersionExceptionAsync` (querying the events collection for any of
`eventList`'s IDs): extend that method, or extract its lookup into a shared helper, so both the
existing explicit-version path (`ValidatePositionsAsync`) and the new store-assigned path use the
same stored-ID lookup and the same choice between `MongoDuplicateEventException` and the caller's
original error. When the batch's IDs are already stored, `AppendOnceAsync` must return/throw
`MongoDuplicateEventException` immediately, without evaluating the revision/`expectedRevision`
checks or running `ApplyEvents`. When they are not, behavior is unchanged.

This adds one indexed lookup by `_id` to every store-assigned append attempt, not only retries. The
explicit-version path only pays the equivalent lookup for a stale append (`ValidatePositionsAsync`
calls `CreateStaleVersionExceptionAsync` only when `firstVersion <= head.Version`); a fresh
explicit-version append does not. The store-assigned path cannot tell a retry from a fresh append by
position alone — a store-assigned append has no caller-supplied version to compare against the head
— so it must look the IDs up on every attempt to make the same distinction.

### 3. Reject unregistered event types before any write

In `AppendEventsAsync`, after the existing `PersonalDataErased<TAggregate>` guard
(`MongoEventStore.cs:647–654`) and before `PrepareEvents` is called, validate that every event's
runtime type is either `typeof(PersonalDataErased<TAggregate>)` or a key of `_options.EventTypes`.
Throw `ArgumentException` (parameter name `events`) naming the unregistered type and pointing at
`WithEvent<TEvent>()`, matching the style of the existing guard clauses in this method. This check is
synchronous and runs before any Mongo call, so no partial write is possible.

### Contracts and compatibility

- `MongoDuplicateEventException`, `MongoConcurrencyException`, and `MongoEventValidationException`
  remain unchanged; only which one is thrown for the affected scenarios changes.
- No public API shape changes. `MongoEventStoreOptions<TAggregate>.EventTypes` already exists and is
  sufficient to validate against. `EventsCollectionDuplicateKeyConflict` (Technical Details §1) is a
  private implementation-internal signal type, not public API; callers never see it.
- Existing callers whose `onBeforeCommit` callback or checkpoint configuration happens to collide
  with a duplicate `_id` will now see that failure surface as the real underlying `MongoException`
  instead of a swallowed `MongoDuplicateEventException`; this is the documented bug fix and is not a
  compatibility concern since the previous behavior silently discarded data.

### Testing

All existing event-store append tests under `tests/Chaos.Mongo.EventStore.Tests/Integration/` are
Testcontainers-based integration tests exercising `AppendEventsAsync` against a real MongoDB, which
is the only way to produce authentic server-side duplicate-key messages for case 1 and 2, including
the namespace-collision and zero-version-retry regressions called out in the acceptance criteria
above. New tests for the first five behavioral acceptance criteria belong alongside them (for example
in `EventStoreIntegrationTests.cs` or `StoreAssignedAppendIntegrationTests.cs`), following
`Method_Scenario_ExpectedResult` naming and the sociable-test preference from `tests/AGENTS.md`. The
bulk-write-optimization duplicate-`_id` scenario is a separate acceptance criterion specifically
because it exercises the `ClientBulkWriteException`/write-error-index branch of Technical Details §1
rather than the message-based branch the other duplicate-key criteria exercise; it belongs in
`BulkWriteOptimizationIntegrationTests.cs`, the existing home for `BulkWriteOptimizationEnabled`
scenarios, with `BulkWriteOptimizationEnabled` set and a genuine duplicate `_id` forced on an event
document. The unregistered-event-type guard (case 3) is a pure in-memory check and can additionally be
covered by constructing the event list directly without requiring Mongo, if a suitable fake
`IMongoHelper` already exists in this project's test helpers; otherwise, cover it with the same
integration-test host as the other cases.
