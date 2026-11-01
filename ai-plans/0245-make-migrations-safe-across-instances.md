# 0245 — Make Migrations Safe Across Instances

> Issue: [#245](https://github.com/chA0s-Chris/Chaos.Mongo/issues/245)

## Rationale

`MongoMigrationRunner` and `MongoHostedService` coordinate migrations across application instances with a distributed lock, but three gaps make that coordination unsafe. First, when the migration lock is already held, `RunMigrationsAsync` logs a warning and returns a plain `Task`, so an instance starts on the old schema and callers have no way to know migrations were skipped. Second, the lock's lease is acquired once for the whole run and never renewed, so a migration that runs longer than the lease loses its lock — or overlaps with another runner — even though `IMongoLock.TryExtendAsync` (added in #110) already exists to solve exactly this. Third, `Migrations` is never checked for duplicate `IMongoMigration.Id` values, so two migrations sharing an ID can silently mean one of them never runs. `MongoHostedService.StartingAsync` also runs configurators before migrations today, which is undocumented and can let a configurator create an index before the migration that cleans up the data it depends on.

This plan makes `RunMigrationsAsync` wait for the lock by default (bounded by a timeout) and return a result describing what happened, renews the lease while a migration executes, rejects duplicate migration IDs at construction, and runs migrations before configurators so schema/data changes land before index creation depends on them.

## Acceptance Criteria

- [x] `IMongoMigrationRunner.RunMigrationsAsync` returns a result object that reports whether the run was skipped and which migration IDs were applied, instead of a plain `Task`.
- [x] By default, `RunMigrationsAsync` waits for the migration lock (instead of skipping immediately when it is held), bounded by a configurable timeout; a second instance waits for the first to finish before it continues starting, covered by a test.
- [x] When `MongoHostedService` runs migrations automatically on startup and the lock wait times out (a skipped result with a positive `MigrationLockWaitTimeout`), hosted startup fails instead of continuing to configurators against a potentially stale schema, covered by a test proving a blocked first runner prevents a second instance's hosted startup from completing.
- [x] The migration lock's lease is renewed while a migration is executing, so a migration that runs longer than the lease duration keeps its lock instead of losing it, covered by a test. This holds even when the lease is exceeded by the cumulative duration of several individually short migrations, not only by a single long-running one.
- [x] When the lease cannot be renewed (extension refused or fails) while protected work is in progress, that work is stopped instead of running to completion, the affected migration is not recorded as applied, and any active transaction is aborted, covered by a test.
- [x] The `MongoMigrationRunner` constructor throws a clear, descriptive exception when two or more registered migrations share the same `Id`, naming the duplicate ID(s).
- [x] `MongoHostedService` runs migrations before configurators during startup.
- [x] `docs/migrations.md` and `docs/configurators.md` state the startup execution order and the lock-wait/renewal behavior; `docs/migrating-from-0.x.md` lists the `RunMigrationsAsync` signature change and the new default wait-for-lock behavior.
- [x] The Release build produces no warnings and the full test suite (`dotnet test Chaos.Mongo.slnx`) passes.

## Technical Details

### Result type and breaking signature change

Add a public `MongoMigrationResult` type in `Chaos.Mongo.Migrations`, following the existing `MongoMigrationHistoryItem` shape (sealed class, `required` init-only properties, no custom equality):

```csharp
public sealed class MongoMigrationResult
{
    public required Boolean Skipped { get; init; }
    public required IReadOnlyList<String> AppliedMigrationIds { get; init; }
}
```

`Skipped` is `true` only when the lock could not be acquired within the wait timeout; `AppliedMigrationIds` is empty in that case. When the lock is acquired but there is nothing pending, `Skipped` is `false` and `AppliedMigrationIds` is empty. `IMongoMigrationRunner.RunMigrationsAsync` changes from `Task` to `Task<MongoMigrationResult>` — a breaking change, already covered by the issue's `breaking` label.

### Waiting for the lock with a timeout

Add `MongoOptions.MigrationLockWaitTimeout` (`TimeSpan?`, default `MongoDefaults.MigrationLockWaitTimeout` = 30 minutes — three times the default `MigrationLockLeaseTime`, enough margin for a slow first run without blocking startup indefinitely). Semantics: `null` waits indefinitely (until `cancellationToken` is cancelled); `TimeSpan.Zero` makes a single attempt and returns a skipped result immediately, restoring the pre-1.0 behavior; a positive value bounds the wait.

Implement by wrapping the existing `MongoHelperExtensions.AcquireLockAsync` (which already retries indefinitely with `MongoDefaults.RetryDelay` between attempts) in a `CancellationTokenSource` linked to `cancellationToken` with `CancelAfter(waitTimeout)`. Distinguish an internal timeout from external cancellation by checking `cancellationToken.IsCancellationRequested` in the `catch (OperationCanceledException)` handler: if the caller's token was not the one that fired, return a skipped `MongoMigrationResult`; otherwise let the exception propagate. Skip the wrapping entirely (single `TryAcquireLockAsync` call) when `MigrationLockWaitTimeout` is `TimeSpan.Zero`.

### Hosted startup fails on a timed-out lock wait

`RunMigrationsAsync` keeps returning a skipped `MongoMigrationResult` rather than throwing when the wait times out — this preserves a simple, non-throwing contract for callers that invoke it manually and want to decide for themselves how to react. `MongoHostedService.StartingAsync`, however, cannot treat "skipped" as a benign outcome: letting hosted startup continue would let this instance's configurators (and the rest of the host) run against a schema another instance may still be migrating, which is the exact cross-instance hazard this plan exists to close. When `ApplyMigrationsOnStartup` is enabled and `RunMigrationsAsync` returns `Skipped = true`, `StartingAsync` throws (e.g. `InvalidOperationException`) instead of proceeding to configurators, failing hosted startup. `MigrationLockWaitTimeout = TimeSpan.Zero` is an explicit, intentionally-unsafe compatibility opt-out and is exempt from this check — a `TimeSpan.Zero` run that returns skipped is expected and does not fail startup.

Callers that invoke `RunMigrationsAsync` directly instead of relying on `MongoHostedService` do not get this protection automatically: a skipped result is returned to them like any other result, and they are responsible for inspecting `Skipped` and deciding whether to proceed, retry, or fail. Call this out explicitly in `IMongoMigrationRunner.RunMigrationsAsync`'s XML doc and in `docs/migrations.md`.

### Renewing the lease while a migration runs

`EventStreamSealingSweep.EnsureLockAsync` and `AnchoringRun.EnsureLockAsync` already extend a lock when less than half its lease remains, called between discrete units of work. That checkpoint idiom is not sufficient here for two reasons: a single `IMongoMigration.ApplyAsync` call is an opaque, potentially long-running operation with no checkpoints inside it, so renewal must run concurrently with it, not just before and after; and restarting a fresh checkpoint loop around each migration would never fire for a run made up of several migrations that are each individually shorter than half the lease but collectively exceed it.

Run a single renewal loop for the lifetime of the whole run — started right after the lock is acquired, cancelled and awaited immediately before the lock is disposed — instead of one loop per migration. The loop waits half of the lease's remaining lifetime (`(ValidUntilUtc - now) / 2`) rather than a fixed `MigrationLockLeaseTime / 2`, because a lease's expiry is calculated before its database round trip, so a slow acquisition or extension has already consumed part of the lease; a lease that has already expired counts as lost without another round trip. It waits using `Task.Delay(delay, _timeProvider, cancellationToken)` so it stays testable with `FakeTimeProvider` instead of real sleeps, consistent with the existing test class and with `tests/AGENTS.md`'s preference for deterministic, non-flaky tests.

A refused or failed extension must be observed by the in-progress protected work, not only by a post-hoc check after it returns: on `TryExtendAsync` returning `false`, or throwing anything other than the loop's own expected shutdown cancellation, the loop cancels a linked `CancellationTokenSource` whose token is passed into `ApplyAsync` and into the subsequent history-insert/transaction-commit calls for the migration in progress. `RunMigrationsAsync` awaits both that protected work and the renewal task before disposing the lock, aborts an active transaction when the lock is known lost, and does not record the migration as applied once loss is confirmed — preserving the original migration exception (if any) over a subsequent renewal-loop exception, and treating the loop's own shutdown-triggered `OperationCanceledException` as normal completion rather than loss. Shutdown cancels only the wait between renewals, not an extension already in flight: MongoDB may already have applied that extension, so cancelling it could leave `MongoLock` holding a stale expiry whose release matches nothing, keeping the lock held until the renewed lease expires. That extension stays bounded by the expiry of the lease it renews and, once protected work has finished, by the caller's token; a failure after that point is logged rather than failing a completed run. An extension that times out after MongoDB applied it can still leave the lock held until its lease expires. Cooperative cancellation cannot forcibly interrupt a migration that ignores its token; this narrows the unsafe window rather than eliminating every possible overlap, consistent with the lock's own documented stalled-holder limitations.

Illustrative contract only (not prescriptive of the exact control flow):

```csharp
// one loop spans the full run: start after lock acquisition, cancel+await before disposal
// extends every MigrationLockLeaseTime / 2 via mongoLock.TryExtendAsync()
// on refusal or an unexpected exception: cancels a linked token that flows into
// ApplyAsync and the following history-insert/transaction-commit calls
```

### Duplicate migration IDs

In the `MongoMigrationRunner` constructor, materialize `migrations` once (e.g., `migrations.ToImmutableArray()`), group by `Id` with `StringComparer.Ordinal` (the same comparer used for sorting), and throw `ArgumentException` (`paramName: "migrations"`) naming every duplicated ID when any group has more than one entry — before assigning the sorted `Migrations` property.

### Startup order

Swap the order in `MongoHostedService.StartingAsync` so migrations run before configurators. No documented contract today depends on the current configurators-first order — `docs/migrations.md` and `docs/configurators.md` are both silent on it — so swapping removes the hazard described in the issue (an index created before the migration that cleans the data it needs) rather than only documenting it. Add a test asserting the new order (none of the existing `MongoHostedServiceTests` currently assert on relative order).

### Documentation

Update `docs/migrations.md`'s "Behavior" section and `docs/configurators.md`'s "Startup execution" section to state that migrations run before configurators, and to describe the default lock-wait-with-timeout and lease-renewal behavior, replacing the current "skips if lock is held by another process" framing in `IMongoMigrationRunner.RunMigrationsAsync`'s XML doc `<remarks>` (that framing does not currently appear in `docs/migrations.md` itself). State explicitly that `MongoHostedService` fails startup if the wait times out (unless `MigrationLockWaitTimeout` is `TimeSpan.Zero`), and that manual callers of `RunMigrationsAsync` receive a skipped result instead and must check `MongoMigrationResult.Skipped` themselves to decide how to react — they do not get the hosted service's automatic fail-fast behavior. Add an entry to `docs/migrating-from-0.x.md` covering: the `RunMigrationsAsync` return type change, the new default wait-for-lock behavior (and how to restore the old behavior via `MigrationLockWaitTimeout = TimeSpan.Zero`), the hosted-startup-fails-on-timeout behavior, and the duplicate-ID constructor check.

### Testing notes

The "second instance waits" criterion is best proven as a MongoDB integration test: two runners share a lock, the first is given a migration that blocks on a `TaskCompletionSource` until released, and the second instance's `RunMigrationsAsync` call is asserted to return only after the first completes (and not as skipped). The lease-renewal criterion needs a lease shorter than the simulated migration duration, driven by a shared `FakeTimeProvider` or `Task.Delay` controllable via `TimeProvider`, following the same approach used for the extension/disposal concurrency test in [0110](0110-extend-lock-lease.md); include a variant with several migrations that are each individually shorter than half the lease but collectively exceed it, to prove the single run-scoped renewal loop (not a per-migration one) is what keeps the lock alive. Add coverage for a refused extension and for an extension that throws while a migration is in progress, asserting the migration is not recorded as applied and, when a transaction was used, that it was aborted. New unit coverage for the wait timeout, renewal loop, and duplicate-ID validation uses hand-crafted test doubles rather than Moq, per `tests/AGENTS.md`'s rule against new mocking-framework usage; the existing Moq-based setups in `MongoMigrationRunnerTests` may remain for the tests that already use them.
