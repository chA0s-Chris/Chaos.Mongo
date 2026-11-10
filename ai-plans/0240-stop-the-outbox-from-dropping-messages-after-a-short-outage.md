# Stop the Outbox from Dropping Messages After a Short Broker Outage

## Rationale

[Issue #240](https://github.com/chA0s-Chris/Chaos.Mongo/issues/240) reports that with default options the outbox
permanently gives up on a message after about 75 to 95 seconds of broker outage: `OutboxOptions.MaxRetries`
defaults to `5` (`OutboxOptions.cs:16`), so `OutboxProcessor`'s claim/failure logic (`OutboxProcessor.cs:104-189`,
`203-255`) terminalizes the message to `Failed` after 6 total delivery attempts, with no API to requeue it. If
`WithRetentionPeriod(...)` is also configured, `OutboxConfigurator` creates a TTL index on `FailedUtc`
(`OutboxConfigurator.cs:70-81`) that then deletes the undelivered message, leaving only a log line — the outbox has
no metrics. An outbox exists to provide at-least-once delivery, so silently losing a message to a short outage
contradicts its purpose.

[Plan 0239](0239-count-queue-delivery-attempts-maxretries.md) deliberately kept `OutboxOptions.MaxRetries`
non-nullable to stay in scope, but noted that the queue's `MaxRetries` already supports `null` for unlimited
retries (`MongoQueueDefinition.cs:31-37`, `MongoDefaults.QueueMaxRetries` at `MongoDefaults.cs:63`), and that the
queue's single TTL index already excludes terminal (dead-lettered) items by partial filter
(`MongoQueueSubscription.CreateClosedItemTtlPartialFilter`, `MongoQueueSubscription.cs:195-218`), with no opt-in to
expire them. This plan closes that gap for the outbox: default to unlimited retries, bounded by the existing
backoff cap, and exclude `Failed` messages from TTL cleanup by default, with an explicit opt-in for anyone who
relied on the old eager-expiry behavior.

## Acceptance Criteria

- [x] With default options (no `WithMaxRetries(...)` call), an outbox message whose publisher keeps throwing
      survives well past today's ~75-95 second/6-attempt budget — remaining `Pending` and continuing to be
      retried with the existing exponential backoff capped at `RetryBackoffMaxDelay` — and reaches `Processed`
      once the publisher starts succeeding; it never becomes `Failed` purely from exhausting an attempt budget.
      Covered by a new test using `FakeTimeProvider` to advance well past the old exhaustion point.
- [x] With `WithMaxRetries(N)` explicitly configured to a finite value, a message whose publisher always throws
      still reaches `State == Failed` after exactly `N + 1` attempts, preserving the existing finite-budget
      behavior from [plan 0239](0239-count-queue-delivery-attempts-maxretries.md).
- [x] With `WithRetentionPeriod(...)` configured and default options, a message that reaches `State == Failed` is
      not removed and is not matched by any TTL index: the `IX_Outbox_FailedUtc_TTL` index does not exist on the
      collection, while the `ProcessedUtc` TTL index is created and still expires `Processed` messages normally.
      Covered by a test asserting the actual index contract (index absence/presence), not only the processor's
      in-memory behavior.
- [x] With `WithRetentionPeriod(...)` and the new opt-in configured, `Failed` messages are still expired via a
      `FailedUtc` TTL index with the same contract as today, preserving that behavior for anyone who opts in.
- [x] `docs/transactional-outbox.md` describes the new defaults (unlimited retries bounded by the backoff cap;
      `Failed` messages excluded from TTL cleanup) and how to restore the previous finite-retry and eager-expiry
      behavior.
- [x] `docs/migrating-from-0.x.md` gets a new entry describing both default changes, their consequence for an
      unconfigured outbox already in production, and how to restore each previous behavior explicitly.
- [x] The Release build (`dotnet build -c Release Chaos.Mongo.slnx`) produces no warnings, and the complete test
      suite passes while maintaining the repository's required merged line coverage.

## Technical Details

### Unlimited retries by default

Change `OutboxOptions.MaxRetries` (`OutboxOptions.cs:51`) from `Int32` to `Int32?`, and
`OutboxOptions.DefaultMaxRetries` (`OutboxOptions.cs:16`) from `const Int32 = 5` to a `null` default, mirroring
`MongoQueueDefinition.MaxRetries`/`MongoDefaults.QueueMaxRetries` exactly: `null` means a claim is always granted
another attempt, never terminalizing the message for exhausting a budget. `OutboxOptions` is already `internal`
(trimmed by the 1.0 public-API cut), so this is not a public breaking change by itself.

Keep `OutboxBuilder.WithMaxRetries(Int32 maxRetries)` (`OutboxBuilder.cs:121-128`) exactly as shipped today — it
still sets a finite budget and keeps its existing non-negative validation. Only the *unconfigured* default changes,
the same relationship `MongoQueueBuilder<TPayload>.WithMaxRetries(Int32)` already has to
`MongoDefaults.QueueMaxRetries`.

Update `OutboxProcessor.CreateClaimUpdate` (`OutboxProcessor.cs:104-189`) to branch on `HasValue` the same way
`MongoQueueSubscription.CreateAcquireUpdate` does (`MongoQueueSubscription.cs:114-120`):

```csharp
BsonValue grant = _options.MaxRetries.HasValue
    ? new BsonDocument("$lte", new BsonArray { existingAttemptCount, _options.MaxRetries.Value })
    : BsonBoolean.True;
```

