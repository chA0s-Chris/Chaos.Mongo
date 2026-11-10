# Name the outbox in the "already running" warning

## Rationale

[Issue #278](https://github.com/chA0s-Chris/Chaos.Mongo/issues/278): `OutboxProcessor.StartAsync`
(`src/Chaos.Mongo.Outbox/OutboxProcessor.cs:529`) logs `"Outbox processor is already running"` when
`StartAsync` is called on a processor that is already running — typically because `WithAutoStartProcessor()`
started it and the application also calls `StartAsync` manually. The warning is harmless (the second call
is a no-op), but it doesn't say which outbox it's about. The outbox identity and collection name are only
attached via `BeginScope` (`OutboxIdentity`, `CollectionName`); a logger that doesn't render scopes, such as
the default console logger, prints the identical line for every outbox, so with several outboxes registered
the reader can't tell which one was started twice. `StartAsync`'s and `StopAsync`'s own `LogInformation`
calls already put the collection name in the message text, establishing the precedent this warning should
follow.

The same gap exists in the other diagnostic warnings `OutboxProcessor` logs while processing a batch
(`HandleFailureAsync`, `MarkProcessedAsync`, `ProcessMessageAsync`, `TryReleaseLockAsync`): each already
executes inside the `ProcessLoopAsync` scope, but the rendered text never says which outbox/collection the
affected message belongs to, only its `MessageId`. `OutboxHostedService` has no `LogWarning` calls; its
`LogInformation`/`LogError` text already names the identity/collection where it varies per registration
(e.g. `StartingAsync`'s per-registration initialization message), so it needs no change.

## Acceptance Criteria

- [ ] Starting a processor that is already running logs a warning whose message text names the outbox
      identity and collection name, and states that the processor is likely being started both
      automatically (via `WithAutoStartProcessor()`) and manually (via `StartAsync`).
- [ ] An automated test covers this warning's message text for both the default outbox and a typed outbox
      registered with a distinct identity/collection.
- [ ] Every other `LogWarning` call in `OutboxProcessor` (permanent failure after exhausting retries in
      `HandleFailureAsync` and in `ProcessMessageAsync`'s terminal-claim branch, the scheduled-retry
      message in `HandleFailureAsync`, the lost-ownership messages in `HandleFailureAsync` and
      `MarkProcessedAsync`, the finalize-timeout message in `MarkProcessedAsync`, and the lock-release
      failure in `TryReleaseLockAsync`) also names the outbox identity and collection, so each reads
      unambiguously without relying on rendered log scopes.
- [ ] Existing tests that assert on a substring of these warning messages (`VerifyLoggedWarning` calls in
      `OutboxProcessorTests`) still pass with the updated text.
- [ ] The Release build has no warnings and the full test suite passes.

## Technical Details

- `OutboxProcessor.StartAsync` (`OutboxProcessor.cs:523-539`) already calls `BeginScope()`
  (`OutboxProcessor.cs:60-64`, which maps `_options.Identity` and `_options.CollectionName` to
  `OutboxIdentity`/`CollectionName`) around the "already running" check, so `_options` is in scope. Change
  the warning to a templated message using `{OutboxIdentity}` and `{CollectionName}` placeholders (matching
  the scope key names for consistency with structured sinks), plus a clause naming the likely cause, e.g.:

  ```csharp
  _logger.LogWarning(
      "Outbox processor '{OutboxIdentity}' for collection '{CollectionName}' is already running; " +
      "it is likely being started both automatically (WithAutoStartProcessor) and manually (StartAsync)",
      _options.Identity, _options.CollectionName);
  ```

- Apply the same `{OutboxIdentity}`/`{CollectionName}` text to the other `OutboxProcessor.cs` warnings,
  keeping each message's existing distinguishing substring intact so the corresponding `VerifyLoggedWarning`
  assertions in `OutboxProcessorTests` keep passing:
  - `HandleFailureAsync` permanent failure (`OutboxProcessor.cs:228-230`) — substring `"permanently failed"`.
  - `HandleFailureAsync` scheduled retry (`OutboxProcessor.cs:244-246`) — no existing substring assertion.
  - `HandleFailureAsync` failure-update-skipped (`OutboxProcessor.cs:253-255`) — substring `"failure update skipped"`.
  - `MarkProcessedAsync` ownership-lost (`OutboxProcessor.cs:291-294`) — substring `"ownership was lost"`.
  - `MarkProcessedAsync` finalize-timeout (`OutboxProcessor.cs:304-306`) — substring `"timed out"`.
  - `ProcessMessageAsync` terminal-claim (`OutboxProcessor.cs:473-475`) — substring `"permanently failed"`.
  - `TryReleaseLockAsync` (`OutboxProcessor.cs:518`) — substring `"Failed to release lock"`.

  `_options` is already accessible in all of these (instance field), so no new parameter threading is
  needed.
- Add the identity/collection text to the message itself (not only to log-scope state), since the point of
  this change is readability when scopes aren't rendered; keep the existing `BeginScope` calls as-is for
  structured sinks that do render scopes.
- `TypedOutboxLifecycleTests.cs` already has host-based infrastructure (`OutboxLogCapture`,
  `CapturingOutboxLogger`, `CreateHost`/`Configure`) that stands up a host with a default outbox plus two
  typed outboxes (`FirstDestination`, `SecondDestination`) with distinct collection names and identities.
  Add a new test there (or extend `OutboxProcessorTests.StartAsync_WhenAlreadyRunning_DoesNotStartAgain`,
  which currently only checks that no exception is thrown) that calls `StartAsync` twice on a processor —
  for both the default outbox and a typed outbox — and asserts the resulting warning's rendered message
  contains that outbox's identity and collection name.
- No public API changes; `OutboxOptions.Identity` stays `internal`. `OutboxHostedService.cs` requires no
  code change — confirmed it logs no warnings today, and its existing informational/error messages either
  are collection/identity-agnostic (hosted-service-level lifecycle) or already name the identity/collection
  per registration.
