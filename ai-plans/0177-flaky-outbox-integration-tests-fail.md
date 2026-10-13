# Flaky outbox integration tests fail intermittently in CI

> Issue: [#177](https://github.com/chA0s-Chris/Chaos.Mongo/issues/177)

## Rationale

Two integration tests in `Chaos.Mongo.Outbox.Tests` fail intermittently on GitHub Actions and turn pull requests red that do not touch the outbox. Both failures are timing-dependent, but neither is merely a slow runner: the shutdown test's cancellation signal races with its own cleanup, and the ordering test exposes a shutdown gap in `OutboxProcessor` that leaves an already published message `Pending`, which guarantees a redelivery after restart.

The goal is to make both tests deterministic without weakening what they verify, and to close the processor gap with a regression test.

## Acceptance Criteria

- [ ] `StopAsync_DelayedPublisher_CancelsEveryProcessorBeforeWaiting` passes deterministically in all three cases and still verifies that every automatic processor is signaled before the host waits on the held one.
- [ ] A message whose publish completed successfully is persisted as `Processed` even when `OutboxProcessor.StopAsync` is called while the completion update is in flight, and an automated regression test covers this.
- [ ] `Processor_ProcessesEligibleMessagesInPollingIndexOrder` passes deterministically and still verifies the publish order and the exact remaining pending set.
- [ ] Both tests pass repeatedly on CI and locally, for example across five consecutive runs of the outbox test project, without weakening what they verify.
- [ ] The `IOutboxProcessor.StopAsync` remarks and the outbox package README state that a successfully published message is finalized even when shutdown cancels the processor, and that only an in-flight publish can be canceled.
- [ ] No unrelated tests change.

## Technical Details

### Cancellation signal race in `StopAsync_DelayedPublisher_CancelsEveryProcessorBeforeWaiting`

Every recorded failure times out at the wait for `secondCanceled` in `tests/Chaos.Mongo.Outbox.Tests/Integration/TypedOutboxIntegrationTests.cs` with `blockCallback == false`. The second handler registers its signal with `using var registration = token.Register(...)` and then awaits `Task.Delay(Timeout.InfiniteTimeSpan, token)`. Cancellation callbacks run in LIFO order, so the delay's own callback runs first and queues the handler's continuation to the thread pool; the signal callback is popped afterwards. If the cancel thread is preempted in between, the continuation unwinds the handler, the `using` disposes the registration, and `CancellationTokenRegistration.Dispose` unregisters the not yet executed signal callback. The signal is lost and the wait expires. A standalone probe that stalls the cancel thread between the two callbacks reproduces the loss deterministically.

The fix belongs in the test: observe the cancellation in a way the handler's own unwinding cannot revoke, for example by signaling from the `OperationCanceledException` the handler observes or by keeping the registration alive for the test's lifetime. Keep the existing waits and timeouts, the three `expireShutdown`/`blockCallback` cases, and the assertion that `host.StopAsync` is still pending while the first publisher is held. Raising timeouts is not an acceptable fix. The sibling `StartAsync_BlockedOrFailingDestination_OtherOutboxesProgressIndependently` uses the same registration pattern only for a negative assertion and is unaffected.

### Completion update canceled by shutdown

`OutboxProcessor.ProcessMessageAsync` passes the processor token to the success `UpdateOneAsync`. When `StopAsync` cancels the token between a successful `PublishAsync` and that update, the `OperationCanceledException` branch releases the lock and the message stays `Pending`, so it is published again after restart although delivery already happened. `HandleFailureAsync` persists its outcome without the token and `TryReleaseLockAsync` uses an independent three-second timeout; the success path is the only finalization that shutdown can cancel. The `IOutboxProcessor.StopAsync` remarks currently document this as expected behavior.

Decision: after `PublishAsync` returns successfully, finalize the message under an independent bounded timeout, reusing the three-second convention of `TryReleaseLockAsync`, instead of the processor token. Create that timeout from the processor's `TimeProvider` so a test can drive it with `FakeTimeProvider` instead of waiting three real seconds; the existing lock-release timeout stays as it is. The publish itself stays cancellable and a canceled publish keeps today's release-and-rethrow behavior. A finalization that times out releases the lock through the bounded `TryReleaseLockAsync` path and is not counted as a publish failure: the retry count and error stay untouched, and the message remains `Pending` for redelivery. The processor loop then continues or stops as the processor token dictates. A finalization that fails with any other exception keeps today's handling through `HandleFailureAsync`. An automated test covers the timed-out finalization. Update the `StopAsync` remarks, which currently state that the completion update may be canceled, and the README sentence on host shutdown in `src/Chaos.Mongo.Outbox/README.md` so both describe the new guarantee.

Regression test: an integration test with a hand-crafted `IOutboxPublisher` double that signals when `PublishAsync` is entered and waits for the test to release it. The test starts the processor, waits for the publish to be entered, starts `StopAsync`, releases the publisher, awaits `StopAsync`, and asserts the message is `Processed` and unlocked. `StopAsync` awaits the processing task, so the assertion can run right after it completes.

### Ordering test

`Processor_ProcessesEligibleMessagesInPollingIndexOrder` in `tests/Chaos.Mongo.Outbox.Tests/Integration/OutboxIndexContractIntegrationTests.cs` waits on the publisher's in-memory count and then stops the processor; the failing run left the third published message `Pending` through the gap above. Once the processor change lands, the test needs no additional waits because `StopAsync` returns only after the processing task has finalized the last published message.
