# Separate stream position (Version) from aggregate revision (Revision) to support observational events

> Issue: [#164](https://github.com/chA0s-Chris/Chaos.Mongo/issues/164)

## Rationale

`Version` currently carries two meanings at once: it is the stream position that the unique `(AggregateId, Version)` index, `GetEventStream` and the concurrency checks operate on, and it is the aggregate revision that identifies the read-model state and triggers checkpoints. Events that only document something about an aggregate, such as a read access, therefore cannot be appended without pretending to produce a new state: they bump the version, replace the read model, may create a checkpoint, and cause spurious `MongoConcurrencyException`s for writers whose state assumption was correct.

This plan keeps `Version` as the stream position and introduces `Revision` as the aggregate revision. Observational events become part of the ordered, hash-chained stream without changing the aggregate, its revision, or its checkpoints. The work remains one story and is delivered as a two-layer GitHub stack with separate verification boundaries: the model and append semantics establish the persisted contract, and the upper layer completes the concurrency and reconstruction APIs against that contract.

## Acceptance Criteria

### Layer 1: Model and append semantics

- [x] `Event<TAggregate>` exposes a new `Revision` property that the event store sets on append (caller-supplied values are overwritten) and that is stored with every event. `IAggregate` and `Aggregate` expose a new `Revision` property; `IAggregate.Version` is documented as the stream position of the last state-changing event.
- [x] A new abstract `ObservationalEvent<TAggregate>` derives from `Event<TAggregate>`, seals `Execute`, and offers a protected virtual `Validate(TAggregate)` hook that defaults to doing nothing. Observational event types are registered through the existing `WithEvent<TEvent>()`.
- [x] Appending state-changing events validates caller-supplied positions against the stream head, increments the revision once per event, and replaces the read model. After each state-changing event, the store updates the aggregate's `Version` and `Revision`; later events in the batch observe these values. State-changing-only streams retain existing positions and persistence behavior except for `Revision` elements, corrected checkpoint crossings, and results produced by `Version`-dependent `Execute` implementations. This breaking batch behavior is documented and tested, and the existing test suite passes.
- [x] Appending observational events inserts only the event documents (sealed when integrity protection is enabled), writes neither the read model nor a checkpoint, and records the observed revision on each event. `Validate` is invoked; when it throws, nothing is persisted.
- [x] An observational event whose observed revision would be 0, because no state-changing event precedes it in the stream or earlier in the same batch, fails with `MongoEventValidationException` without persisting anything.
- [x] A batch may mix state-changing and observational events; revisions are assigned per event in order, and the returned aggregate reflects all state-changing events of the batch.
- [x] After every append, the read model's `Version` is the position of the last state-changing event in the stream and its `Revision` is the number of state-changing events. A state-changing append that follows observational events sets `Version` to its own position and increments `Revision` by one; a mixed batch ending with an observational event leaves `Version` at the position of its last state-changing event.
- [x] Explicit-version validation (stale version, idempotent retry, gap, non-sequential batch, mixed aggregates) compares against the stream head and keeps the exception types established by #120.
- [x] In explicit-version mode, append executes only against a read model whose normalized `Revision` matches the stream head's normalized `Revision` (0 for an empty stream); a mismatch fails with `MongoConcurrencyException` without persisting anything.
- [x] An append to a stream whose head is above zero but whose read model is missing fails with `MongoEventStoreException` without persisting anything.
- [x] A checkpoint is written when a batch's revision crosses a multiple of the configured interval, including when a multi-event batch skips over the exact multiple, and never for a batch without state-changing events. Checkpoint documents store the revision alongside the existing position-based identifier.
- [x] `GetAtVersionAsync` skips observational events during replay and returns an aggregate whose `Version` and `Revision` match the read model that the same events would have produced.
- [x] Events, read models and checkpoints stored without a `Revision` element load with `Revision == Version` in every library read path, and such documents are never rewritten.
- [x] Both append paths (typed insert and MongoDB 8 bulk write) persist identical results for state-changing, observational and mixed batches; with integrity protection enabled, mixed streams verify correctly.
- [x] `EventStoreAppendBenchmarks` gains a single-observational-event scenario.
- [x] Integration tests cover every criterion above, including the batch-crossing checkpoint case and a state-changing commit deterministically interleaved between the head and read-model reads, verifying that stale state never replaces committed state; existing tests asserting the previous checkpoint rule or `IAggregate` shape are updated.
- [x] `docs/event-store.md` explains position versus revision, observational events, the first-event rule, the checkpoint rule and the `breaking` change to `IAggregate`.

### Layer 2: Store-assigned positions, revision-based concurrency and reconstruction

- [x] `AppendEventsAsync` accepts an optional `expectedRevision`. When supplied and different from the aggregate's current revision, the append fails with `MongoConcurrencyException` before anything is persisted, in both explicit and store-assigned mode.
- [x] Events whose `Version` is `0` receive store-assigned positions. A batch that mixes zero and non-zero versions fails with `ArgumentException`. Explicit versions keep today's semantics.
- [x] When a store-assigned append loses the position race or loads head and read model with mismatching normalized revisions, the store reloads both, establishes a consistent pair, re-checks `expectedRevision`, re-executes the events against the fresh aggregate and retries. Both conflict causes share a new configurable bound on additional attempts after the first, default 3; exhausting the bound fails with `MongoConcurrencyException`.
- [x] A state-changing append with a matching `expectedRevision` succeeds despite concurrently interleaved observational events; observational appends succeed despite interleaved state-changing appends; a mismatching `expectedRevision` fails. Tests cover these cases with integrity protection enabled and disabled.
- [x] A duplicate event `Id` is never retried and surfaces as `MongoDuplicateEventException`.
- [x] Integration tests cover every criterion above, including a mixed-mode batch, an `expectedRevision` mismatch that persists nothing, a duplicate `Id` that is not retried, and retry exhaustion. A deterministic state-changing commit between the head and read-model reads verifies reload and re-check of `expectedRevision` against the fresh state.
- [x] `docs/event-store.md` describes both concurrency modes, the retry bound and the `breaking` signature change; call sites in tests, benchmarks and documentation are updated.
- [x] `IAggregateRepository<TAggregate>` exposes a new `GetAtRevisionAsync` that reconstructs the state at a revision. `GetAtVersionAsync` keeps its position semantics. Both use the nearest checkpoint at or below the target and replay only state-changing events.
- [x] Replay excludes registered observational types server-side using the scalar discriminator or the last element of a hierarchical discriminator array, so observational documents are neither transferred nor executed. Unknown concrete types, including descendants of registered observational types, still fail during deserialization.
- [x] The configurator creates indexes on the checkpoint collection that serve both the position-based and the revision-based nearest-checkpoint lookup.
- [x] Query-contract tests verify the checkpoint and event filters of both reconstruction methods, including the legacy revision fallback and scalar/hierarchical concrete-discriminator exclusion. Integration tests cover reconstruction with and without checkpoints, interleaved observational events, historical targets and mixed legacy/new streams, and deserialization failure for an unknown descendant of a registered observational type.
- [x] `docs/event-store.md` documents `GetAtRevisionAsync`, the replay cost of observational events between checkpoints, and the one-time backfill for pre-existing checkpoints.

## Technical Details

### Model

```csharp
// Exact shapes of the new members.
public abstract class Event<TAggregate>
{
    public Int64 Version { get; set; }   // stream position, unchanged meaning
    public Int64 Revision { get; set; }  // aggregate revision after this event; set by the store
}

public interface IAggregate
{
    Int64 Version { get; set; }   // stream position of the last state-changing event
    Int64 Revision { get; set; }  // number of state-changing events applied
}

public abstract class ObservationalEvent<TAggregate> : Event<TAggregate>
{
    public sealed override void Execute(TAggregate aggregate) => Validate(aggregate);
    protected virtual void Validate(TAggregate aggregate) { }
}
```

`Revision` is set like `AggregateType`: the store owns it and overwrites whatever the caller supplied. For a state-changing event it is the previous revision plus one; for an observational event it is the revision observed. `Revision` is an ordinary mapped member and therefore part of the hashed bytes of sealed events; nothing in the integrity code changes.

`MongoEventStoreSerializationSetup.RegisterClassMaps` registers each concrete event type with `Event<TAggregate>` as its base class map. `ObservationalEvent<TAggregate>` declares no serialized members, so it needs no class map of its own and the concrete type's discriminator is unaffected. Observational types are detected at registration via `typeof(ObservationalEvent<TAggregate>).IsAssignableFrom(eventType)`. Layer 2 derives the discriminators for the replay filter from the registered class maps (`BsonClassMap.LookupClassMap(type).Discriminator`) after registration, not from the names in `EventTypes`, because `RegisterClassMaps` adopts consumer-registered class maps together with their own discriminators.

### Append

The stream head is the highest `Version` of the aggregate in the events collection, read outside the transaction by the same indexed point read `GetExpectedNextVersionAsync` performs. This adds one indexed point read to every append, an accepted cost of decoupling the head from the read model. The head read includes its normalized `Revision`. The read model no longer tracks the head; `IAggregate.Version` is updated only by state-changing events.

After each state-changing event executes, the store updates the aggregate's `Version` and `Revision` before executing the next event. This is a breaking change from exposing the pre-batch `Version` throughout the append: `Execute` implementations that read it can produce different final state even in batches containing only state-changing events. Per-event metadata keeps append consistent with replay and lets observational validation see the state produced by preceding events.

Invariant: a non-empty stream always has a read model with `Revision >= 1`. The first-event rule enforces it, and `AppendEventsAsync` relies on it: a head above zero without a read model is corruption (or an interrupted rebuild) and fails with `MongoEventStoreException` instead of silently applying events to an empty aggregate, which today would surface as a version-gap `ArgumentException`. This check precedes the first-event rule, so an observational event appended to such a stream also fails with `MongoEventStoreException`.

Before checking `expectedRevision` or executing events, the normalized revisions of the head and read model must match (0 for an empty stream). A mismatch indicates a concurrent state change: explicit-version mode fails with `MongoConcurrencyException` without persisting anything; store-assigned mode reloads both within the shared retry bound described below. The unique `(AggregateId, Version)` index remains the authoritative conflict detector for interleavings after this consistency check.

The head is read before the read model. An internal seam invoked between the two reads, visible to tests via `InternalsVisibleTo`, lets tests commit a competing event deterministically at that point; layer 1 uses it for the interleaving criterion.

Observational events never touch the read model, so concurrent observational appends to one aggregate only contend on the position index, not on the read-model document. The returned aggregate of an observational-only batch is the loaded read model, unchanged. Under the bulk-write path an observational-only batch consists of insert models only.

The checkpoint rule is `floor(newRevision / interval) > floor(oldRevision / interval)`. A checkpoint is a snapshot of the post-batch state keyed by `CheckpointId(AggregateId, Version)` with `Version` still the position, plus a new `Revision` member on `CheckpointDocument<TAggregate>`. Keeping the position-based `_id` leaves existing checkpoint identifiers and the position-based lookup valid without migration; the revision-based lookup uses the separately indexed `Revision` member. Checkpoints therefore no longer sit on exact multiples; lookups already use "nearest at or below", so this is transparent. This replaces the current `lastVersion % interval == 0` rule, which skips checkpoints for batches that jump over a multiple.

In layer 1, `MongoAggregateRepository.GetAtVersionAsync` skips observational events in memory (`is ObservationalEvent<TAggregate>`) and sets `Version` and `Revision` from the last state-changing event replayed. Layer 2 moves the skip server-side.

The observational benchmark scenario is valid only against existing streams because of the first-event rule; the scenario matrix expresses that constraint through `ParamsSource` as `benchmarks/AGENTS.md` requires, rather than running a parameter combination that fails.

### Legacy documents

Documents written before this change have no `Revision` element and deserialize with `Revision == 0`. Because of the invariant above, `Revision == 0 && Version > 0` identifies exactly these documents, and every library read path (append load, `GetEventStream`, repository methods) normalizes them to `Revision = Version`; for checkpoints `_id.Version` is the source. The normalization is in-memory only. Event documents must never be rewritten: adding an element would change the sealed bytes and break the hash chain. Direct access through `IAggregateRepository.Collection` bypasses the normalization, which the documentation states.

Checkpoints are a derived cache. The revision-based lookup of layer 2 cannot match legacy checkpoints that lack the `Revision` element; the position-based lookup still does. The documentation gives the one-time `updateMany` with an aggregation-pipeline `$set` of `Revision` from `_id.Version` for documents missing the element, and names dropping the checkpoint collection as the alternative.

### Concurrency (layer 2)

```csharp
// Exact signature.
Task<TAggregate> AppendEventsAsync(
    IEnumerable<Event<TAggregate>> events,
    Int64? expectedRevision = null,
    Func<IClientSessionHandle, TAggregate, IMongoHelper, CancellationToken, Task>? onBeforeCommit = null,
    CancellationToken cancellationToken = default);
```

Inserting the parameter after `events` breaks positional callers of `onBeforeCommit`; the library is pre-1.0 and the PR carries the `breaking` label. `Version == 0` means "assign", mirroring `Id == Guid.Empty`. `expectedRevision` is checked whenever supplied, in both modes, against the read model's revision before anything is executed or persisted.

The retry loop wraps load, validation, execution, serialization and the transaction. It runs only for store-assigned batches and consumes an additional attempt for either a mismatching head/read-model revision pair or a duplicate key on the position index; a duplicate on `_id` is an idempotency conflict and is never retried. Each retry reloads head and read model, establishes matching normalized revisions and re-checks `expectedRevision` before re-executing the events against the fresh aggregate, so `Execute` must be deterministic, which the existing in-memory validation already requires. Event documents are re-serialized per attempt because `Version` and `Revision` change; sealing inside the transaction picks up the final values. `onBeforeCommit` runs again on retry; its previous effects were rolled back with the aborted transaction, which the existing contract already assumes. Both conflict causes share one bound on additional attempts after the first, exposed through a new `MongoEventStoreOptions<TAggregate>` member (default 3, builder method following the `With*` pattern); exhaustion throws `MongoConcurrencyException` whose message names the bound.

Retry exhaustion reuses the test seam described under Append to insert a competing position before the transaction starts. Racing concurrent writers is used only for the interleaving criteria, where any interleaving order must succeed.

### Reconstruction (layer 2)

`GetAtRevisionAsync(Guid aggregateId, Int64 revision, CancellationToken cancellationToken = default)` mirrors `GetAtVersionAsync`. Both event filters include `AggregateId` and a `Version` range starting after the checkpoint. Position-based replay bounds `Version` by the target. Revision-based replay uses `(Revision <= target) OR (Revision is missing AND Version <= target)`. Legacy events are normalized in memory after loading; event documents are never rewritten.

Replay excludes events server-side only when their concrete discriminator matches a registered observational type. For hierarchical discriminator arrays, the concrete discriminator is the last element; for scalar discriminators, it is the scalar value. This requires an `$expr` clause (`$arrayElemAt` with index `-1` when `$isArray`, otherwise the scalar) compared against the observational discriminators; `$nin` on the element would also exclude unknown descendants. The `AggregateId` and `Version` range remain index-served. Unknown concrete types remain in the query results and fail during deserialization, even when an ancestor is a registered observational type. The discriminator element name comes from `BsonSerializer.LookupDiscriminatorConvention(typeof(Event<TAggregate>)).ElementName`; event-type-name constants must not be assumed. Query-contract assertions cover both scalar and hierarchical shapes.

The checkpoint collection has no index today, so both nearest-checkpoint lookups scan the collection. `MongoEventStoreConfigurator<TAggregate>` creates `{ "_id.AggregateId": 1, "_id.Version": -1 }` and `{ "_id.AggregateId": 1, "Revision": -1 }` with names added to `IndexNames`, using the existing `CreateOneOrUpdateAsync` pattern.

Replay cost between two checkpoints grows with the number of observational events in between. A position-distance checkpoint trigger is deliberately out of scope; the documentation notes the trade-off.

### Stack Design

1. `0164-event-store-separate-stream-position-model` — `Revision` on events, aggregates and checkpoints, `ObservationalEvent<TAggregate>`, head-based append, observational append semantics, checkpoint crossing rule, legacy normalization, benchmark scenario.
2. `0164-event-store-separate-stream-position-concurrency` — complete the concurrency and reconstruction APIs with store-assigned positions, `expectedRevision`, bounded retry, `GetAtRevisionAsync`, server-side replay filtering, checkpoint indexes and backfill documentation; depends on the model layer's persisted revision and observational-event contract.

Both layer PRs carry `enhancement` and `breaking`. Layer 1 references the issue with `Refs #164`; layer 2 closes it with `Closes #164`.
