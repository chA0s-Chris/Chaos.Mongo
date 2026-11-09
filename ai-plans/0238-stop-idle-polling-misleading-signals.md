# Stop idle queue polling and misleading queue signals

## Rationale

[Issue #238](https://github.com/chA0s-Chris/Chaos.Mongo/issues/238) identifies three behaviors in
`src/Chaos.Mongo/Queues/MongoQueueSubscription.cs` that make a healthy queue look, or act, busier than it is:

- When `ProcessQueueItemsAsync` finds no available items, it delays 100 ms and releases its own wait signal
  (`:679-686`), so an idle queue re-queries roughly ten times per second instead of waiting for the
  `leaseRecoveryWakeInterval` it already computes. The comment attributes this to a race between the initial
  catch-up signal and the change stream becoming active.
- `ProcessQueueItemAsync` records `chaos.mongo.queue.lock.recovered` and logs a recovery warning whenever the
  reacquired item's pre-update `IsLocked` was `true` (`:523-533`). `HandleFailedQueueItemAsync`'s retry branch
  (`:368-377`) resolves a failure without writing anything, so the item stays locked until its lease naturally
  expires; the next acquisition sees the same `IsLocked == true` a genuine crash-abandoned lock would leave, so
  an ordinary, already-logged retry is indistinguishable from real lock recovery today.
- `MonitorChangeStreamAsync`'s retry loop (`:423-449`) catches every exception the same way: log at `Error` with
  the full stack trace, wait a fixed 300 ms, retry. Change streams require a replica set or sharded cluster; on a
  standalone server, `WatchAsync` always fails the same way and this loop retries it forever, producing an error
  with a stack trace roughly three times a second indefinitely. `docs/requirements.md` only calls out transactions
  and the event store/outbox as needing a replica set, implying queues work unconditionally on a standalone
  server, which is misleading once this failure is handled gracefully instead of as a log flood.

This plan removes the self-signal, gives `lock.recovered` a signal it can trust, and turns the standalone case
into a single clear message with backoff for genuine transient failures, then documents what standalone servers
actually support.

## Acceptance Criteria

- [ ] An idle queue (no available items, no change-stream activity) issues an availability query no more often
      than once per the subscription's wake interval (`min(LockLeaseTime, MaxLeaseRecoveryWakeInterval)`), verified
      by a test that simulates an idle period across multiple wake intervals and counts queries.
- [ ] A queue item published after `StartAsync` is called, but before the change stream has started reporting
      inserts, is still picked up without waiting for a full `leaseRecoveryWakeInterval`, verified by a test.
- [ ] A queue item whose handler throws and is granted a retry (attempt count still within `MaxRetries`) does not
      record `chaos.mongo.queue.lock.recovered` or log a recovery warning on its next, successful reacquisition,
      verified by a test. A lock abandoned by a crash (no resolved failure ever recorded) still records
      `lock.recovered` on reacquisition, verified by a test.
- [ ] When `MonitorChangeStreamAsync`'s `WatchAsync` call fails because the server is not a replica set or sharded
      cluster, the subscription logs exactly one clear message identifying that cause (no repeated error-level
      stack traces) and stops retrying the change stream for that subscription; queue processing continues through
      polling alone, verified by a test.
- [ ] Any other `MonitorChangeStreamAsync` failure retries with increasing backoff (capped) instead of a fixed
      300 ms delay, verified by a test.
- [ ] After one or more idle wake-interval timeouts, a burst of newly published items is picked up within one
      wake interval of the first item's insert, not delayed by the number of preceding idle timeouts, verified by
      a test.
- [ ] `docs/requirements.md` and `docs/queues.md` state that queues work on a standalone server through polling
      only, without real-time change-stream notifications.
- [ ] The Release build has no warnings and the full test suite passes.

## Technical Details

### Removing the idle self-signal and fixing startup accounting

`StartAsync` (`:772-795`) releases `_signalSemaphore` once (`:791`) in addition to the semaphore's own initial
count of 1 (`:53`), so two permits already exist before either background task starts. The gap the 100 ms
self-resignal loop (`:679-686`) actually papers over is a race between that startup signal and
`MonitorChangeStreamAsync` (`:412-449`) opening its change-stream cursor on a separate `Task.Run`: an item
inserted after the startup signal's query ran, but before the change stream is actually watching, produces no
signal at all under the old code without the self-resignal fallback.

Close that race instead of masking it, and collapse the two startup permits into one at the same time:

- Change `_signalSemaphore`'s construction (`:53`) from `new(1, Int32.MaxValue)` to `new(0, Int32.MaxValue)`, so
  the only startup permit is the one `StartAsync` explicitly releases.
- Extract a private helper (e.g. `TryOpenChangeStreamAsync`) that opens one change-stream cursor and classifies
  the outcome: success returns the open cursor; `MongoCommandException` with `Code == 40573` (standalone/non-replica-set
  server) logs the single warning described below and returns "unsupported"; any other exception is handled exactly
  like a retry-loop failure (see change-stream failure handling below) and returns "failed". Both `StartAsync` and
  `MonitorChangeStreamAsync`'s retry loop call this same helper, so the initial open and every later reopen share one
  failure policy.
- In `StartAsync`, call the helper once before releasing the startup signal and before starting either background
  task, then branch on the result: on success, hand the already-open cursor to `MonitorChangeStreamAsync` for its
  first watch iteration instead of letting it call `WatchAsync` again; on "unsupported", do not start the monitor
  task at all — there is nothing to retry, and the subscription relies on polling alone, exactly as the
  standalone-server acceptance criterion requires; on "failed" (a transient error on the very first open), still
  start the monitor task, which begins its own retry loop at the initial backoff delay instead of with a pre-opened
  cursor. In every branch `StartAsync` still releases the single startup signal and starts `ProcessQueueItemsAsync`,
  so a transient first-open failure never blocks polling from starting.
- Once a cursor is confirmed open (the initial one or a later retry), every subsequent insert is guaranteed to
  produce a change event, so the single startup signal only needs to cover items that existed at or before that
  point — no repeated re-signal is needed. If the initial open instead failed transiently, items published during
  the ensuing backoff window are still bounded by the existing wake-interval polling backstop, same as today; no
  acceptance criterion claims change-stream coverage during that narrow, already-logged failure window.
- `ProcessQueueItemsAsync`'s empty-result branch (`:679-686`) then simply `continue`s back to waiting for the next
  signal instead of delaying and self-releasing, so an idle queue is bounded to one query per wake interval exactly
  as the existing `leaseRecoveryWakeInterval`/`MaxLeaseRecoveryWakeInterval` calculation (`:659-661`) intends. The
  next subsection covers how that wait is implemented so it stays deterministically testable.

### A time-provider-aware wake wait

`SemaphoreSlim.WaitAsync(TimeSpan, CancellationToken)` (used today at `:671`) times out against the real system
clock, not the subscription's injected `_timeProvider`; a `FakeTimeProvider`-driven test cannot make it expire
deterministically. Replace that call with a small private helper, e.g.
`WaitForSignalOrIntervalAsync(TimeSpan interval, CancellationToken cancellationToken)`, that races
`_signalSemaphore.WaitAsync(...)` against `Task.Delay(interval, _timeProvider, ...)` (the `TimeProvider`-overload
of `Task.Delay`, available on the repository's `net10.0` target) using `Task.WhenAny`, and returns whether a
signal was actually received.

The two raced operations must not simply be abandoned once one of them wins: `Task.WhenAny` does not cancel its
loser, so a plain race between an untimed `_signalSemaphore.WaitAsync` and `Task.Delay` leaves the losing
semaphore wait registered in `SemaphoreSlim`'s internal (FIFO) waiter queue every time the delay wins — the
normal case throughout an idle period. A later `Release()` (a real change-stream wake or the existing backlog
re-signal at `:694-699`) then completes the oldest still-registered waiter from an earlier idle iteration instead
of the current loop's own, freshly registered wait, so a burst of inserts right after an idle stretch is delayed
by up to one extra wake interval per preceding idle timeout — reintroducing, through this helper, the exact
"idle looks right-sized, busy looks responsive" distortion issue #238 is about. The helper must therefore give
the race its own `CancellationTokenSource` linked to the caller's `cancellationToken`, cancel it as soon as
either side completes, and await the loser before returning so it actually unregisters from the semaphore
instead of lingering: canceling `_signalSemaphore.WaitAsync` before it has acquired a permit removes it from the
waiter queue without consuming one. Cancellation and completion can race at the same instant, so if the
semaphore wait still completes successfully despite "losing" the outer race, treat that as a real signal
(return `true`) rather than discarding it; only swallow the loser's `OperationCanceledException` when it stems
from the helper's own synthetic cancellation, and propagate a genuine external cancellation (the caller's
`cancellationToken` itself firing) instead of swallowing it. An equivalent safe provider-aware wait primitive
that gives the same unregister-the-loser and no-lost-signal guarantees is also acceptable.

`ProcessQueueItemsAsync` (`:667-713`) calls this helper instead of `_signalSemaphore.WaitAsync` directly;
semaphore release/signal semantics (change-stream wakes, backlog re-signal) are unchanged — only the timeout side
becomes driven by `_timeProvider`, and no iteration leaves an orphaned waiter behind, so a test using
`FakeTimeProvider.Advance(...)` can deterministically expire the wait without a live clock, a stalled test, or a
signal silently misdirected to a stale waiter.

### Distinguishing retry from recovery

`AttemptCount` (added for #239) increments on every granted acquisition, including a lease-expiry reclaim after a
crash, so it cannot by itself tell a deliberate, already-logged retry apart from an abandoned lock — both leave the
same `IsLocked == true` / stale `LockedUtc` state. Add a new `DateTime?` property to `MongoQueueItem`, e.g.
`DeferredUtc` (`[BsonIgnoreIfNull]`, `internal set`, public get, following `LockedUtc`/`ClosedUtc`), set only by
`HandleFailedQueueItemAsync`'s retry branch (`:368-377`) via an ownership-checked
`UpdateOneAsync(CreateLockOwnershipFilter(...), Set(x => x.DeferredUtc, now))`, without touching `IsLocked` or
`LockedUtc` — the item still only becomes available once its lease naturally expires, preserving today's retry
timing.

`CreateAcquireUpdate` (`:107-183`) must clear `DeferredUtc` (`$$REMOVE`) whenever an attempt is granted, so a stale
value from an earlier cycle never survives into the next one; the projected `MongoQueueItem` read with
`ReturnDocument.Before` still reflects the pre-clear value for the acquisition that just happened. In
`ProcessQueueItemAsync` (`:523-533`), record `lock.recovered` and log the recovery warning only when
`projectedResult.IsLocked && projectedResult.DeferredUtc is null`; when `DeferredUtc` is set, log at `Debug` that
a deferred retry is being reacquired instead. This is additive to the public `MongoQueueItem` surface and does not
change the document shape for existing records (a missing `DeferredUtc` behaves as `null`, i.e. today's
recovery-on-reacquire behavior, which is correct for documents written before this change).

If `HandleFailedQueueItemAsync`'s ownership-checked write loses the race (lock ownership already changed), skip it
silently like the method's other best-effort writes; a missed `DeferredUtc` only means that one reacquisition is
misclassified as recovery, not a correctness issue.

### Change-stream failure handling

Confirmed empirically against a standalone MongoDB 7 server with `MongoDB.Driver` 3.9.0 (the repository's pinned
minimum): opening a change stream against a non-replica-set server throws `MongoDB.Driver.MongoCommandException`
with `Code == 40573` ("The $changeStream stage is only supported on replica sets"). The `TryOpenChangeStreamAsync`
helper introduced above special-cases `MongoCommandException ex when (ex.Code == 40573)`: log once at `Warning`
that the server does not support change streams (not a replica set or sharded cluster) and that this queue will
rely on polling only, then report "unsupported" so neither `StartAsync` nor the retry loop calls it again for this
subscription — retrying can never succeed for this cause on this server. This mirrors the existing
`ChangeStreamHistoryLostCode`-style code-matching catch clause in
`src/Chaos.Mongo.EventStore/Integrity/ChangeStreamAnchorCandidateSource.cs:395`; keep `40573` as a locally named
constant (e.g. alongside `MongoQueueSubscriptionDefaults`) rather than a bare literal.

For every other failure, replace the fixed 300 ms `Task.Delay` with capped exponential backoff: add
`ChangeStreamRetryInitialDelay` (300 ms, matching today's behavior on the first failure) and
`ChangeStreamRetryMaxDelay` (e.g. 30 s) to `MongoQueueSubscriptionDefaults`, doubling the delay after each
consecutive failure. Reset the delay to the initial value only after a healthy iteration of the open cursor — that
is, after `MoveNextAsync` itself returns successfully (including an empty batch) — not merely after
`TryOpenChangeStreamAsync`/`WatchAsync` returns a cursor. `WatchAsync` only opens the stream; a cursor that opens
successfully but then fails on its first `MoveNextAsync` call must still count as a consecutive failure for backoff
purposes, otherwise a server that always fails the read immediately after a successful open would retry at the
fixed initial delay forever instead of backing off.

### Documentation

`docs/requirements.md`'s MongoDB server section currently lists only the event store, the transactional outbox,
and explicit transactions as needing a replica set or sharded cluster, implying queues have no such constraint.
Add a line stating that queue change-stream notifications need a replica set or sharded cluster, and that a
standalone server still works through polling alone (bounded by `LockLeaseTime`/`MaxLeaseRecoveryWakeInterval`),
without real-time notification of newly published items. `docs/queues.md`'s "Recovery and retention" or
"Diagnostics" section should note the same polling-only behavior on a standalone server, and that
`lock.recovered` now reflects only locks left behind by an abandoned attempt, not ordinary resolved retries.

### Testing

Extend `tests/Chaos.Mongo.Tests/Queues/MongoQueueSubscriptionTests.cs`, which already has hand-crafted
`IMongoCollection<MongoQueueItem<TestPayload>>` test doubles for this exact area (`PollingQueueItemCollection`,
`IdleChangeStreamCursor`, `FakeTimeProvider` usage) and reflection-based private-method invocation for
`CreateAcquireUpdate`/`HandleFailedQueueItemAsync`/`ProcessQueueItemAsync`. Add:

- A collection double that reports zero available items and counts `FindAsync` calls over a simulated idle period,
  driving the new `WaitForSignalOrIntervalAsync` wait (directly, or indirectly through `ProcessQueueItemsAsync`)
  by advancing a `FakeTimeProvider` across several wake intervals; assert the query count matches the number of
  elapsed intervals, not a busy-poll count, and assert exactly one query happens at startup (not two, now that
  `_signalSemaphore` starts at count 0). A second scenario covers an item appearing after `StartAsync` but before
  the change-stream cursor would otherwise report it, to cover both idle-polling acceptance criteria. A third
  scenario advances the `FakeTimeProvider` through several idle wake-interval timeouts with no signal, then
  releases `_signalSemaphore` exactly once (simulating a real insert) and asserts `WaitForSignalOrIntervalAsync`
  returns a signal immediately, without advancing time again — covering the orphaned-waiter/burst-after-idle
  acceptance criterion and exercising both cancellation-of-the-loser paths (delay wins, then signal wins).
- `StartAsync`-level tests for the initial `TryOpenChangeStreamAsync` call: one where it throws
  `MongoCommandException` with `Code == 40573`, asserting the single warning, that no monitor task is started, and
  that `ProcessQueueItemsAsync` still runs; one where it throws an unrelated transient exception, asserting
  processing still starts and the monitor task begins its own retry loop at the initial backoff delay; and the
  new test covering the startup race between the initial signal and the change stream becoming active, asserting
  the already-open cursor is handed to the monitor task instead of being reopened.
- A `HandleFailedQueueItemAsync`-then-`ProcessQueueItemAsync` sequence (reusing the existing reflection-invocation
  pattern) asserting `DeferredUtc` is set by the retry branch, cleared by the next granted acquisition, and that
  `lock.recovered` is not recorded for that reacquisition, alongside the existing
  `ProcessQueueItemAsync_WithRecoveredLock_EmitsRecoveryAndSuccessDiagnostics` test (`:627`) which must keep
  passing unchanged for a lock that was never resolved by `HandleFailedQueueItemAsync`.
- A `WatchAsync`-throwing collection double returning the `MongoCommandException` with `Code == 40573` to verify
  the single clear message and that the monitor loop stops retrying, plus a double that fails with an unrelated
  exception a few times to verify increasing backoff delays (via `FakeTimeProvider`/injected delay, not real
  `Task.Delay` waits). Confirmed directly: `MongoCommandException`'s public four-argument constructor
  (`ConnectionId`, message, command `BsonDocument`, result `BsonDocument` with a `code` element) builds an
  instance whose `Code`/`CodeName` reflect that `code` value, so the double can throw a real `40573` exception
  without a live server; `ConnectionId`/`ServerId`/`ClusterId` (`MongoDB.Driver.Core.Connections`/`Core.Servers`/
  `Core.Clusters`) are public and constructible from the test project.
- A double that succeeds `WatchAsync` (cursor opens) but fails every `MoveNextAsync` call, repeated across several
  retry iterations, asserting the backoff delay keeps increasing rather than resetting to the initial value on
  each successful open; and a double that fails `MoveNextAsync` a few times and then returns a batch successfully,
  asserting the delay resets to the initial value only after that healthy read.

No integration test changes are required for these behaviors; the existing `MongoQueueRetryIntegrationTests.cs`
and `MongoQueueLockExpiryIntegrationTests.cs` exercise real retry and lease-expiry recovery against Testcontainers
MongoDB and should keep passing unchanged, since the lease and retry timing itself is not changing.