Update `OutboxProcessor.HandleFailureAsync`'s terminal/retry branch (`OutboxProcessor.cs:214`,
`attemptCount > _options.MaxRetries`) to `_options.MaxRetries.HasValue && attemptCount > _options.MaxRetries.Value`,
matching `MongoQueueSubscription.HandleFailedQueueItemAsync`'s equivalent nullable check. No other claim/failure
logic changes; `ComputeNextAttemptUtc`'s exponential backoff and its `RetryBackoffMaxDelay` cap
(`OutboxProcessor.cs:66-88`) are unaffected and remain the safety valve the issue asks for ("a backoff cap") once
the attempt budget itself is unbounded — the queue has no equivalent backoff mechanism to mirror here, so this
stays outbox-specific.

### Excluding `Failed` messages from TTL by default

Add a new opt-in on `OutboxOptions`/`OutboxBuilder`, illustrative shape:

```csharp
// OutboxOptions
public Boolean ExpireFailedMessages { get; init; } // defaults to false

// OutboxBuilder
public OutboxBuilder WithExpireFailedMessages() { ... } // no-arg toggle, same style as WithAutoStartProcessor()
```

In `OutboxConfigurator.CreateIndexesAsync` (`OutboxConfigurator.cs:54-87`), only create/update the
`IX_Outbox_FailedUtc_TTL` index when `_options.RetentionPeriod.HasValue && _options.ExpireFailedMessages`;
otherwise call `DropOneIfExistsAsync(FailedTtlIndexName, ...)`, the same way the queue never creates a TTL match
for `IsTerminal: true` items (`MongoQueueSubscription.cs:328-344`, `CreateClosedItemTtlPartialFilter`). The
`ProcessedUtc` TTL index's behavior is unchanged: it is still created whenever `RetentionPeriod` is configured,
regardless of `ExpireFailedMessages`. Unlike the queue, the outbox keeps its existing two-separate-timestamp-field
index design (`ProcessedUtc` and `FailedUtc`); only the failed index becomes conditional. The existing unit test
`OutboxConfiguratorTests.ConfigureAsync_WithRetentionPeriod_CreatesPollingAndTtlIndexes`
(`tests/Chaos.Mongo.Outbox.Tests/OutboxConfiguratorTests.cs`) asserts 3 `CreateOneAsync` calls for that scenario and
needs updating for the new default (2 creates, plus a drop of `IX_Outbox_FailedUtc_TTL`).

`WithExpireFailedMessages()` is new public API and needs a `PublicAPI.Unshipped.txt` entry in
`src/Chaos.Mongo.Outbox/`. Update `OutboxBuilder.WithRetentionPeriod`'s XML doc (`OutboxBuilder.cs:234-237`, "TTL
cleanup of processed and failed messages") since it no longer covers failed messages alone.

### Upgrade behavior for `IX_Outbox_FailedUtc_TTL`

Because `CreateOneOrUpdateAsync` reconciles indexes by key/options against the desired definition
(`docs/index-management.md:3-26`), an existing deployment with `WithRetentionPeriod(...)` configured keeps its
`IX_Outbox_FailedUtc_TTL` index until it upgrades; on the first startup after upgrading, the library now treats that
index as unwanted (unless `WithExpireFailedMessages()` is also configured) and drops it via
`DropOneIfExistsAsync`. No manual migration step is needed, but already-failed messages stop being deleted from
that point on, and any messages already deleted by the old index before upgrading are not recoverable.

### Documentation

Update `docs/transactional-outbox.md`'s "Retries and Backoff" section (around line 573) to state the default is now
unlimited retries (mirroring `docs/queues.md`'s `MaxRetries` wording) bounded by `RetryBackoffMaxDelay`, and the
"Retention and Cleanup" section (around line 624) to state `Failed` messages are excluded from TTL cleanup unless
`WithExpireFailedMessages()` is configured. Update the "Builder Options" table (`WithMaxRetries` and
`WithRetentionPeriod` rows) and add a row for `WithExpireFailedMessages()`.

Add a new `docs/migrating-from-0.x.md` section, in the style of the existing "Queue and outbox: `RetryCount`
renamed to `AttemptCount`..." section (lines 193-230): name both default changes (unconfigured `MaxRetries` is no
longer `5`; `WithRetentionPeriod(...)` alone no longer deletes `Failed` messages), state the concrete consequence
for an outbox already running in production (it no longer gives up on a message after a short outage; its
`IX_Outbox_FailedUtc_TTL` index is dropped on next startup unless `WithExpireFailedMessages()` is configured before
upgrading), and how to restore each previous behavior explicitly (`WithMaxRetries(5)`,
`WithExpireFailedMessages()`).

### Testing

Add outbox integration coverage for the unlimited-default end-to-end path (survive-and-recover under
`FakeTimeProvider`), mirroring the existing boundary-style tests in
`tests/Chaos.Mongo.Outbox.Tests/Integration/OutboxAttemptCountBoundaryIntegrationTests.cs`; the queue has no
equivalent end-to-end test for `MaxRetries == null` to reuse. Add an index-contract integration test analogous to
`MongoQueueRetentionIntegrationTests` (`tests/Chaos.Mongo.Tests/Integration/Queues/MongoQueueRetentionIntegrationTests.cs`)
that reads back the outbox collection's actual indexes via `ListIndexesAsync` to confirm `IX_Outbox_FailedUtc_TTL`'s
absence by default and presence when opted in.
