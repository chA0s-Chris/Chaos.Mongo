# Count Queue Delivery Attempts and Align MaxRetries with the Outbox

## Rationale

[Issue #239](https://github.com/chA0s-Chris/Chaos.Mongo/issues/239) reports two related defects that both change what
a stored field means, so neither can wait until after the 1.0 freeze.

First, `MaxRetries` has different meanings on the two retry mechanisms: `WithMaxRetries(5)` allows 6 total delivery
attempts on a queue (`MongoQueueSubscription.HandleFailedQueueItemAsync`, comparing `RetryCount <= MaxRetries` after
incrementing it to 1 on the first failure) but only 5 on the outbox (`OutboxProcessor.HandleFailureAsync:106`,
comparing `newRetryCount >= MaxRetries`).

Second, and more seriously, the queue's `RetryCount` only increases inside the `catch (Exception e)` block around
`IMongoQueuePayloadHandler<TPayload>.HandlePayloadAsync` (`MongoQueueSubscription.cs:444-448`). A process crash mid-handler,
a lock lease that expires while a handler is hung, a payload that fails to deserialize into `TPayload` when the
driver materializes the acquire `FindOneAndUpdateAsync`'s result, or a DI failure resolving the handler
(`_payloadHandlerFactory.CreateHandler<TPayload>` at `MongoQueueSubscription.cs:432`, outside the inner `try`) all
skip that `catch` entirely (caught only by the outer `catch (Exception e)` at `:497-500`, which just logs). None of
these ever increments `RetryCount`, so a poison item can cycle through acquire → crash/fail-before-handler forever
without ever reaching the terminal state that exists specifically to stop that.

This plan unifies `MaxRetries`'s meaning across the queue and the outbox, and moves attempt counting onto the atomic
acquire/claim `FindOneAndUpdateAsync` that already runs on every delivery attempt regardless of why a previous
attempt stopped, so every attempt is counted and the terminal transition is reachable the same way regardless of
cause.

## Acceptance Criteria

- [x] `MongoQueueDefinition.MaxRetries` and `OutboxOptions.MaxRetries` document and implement the same meaning:
      the number of retries allowed after the first attempt, so a configured value of `N` allows `N + 1` total
      delivery attempts before the item/message becomes terminal (the queue's current behavior). The queue's
      `null` ("unlimited retries") stays queue-only; the outbox's `MaxRetries` stays a non-nullable `Int32`.
- [x] A queue item whose handler always throws, and an outbox message whose publisher always throws, each reach
      their terminal state (`IsClosed`/`IsTerminal` for the queue, `State == Failed` for the outbox) after exactly
      `MaxRetries + 1` attempts for the same configured `MaxRetries`, with tests covering both.
- [x] A queue item that is reclaimed after its lock lease expires — without the handler ever throwing (simulating a
      crashed or hung consumer) — counts each reclaim that is granted a new attempt, and the first reclaim after
      `MaxRetries + 1` attempts have already been granted transitions the item directly to the terminal state
      without granting that reclaim a lock or incrementing `AttemptCount` further, covered by a new test.
- [x] A queue item whose stored payload can never deserialize into `TPayload` reaches `IsTerminal: true` after
      `MaxRetries + 1` acquire attempts, covered by a new integration test that inserts a document whose payload
      shape doesn't match `TPayload`, lets the subscription run, and asserts the final state through a
      `BsonDocument`-typed query (the typed collection can't deserialize the poisoned document even after it turns
      terminal).
- [x] A queue item whose `IMongoQueuePayloadHandler<TPayload>` resolution fails (a DI failure) on every attempt
      reaches the terminal state after exactly `MaxRetries + 1` attempts, covered by a new test using a handler
      factory that persistently throws for at least that many calls (an exhausted-budget fixture, not one that
      eventually succeeds). A handler factory that fails only transiently and then resolves successfully before
      `MaxRetries + 1` attempts are exhausted must not be required to reach the terminal state; cover that as a
      separate scenario if desired.
- [x] `MongoQueueItem.RetryCount` and `OutboxMessage.RetryCount` are renamed to `AttemptCount` and widened from
      `Int32` to `Int64`, with XML docs describing an attempt as "the item/message was acquired/claimed for
      processing," not "processing failed" — an item that succeeds on its first acquisition has `AttemptCount == 1`.
      `MaxRetries` itself stays `Int32` with its existing builder validation unchanged. Both packages'
      `PublicAPI.Shipped.txt`/`PublicAPI.Unshipped.txt` reflect the rename and the type change, and the persisted
      element name pinned by [#243](https://github.com/chA0s-Chris/Chaos.Mongo/issues/243)
      (`SetElementName(nameof(...))`) follows the rename.
- [x] A queue item and an outbox message each reach `AttemptCount == Int32.MaxValue + 1` (`2_147_483_648`) without a
      numeric-overflow exception or truncation when `MaxRetries` is configured to `Int32.MaxValue` and a prior
      `AttemptCount` of `Int32.MaxValue` is already recorded: the next acquire/claim grants that attempt and records
      the correct 64-bit count, a resolved failure at that count terminalizes immediately, and an abandoned
      (crashed) attempt at that count terminalizes on the next reclaim without granting a further attempt or
      incrementing `AttemptCount` again — covered by a new seeded-boundary test for both the queue and the outbox.
- [x] `docs/queues.md` and `docs/transactional-outbox.md` state that every lock acquisition/claim counts as a
      delivery attempt — not only a thrown handler/publisher exception — and that `MaxRetries` means the same thing
      on both.
- [x] `docs/migrating-from-0.x.md` gets a new entry documenting the `RetryCount` → `AttemptCount` rename, the
      changed `MaxRetries` meaning on the outbox (an unconfigured outbox now allows 6 attempts, not 5), and that an
      item/message already in flight when upgrading has no `AttemptCount` field yet, so its next acquisition is
      counted as attempt 1 — any attempts it made before the upgrade are not carried over.
- [x] The Release build (`dotnet build -c Release Chaos.Mongo.slnx`) produces no warnings, and the complete test
      suite passes while maintaining the repository's required merged line coverage.

## Technical Details

### Unifying `MaxRetries`

Change `OutboxProcessor.HandleFailureAsync`'s comparison from `newRetryCount >= _options.MaxRetries` to the
already-known-`AttemptCount`-based `AttemptCount > MaxRetries` shape defined below (the same comparison the queue's
`HandleFailedQueueItemAsync` uses), so both mechanisms require the same configured value to exhaust the same number
of total attempts. Update the XML docs on
`MongoQueueDefinition.MaxRetries`, `MongoQueueBuilder<TPayload>.WithMaxRetries`, `OutboxOptions.MaxRetries`, and
`OutboxBuilder.WithMaxRetries` to state the unified meaning explicitly. Leave `WithMaxRetries`'s existing argument
validation on both builders unchanged (queue: rejects `<= 0`, pointing to `WithNoRetry()`; outbox: rejects `< 0`);
do not add a `WithNoRetry()` to `OutboxBuilder` — `WithMaxRetries(0)` already means "terminal after the first
failure" under the unified meaning, matching the queue's `WithNoRetry()` behavior.

### Renaming `RetryCount` to `AttemptCount`

Rename `MongoQueueItem.RetryCount` → `AttemptCount` and `OutboxMessage.RetryCount` → `AttemptCount`, widening the
property's CLR type from `Int32` to `Int64` (`long`) in the same change. The rename reflects the semantic change
below (the field now counts every acquisition, not every failure), and avoids a misleading property name once an
item that succeeds on its very first try carries a non-zero count. The widening closes a numeric-overflow gap: the
unchanged builder validation on both `MongoQueueBuilder<TPayload>.WithMaxRetries` (rejects `<= 0`) and
`OutboxBuilder.WithMaxRetries` (rejects `< 0`) already accepts `MaxRetries = Int32.MaxValue`, and the grant
comparison below (`<= MaxRetries` grants, using the existing, pre-update count) deliberately grants one further
attempt whenever the stored count still equals `MaxRetries`, so an `Int32`-typed counter could be asked to hold
`Int32.MaxValue + 1 = 2_147_483_648`, which does not fit. `MaxRetries` itself stays `Int32` and its existing
validation is unchanged — only the attempt counter, not the configured limit, needs the wider range. Update:

- The `SetElementName(nameof(...))` pin #243 added for this member in the queue's and outbox's class-map
  registration, so the persisted element name follows the rename.
- `PublicAPI.Shipped.txt`/`PublicAPI.Unshipped.txt` in both `src/Chaos.Mongo` and `src/Chaos.Mongo.Outbox` (the
  Roslyn public-API analyzer enforces the exact diff at build time).
- Every internal usage (`failedItem.RetryCount`, `currentRetryCount`/`newRetryCount` parameters in
  `OutboxProcessor`, log message templates).
- `MongoQueueItem.IsTerminal`'s XML doc (`MongoQueueItem.cs:48-51`), which currently says the item "exhausted its
  retry policy": update it to also cover reaching the limit via a crash, a lease-expiry reclaim, a DI failure, or an
  undeserializable payload, not only a handler failure.

### Counting every acquire/claim atomically, not every failure

Both mechanisms decide, in the same atomic claim/acquire operation, whether to **grant** a new attempt or to
transition directly to **terminal**, using the existing, pre-update `AttemptCount` compared against `MaxRetries` —
never the post-increment value, and never `MaxRetries + 1`:

- **Grant**, when `MaxRetries` is `null` (queue only) or the existing `AttemptCount` (missing treated as `0`) is
  `<= MaxRetries`: increment `AttemptCount` and apply today's normal lock (`IsLocked: true`, `LockedUtc: now`, plus
  a fresh `LockId` for the outbox). This is the *only* branch that increments `AttemptCount`, and it is the same
  branch regardless of whether `MaxRetries` is configured — the queue's unlimited case is no longer a separate
  code path that happens to omit the lock fields.
- **Terminal, without incrementing and without granting a lock**, when `MaxRetries` has a value and the existing
  `AttemptCount` already exceeds it (`AttemptCount > MaxRetries`): `IsClosed`/`State → Failed`: `true`,
  `ClosedUtc`/`FailedUtc: now`, `IsLocked: false`, lock fields removed. This branch is reached only for an item/
  message whose last granted attempt was never resolved by an in-process failure handler — a crash, a hung handler
  whose lease expired, or (outbox) a process death between claim and `HandleFailureAsync`/`MarkProcessedAsync` —
  because every *resolved* failure (below) already terminalizes immediately once its own already-known
  `AttemptCount` exceeds `MaxRetries`, before a further acquire/claim is possible.

Comparing the pre-update count this way (`<= MaxRetries` grants, `> MaxRetries` terminalizes) keeps the two
mechanisms' boundaries identical to each other and to `HandleFailedQueueItemAsync`'s/`HandleFailureAsync`'s own
already-known-count comparison below, and avoids ever overflowing the *comparison itself* by computing
`MaxRetries + 1` (unlike today's outbox check). Because `AttemptCount` is `Int64` while `MaxRetries` stays `Int32`
(see "Renaming `RetryCount` to `AttemptCount`" above), the highest value the grant branch ever needs to write —
`Int32.MaxValue + 1 = 2_147_483_648`, one more than the highest value `MaxRetries` can hold — is far inside `Int64`'s
range, so no configured `MaxRetries` value, including `Int32.MaxValue` itself, can make the increment overflow the
counter. The pipeline runs entirely inside MongoDB's aggregation arithmetic, which freely promotes between 32- and
64-bit integers as a computed value grows, and the C# driver reads either representation into the `Int64`
`AttemptCount` property without a cast, so no pipeline-side type annotation is needed beyond declaring the property
as `long`. Worked example for `MaxRetries = 1` (2 total attempts): attempt 1 grants
(`AttemptCount` 0→1); a failure here is `1 <= 1`, so it retries normally. Attempt 2 grants (`AttemptCount` 1→2); a
failure here is `2 > 1`, so it terminalizes immediately. If attempt 2 instead crashes before any failure handler
runs, the item stays locked until the lease expires, and the *next* acquire sees the existing `AttemptCount` (2)
`> 1` and terminalizes without granting a third attempt — `AttemptCount` stays at `2`, so "exactly `MaxRetries + 1`
attempts" holds exactly, because that reclaim was never counted as a new attempt. Explicitly cover both exhaustion
boundaries in tests: `MaxRetries = 0` (one total attempt, the queue's existing `WithNoRetry()` case) and
`MaxRetries = 1` (two total attempts, covering both "the second granted attempt fails and terminalizes immediately"
and "the second granted attempt crashes, and a third acquire terminalizes it without granting it").

Illustrative (not exact) shape of the grant/terminal `$set` stage, once `grant` (`MaxRetries` is null, or the
existing, pre-increment `AttemptCount` is `<= MaxRetries`) is computed:

```js
{ $set: {
    AttemptCount: { $cond: [grant, { $add: [{ $ifNull: ["$AttemptCount", 0] }, 1] }, "$AttemptCount"] },
    IsClosed:   { $cond: [grant, "$IsClosed", true] },
    IsTerminal: { $cond: [grant, "$IsTerminal", true] },
    ClosedUtc:  { $cond: [grant, "$$REMOVE", now] },
    IsLocked:   { $cond: [grant, true, false] },
    LockedUtc:  { $cond: [grant, now, "$$REMOVE"] }
} }
```

**Queue** (`MongoQueueSubscription<TPayload>.ProcessQueueItemAsync`, acquire call at `:392-401`): replace the plain
`Set(IsLocked, true).Set(LockedUtc, now)` update with an aggregation-pipeline update implementing the grant/terminal
branches above (`Builders<MongoQueueItem<TPayload>>.Update.Pipeline(...)`, confirmed to compile against the pinned
`MongoDB.Driver` 3.9.0 and supported since MongoDB 4.2, well under this project's MongoDB 7.0 floor). The pipeline's
own output stays `MongoQueueItem<TPayload>` — it is what gets written — but the **returned** document is reshaped
with `FindOneAndUpdateOptions<MongoQueueItem<TPayload>, MongoQueueItem>.Projection =
Builders<MongoQueueItem<TPayload>>.Projection.As<MongoQueueItem>()` (confirmed to compile against the pinned
driver), so the client reads back only the non-generic `MongoQueueItem` base type — `AttemptCount`, `IsLocked`,
`LockedUtc`, `IsTerminal`, `IsClosed`, `CreatedUtc`, `Id` — and never attempts to deserialize `Payload`/`PayloadType`
at all. This is what makes the acquire itself safe for a permanently undeserializable payload: the client never
attempts to deserialize `Payload` while deciding the outcome, even for a poisoned item. Keep `ReturnDocument.Before`
exactly as today (`:399`) — do **not** switch to `After`: the existing "recovered lock" diagnostics at `:418-428`
already read the pre-update `IsLocked`/`LockedUtc` to compute `recoveredLockAge` for
`MongoQueueDiagnostics.RecordLockRecovered`, and the projected `Before` image still carries those same pre-update
values unchanged, so that telemetry needs no other change.

Because `ReturnDocument.Before` returns the document's state *before* the pipeline applied, the projected result's
own `AttemptCount`/`IsTerminal` fields reflect that prior state for either outcome and cannot tell the client whether
*this* acquisition granted or terminalized. Instead, derive the outcome client-side from the exact same predicate the
pipeline itself evaluated, applied to the projected `AttemptCount` the client already has:
`grant = MaxRetries is null || (projectedResult.AttemptCount ?? 0) <= MaxRetries`. Because the acquire's own filter
(`CreateAvailableQueueItemFilter`) already requires a non-terminal, available document, this is exactly the document
state the pipeline matched and decided from, so the client's recomputation and the pipeline's write always agree.

When `grant` is `false`: this acquisition's own terminal branch just fired atomically (lock cleared,
`IsTerminal`/`IsClosed` set) — log the terminal transition, record the `processing.failed` ("terminal") metric, and
return. No DI scope, no handler invocation, no payload read of any kind, closing the DI-resolution-failure gap
(`:432`) for this path. When `grant` is `true`: log the existing "recovered lock" diagnostics unchanged if the
projected result's `IsLocked` was already `true`, then perform a **second, separate, typed** read of the same
document (by `Id`, typed as `MongoQueueItem<TPayload>`) to obtain `Payload` for the handler. If that typed read
throws because the stored payload can never deserialize into `TPayload`, handle it exactly like a handler exception
below — this is what closes the poison-payload gap without requiring the acquire's own result to carry a
client-readable payload. This second read permanently doubles the round trips to MongoDB for every successful
acquisition (one pipeline update plus one indexed point read by `Id`, versus today's single call); this is accepted
as the cost of correctness, since a single combined typed read cannot distinguish a poisoned payload on a
just-granted attempt from one a different acquisition already terminalized, which would reopen the
deserialization-failure gap this design closes. No microbenchmark is required under `benchmarks/AGENTS.md`'s
convention, because the added operation is one indexed point read by `_id`, not a scan or aggregation with uncertain
cost.

Thread the client-computed just-granted `AttemptCount` — `(projectedResult.AttemptCount ?? 0) + 1`, **not** the
projected result's own pre-update `AttemptCount` — through to `HandleFailedQueueItemAsync`, for all three failure
sources that now share it: a handler exception, a DI resolution failure, and the secondary typed-read failure above.
`HandleFailedQueueItemAsync` no longer increments `AttemptCount` itself, so its own `FindOneAndUpdateAsync` at
`:271-278` (today's `Inc(x => x.RetryCount, 1)` under `CreateLockOwnershipFilter`) is removed entirely, not replaced
with an equivalent write: comparing the already-known count directly, `AttemptCount > MaxRetries` terminalizes
immediately via the existing ownership-checked `UpdateOneAsync` at `:299-307` (unlock,
`IsTerminal`/`IsClosed`/`ClosedUtc`) exactly as today; otherwise today's "retry after lease expiry" branch
(`:288-297`) already performs no database write of its own — only logging — so it needs no change beyond reading
`AttemptCount` from the parameter instead of a fresh query.

**Outbox** (`OutboxProcessor.ProcessMessageAsync`, claim call at `:342-354`): apply the same grant/terminal
invariant to the claim update, which also becomes a pipeline update (`Builders<OutboxMessage>.Update.Pipeline(...)`)
for the same reason as the queue — a message that crashes the process between claim and
`HandleFailureAsync`/`MarkProcessedAsync` must still reach terminal via a later claim attempt, exactly like the
queue's crash case. Grant: `AttemptCount` increments, `IsLocked: true`, `LockedUtc: now`, `LockId: <new id>` (the
client-generated GUID is a pipeline literal, same as any other `$set` value). Terminal: `State: Failed`,
`FailedUtc: now`, `IsLocked: false`, lock fields removed, `AttemptCount` unchanged — reached without calling
`IOutboxPublisher.PublishAsync`. Because `State == Pending` is part of the claim filter, and this claim-time
terminal branch is the only place besides `HandleFailureAsync` that sets `State: Failed`, a message can only reach
it by having crashed on an earlier granted attempt, not by an ordinary resolved failure (those self-terminalize in
`HandleFailureAsync` already). Keep `ReturnDocument.After` and the full `OutboxMessage` result type unchanged
(unlike the queue, `Payload` is always read as `BsonDocument` and never risks a deserialization failure during the
claim itself, so no projection change is needed here). `HandleFailureAsync` and the `MarkProcessedAsync`
finalize-exception path stop incrementing (`var newRetryCount = currentRetryCount + 1;` at `:100` is removed) and
compare the already-known `claimed.AttemptCount` directly: `AttemptCount > MaxRetries` is permanent failure,
matching the queue's comparison and avoiding the same comparison overflow as the queue (see above); `OutboxMessage.AttemptCount`
is widened to `Int64` for the same reason as the queue's counter (see "Renaming `RetryCount` to `AttemptCount`"
above), so the same `MaxRetries = Int32.MaxValue` boundary remains representable without an outbox-specific gap.

Note that a DI failure resolving `IOutboxPublisher` (`ProcessBatchAsync:247-250`) happens once per batch, before any
message in that batch is claimed, so it cannot silently skip counting a specific message's attempt the way the
queue's per-item DI resolution could: no message is claimed (hence no attempt counted) until the DI failure is
resolved, and every eligible message is simply retried on the next poll.

### Diagnostics and logging

Record `chaos.mongo.queue.processing.failed` ("terminal" tag) from exactly two places: the acquire pipeline's own
terminal branch (crash/lease-expiry exhaustion, using only the projected `MongoQueueItem` result) and
`HandleFailedQueueItemAsync`'s already-known-count terminal branch (handler exception, DI failure, or
poison-payload secondary-read failure). Update both the queue's and outbox's log message templates for the renamed
field, the pre-update-count comparison, and the two terminal paths; the outbox's equivalent metric/log in
`HandleFailureAsync` and its new claim-time terminal branch follow the same split.

### Documentation

- `docs/queues.md`'s "Recovery and retention" section currently states only "a handler failure increments the retry
  count and makes the item eligible for another attempt" and describes a process crash as leaving a lock that
  "becomes recoverable" with no mention of counting; rewrite both bullets to state that every lock
  acquisition — including a lease-expiry reclaim after a crash — counts as a delivery attempt.
- `docs/transactional-outbox.md`'s options table (`WithMaxRetries(int) | 5 | ...`) and its "`RetryCount` is
  incremented" / "Once the retry count reaches `MaxRetries`..." prose (`:574,579`) need both the rename and the
  corrected attempt semantics.
- `docs/migrating-from-0.x.md` gets a new section (alongside the existing breaking-change entries for this release)
  covering the rename, the outbox's changed default attempt count (5 → 6 for an unconfigured outbox), and the
  in-flight/upgrade behavior decided above.
