# Use the same option names for queues and the outbox

## Rationale

[Issue #213](https://github.com/chA0s-Chris/Chaos.Mongo/issues/213) notes that
`MongoQueueBuilder<TPayload>` (`Chaos.Mongo`) and `OutboxBuilder`
(`Chaos.Mongo.Outbox`) configure the same three concepts under different
names: the distributed-lock lease duration, the number of items fetched per
poll, and the retention of finished items. This asymmetry makes the two
features harder to learn together even though they share no other coupling.
The issue proposes a single vocabulary per concept, generally preferring
whichever name already matches an established convention elsewhere in the
library (`WithLockLeaseTime` matches `MongoDefaults.LockLeaseTime` and the
`leaseTime` parameter of `IMongoHelper.TryAcquireLockAsync`; `WithBatchSize`
is the existing outbox name with no other precedent to defer to). This is a
pure rename: no stored document shape, runtime behavior, or validation
outcome changes.

## Acceptance Criteria

- [x] The outbox builder exposes `WithLockLeaseTime(TimeSpan leaseTime)`
      instead of `WithLockTimeout(TimeSpan lockTimeout)`; the queue builder's
      `WithLockLeaseTime` is unchanged.
- [x] The queue builder exposes `WithBatchSize(Int32 batchSize)` instead of
      `WithQueryLimit(Int32 queryLimit)`; the outbox builder's `WithBatchSize`
      is unchanged.
- [x] The queue builder exposes `WithRetentionPeriod(TimeSpan retentionPeriod)`
      instead of `WithClosedItemRetention(TimeSpan retention)`; the queue
      keeps `WithImmediateDelete()` with its current behavior; the outbox
      builder's `WithRetentionPeriod` is unchanged.
- [x] `MongoQueueDefinition.LockLeaseTime` is unchanged; `MongoQueueDefinition`
      exposes `BatchSize` instead of `QueryLimit` and `RetentionPeriod`
      instead of `ClosedItemRetention`, with the same types, nullability, and
      required/optional modifiers as today.
- [x] `MongoDefaults` exposes `QueueBatchSize` instead of `QueryLimit`, and
      `QueueRetentionPeriod` instead of `QueueClosedItemRetention`, with the
      same values; `MongoDefaults.LockLeaseTime` and
      `MongoDefaults.QueueLockLeaseTime` are unchanged.
- [x] No renamed member's thrown condition or default value changes. The
      only exception-type change is the deliberate amendment below: the queue
      builder's range checks now throw `ArgumentOutOfRangeException`, matching
      the outbox builder.
- [x] The defaults and switch pairs of both builders are audited per the
      issue's proposal item 2; the audit's findings and disposition (keep
      as-is, with rationale) are recorded in Technical Details, and no new
      public member is added as a result.
- [x] `src/Chaos.Mongo/PublicAPI.Unshipped.txt` and
      `src/Chaos.Mongo.Outbox/PublicAPI.Unshipped.txt` reflect every renamed
      member of a currently public, shipped type, and
      `Microsoft.CodeAnalysis.PublicApiAnalyzers` (RS0016/RS0017) reports no
      errors.
- [x] `docs/queues.md`, `docs/transactional-outbox.md`, and
      `src/Chaos.Mongo.Outbox/README.md` use only the renamed members; no
      sample in either doc still shows `WithLockTimeout`, `WithQueryLimit`, or
      `WithClosedItemRetention`.
- [x] Every production, test, and benchmark reference to a renamed member
      compiles against the new name; `dotnet build -c Release
      Chaos.Mongo.slnx` produces no warnings and the complete test suite
      (`bash ./build.sh Test`) passes with the merged line coverage required
      by `tests/AGENTS.md`.

## Technical Details

**Renames, by concept (old → new):**

| Member | Old | New |
|---|---|---|
| `OutboxBuilder` method | `WithLockTimeout(TimeSpan lockTimeout)` | `WithLockLeaseTime(TimeSpan leaseTime)` |
| `OutboxOptions` property (internal) | `LockTimeout` | `LockLeaseTime` |
| `OutboxOptions` constant (internal) | `DefaultLockTimeout` | `DefaultLockLeaseTime` |
| `MongoQueueBuilder<TPayload>` method | `WithQueryLimit(Int32 queryLimit)` | `WithBatchSize(Int32 batchSize)` |
| `MongoQueueDefinition` property | `QueryLimit` | `BatchSize` |
| `MongoDefaults` member | `QueryLimit` | `QueueBatchSize` |
| `MongoQueueBuilder<TPayload>` method | `WithClosedItemRetention(TimeSpan retention)` | `WithRetentionPeriod(TimeSpan retentionPeriod)` |
| `MongoQueueDefinition` property | `ClosedItemRetention` | `RetentionPeriod` |
| `MongoDefaults` member | `QueueClosedItemRetention` | `QueueRetentionPeriod` |

`MongoQueueBuilder<TPayload>.WithLockLeaseTime`, `MongoQueueBuilder<TPayload>.WithImmediateDelete`,
`OutboxBuilder.WithBatchSize`, and `OutboxBuilder.WithRetentionPeriod` already
use the target names and are unchanged. `MongoDefaults.LockLeaseTime` and
`MongoDefaults.QueueLockLeaseTime` are two existing, deliberately separate
members (generic distributed-lock default vs. queue-specific default) and are
unrelated to this rename.

Each builder method keeps the condition its validation fires on. On the queue
builder, `WithQueryLimit`, `WithClosedItemRetention`, `WithLockLeaseTime`
(already correctly named), and `WithMaxRetries` threw `ArgumentException`
before this change. On the outbox builder, `WithLockTimeout` and the rest of
`OutboxBuilder` throw `ArgumentOutOfRangeException`. Carry over each existing
`if` exactly, renaming the parameter and updating the exception message text
to name the new parameter (e.g., "Retention period must be greater than 0.").

**Amendment: queue builder exception types.** This plan originally preserved
the `ArgumentException` throws, including `WithQueryLimit`'s mismatch with its
XML doc, which documented `ArgumentOutOfRangeException`. Once the names match,
the same mistake on `WithBatchSize` would throw different types on the two
builders, so `WithBatchSize`, `WithLockLeaseTime`, `WithMaxRetries`, and
`WithRetentionPeriod` on the queue builder now throw
`ArgumentOutOfRangeException`, and their XML docs say so. Existing
`catch (ArgumentException)` handlers still work because
`ArgumentOutOfRangeException` derives from it. The conditions are unchanged:
the queue's `WithMaxRetries` still rejects `0` because `WithNoRetry()` exists,
while the outbox's accepts it.

**`MongoQueueBuilder<TPayload>.RegisterQueue()`** currently reads
`MongoDefaults.QueryLimit` and assigns `QueryLimit` on the constructed
`MongoQueueDefinition`, and reads `MongoDefaults.QueueClosedItemRetention` as
the backing field's initializer and assigns `ClosedItemRetention`; update
both to the renamed members (private backing fields may be renamed too, e.g.
`_queryLimit` → `_batchSize`, `_closedItemRetention` → `_retentionPeriod`,
but that is an implementation detail, not part of the public contract).

**`MongoQueueSubscription`** (`src/Chaos.Mongo/Queues/MongoQueueSubscription.cs`)
reads `_queueDefinition.ClosedItemRetention` (lines ~118, 125, 142, 145, 347,
366, including in a log message template) and `_queueDefinition.QueryLimit`
(line ~420); update every read to the renamed `MongoQueueDefinition`
properties.

**`MongoDefaults.QueryLimit` public-API-tracking nuance.** `QueryLimit` is a
`static Int32` property added by #210 (replacing a previously shipped `const
Int32 QueryLimit`); that const's removal is already recorded in
`src/Chaos.Mongo/PublicAPI.Unshipped.txt` as `*REMOVED*const
Chaos.Mongo.MongoDefaults.QueryLimit = 1 -> int` and must stay untouched. The
property's own addition line, `static Chaos.Mongo.MongoDefaults.QueryLimit.get
-> int`, has never shipped, so renaming it to `QueueBatchSize` only requires
editing that one pending-addition line in place (no `*REMOVED*` entry, since
nothing shipped under the old property name). In contrast,
`MongoDefaults.QueueClosedItemRetention` and every other renamed member listed
in the table above (`OutboxBuilder.WithLockTimeout`,
`MongoQueueBuilder<TPayload>.WithQueryLimit`,
`MongoQueueBuilder<TPayload>.WithClosedItemRetention`,
`MongoQueueDefinition.QueryLimit`, `MongoQueueDefinition.ClosedItemRetention`)
are present in `PublicAPI.Shipped.txt` today, so each needs a `*REMOVED*`
entry plus a new entry for the renamed member in the corresponding project's
`PublicAPI.Unshipped.txt`; generate both from the analyzer's own RS0016/RS0017
diagnostics rather than typing them by hand, following the convention
`ai-plans/0212-trim-the-outbox-public-api.md` established. `OutboxOptions`
(including `LockTimeout`/`DefaultLockTimeout`) is already `internal` per
#212, and its pending-removal entries in
`src/Chaos.Mongo.Outbox/PublicAPI.Unshipped.txt` already reflect that
type-level change; renaming members inside it needs no further
`PublicAPI.*.txt` edits because internal members are not analyzer-tracked.

**Documentation.** Update the configuration examples and prose in
`docs/queues.md` (code block and "Processing options" bullets, currently
`WithQueryLimit(10)` and `WithClosedItemRetention(TimeSpan.FromHours(6))`) and
`docs/transactional-outbox.md` (code blocks, the options table, and prose,
currently `WithLockTimeout(TimeSpan.FromMinutes(5))` and the `LockTimeout`
prose mention) and `src/Chaos.Mongo.Outbox/README.md` if it gains a matching
example. No other doc file references these names.

**Defaults and switch-pair audit (issue proposal item 2).** Comparing the two
builders' startup switches and numeric defaults:

- *Startup switch pair.* `MongoQueueBuilder<TPayload>` has both
  `WithAutoStartSubscription()` and `WithoutAutoStartSubscription()`;
  `OutboxBuilder` has only `WithAutoStartProcessor()`, with no method to turn
  processor auto-start back off. Both default to not starting automatically
  (`MongoDefaults.AutoStartSubscription` and `OutboxOptions.AutoStartProcessor`
  both default to `false`).
- *Batch size.* Queue default is `1` (`MongoDefaults.QueryLimit`, renamed to
  `QueueBatchSize`); outbox default is `100`
  (`OutboxOptions.DefaultBatchSize`). Different, pre-existing, unrelated to
  this rename.
- *Retention.* Queue default retains closed items for `1 hour`
  (`MongoDefaults.QueueClosedItemRetention`, renamed to
  `QueueRetentionPeriod`); outbox default retains messages indefinitely
  (`OutboxOptions.RetentionPeriod` defaults to `null`). Different, pre-existing,
  unrelated to this rename.
- *Lock lease.* Both default to `5 minutes`
  (`MongoDefaults.QueueLockLeaseTime` and `OutboxOptions.DefaultLockTimeout`,
  the latter renamed to `DefaultLockLeaseTime`) — already symmetric.

Disposition: this issue's scope, per its proposal table and acceptance
criteria, is unifying the three *names* in the table above, not equalizing
numeric defaults or adding a `WithoutAutoStartProcessor()` counterpart to
`OutboxBuilder`. The batch-size and retention default differences reflect
the two features' different delivery semantics (queue items are drained
quickly; outbox messages may need to stay for audit) and are preserved
as-is. No new public member is added to close the startup-switch asymmetry,
since the issue's proposal and acceptance criteria do not request one and
adding it would be a new public API commitment outside the renames this
issue asks for; the asymmetry is documented here rather than silently
dropped.

**Out of scope.** `IOutboxConfiguratorRunner.RunAsync` and
`IMongoConfiguratorRunner.RunConfiguratorsAsync` remain publicly named
differently from each other. The issue's background section reasons this "no
longer matters" because `IOutboxConfiguratorRunner` becomes internal in #212;
in the merged #212 implementation only the `OutboxConfiguratorRunner` class
became internal, while `IOutboxConfiguratorRunner` itself, with its `RunAsync`
method, is still public. The issue's proposal and acceptance-criteria text
limit the rename to the three concepts in the table (lock lease, items per
poll, retention), not the runner method name, so this plan does not touch
`RunAsync`/`RunConfiguratorsAsync`.

**Testing.** This is a mechanical rename with no behavior change, so no new
test scenarios are required beyond updating existing call sites
(`tests/Chaos.Mongo.Tests/Queues/MongoQueueBuilderTests.cs`,
`tests/Chaos.Mongo.Tests/Integration/Queues/*.cs`,
`tests/Chaos.Mongo.Outbox.Tests/OutboxBuilderTests.cs`, and any other test
file referencing a renamed member) to the new names; the existing assertions
already cover the validation and default-value behavior this plan preserves.
