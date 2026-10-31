# Use an async DI scope in the outbox processor

## Rationale

[Issue #241](https://github.com/chA0s-Chris/Chaos.Mongo/issues/241) reports that
`OutboxProcessor.ProcessBatchAsync` (`src/Chaos.Mongo.Outbox/OutboxProcessor.cs:247`)
resolves the batch's `IOutboxPublisher` from a synchronous
`using var scope = _serviceScopeFactory.CreateScope();`. `IServiceScope.Dispose()`
throws `InvalidOperationException` when a service tracked by that scope only
implements `IAsyncDisposable` and not `IDisposable`, so a publisher implemented
that way breaks every batch. The outbox's own queue processing
(`src/Chaos.Mongo/Queues/MongoQueueSubscription.cs:326`) and
`src/Chaos.Mongo.EventStore/Integrity/AnchorCompletionNotifier.cs:102` already
resolve their per-operation scope with `await using (... CreateAsyncScope())`,
so this change brings the outbox processor in line with the established pattern
instead of introducing a new one.

## Acceptance Criteria

- [x] `OutboxProcessor` resolves the batch publisher from an async scope
      (`IServiceScopeFactory.CreateAsyncScope()`, disposed with `await using`)
      instead of the synchronous `CreateScope()`/`using` pair.
- [x] An automated test delivers at least one outbox message through a publisher
      that implements only `IAsyncDisposable` (not `IDisposable`) and verifies
      that publisher was disposed once its batch completes, without the
      processor throwing.
- [x] The Release build produces no new warnings and the full test suite passes.

## Technical Details

- The fix is confined to `ProcessBatchAsync` in
  `src/Chaos.Mongo.Outbox/OutboxProcessor.cs`: replace
  `using var scope = _serviceScopeFactory.CreateScope();` (line 247) with
  `await using var scope = _serviceScopeFactory.CreateAsyncScope();`.
  `ProcessBatchAsync` is already `async Task<Int32>`, `CreateAsyncScope()` is a
  `Microsoft.Extensions.DependencyInjection` extension already imported in this
  file, and `scope.ServiceProvider` on the resulting `AsyncServiceScope` has the
  same shape used today, so no other line in the method needs to change.
- `CreateAsyncScope()` itself calls the factory's existing `CreateScope()` and
  wraps the result; disposal prefers `IAsyncDisposable` on the underlying scope
  when available and only falls back to synchronous `Dispose()` otherwise. The
  real `IServiceScope` created by the built-in Microsoft DI container supports
  this async-aware disposal, which is what resolves the reported exception.
- The existing unit tests in `tests/Chaos.Mongo.Outbox.Tests/OutboxProcessorTests.cs`
  mock `IServiceScopeFactory.CreateScope()` directly and back the mocked scope's
  `ServiceProvider` with a real `ServiceCollection`-built provider. Because
  `CreateAsyncScope()` still calls through to `CreateScope()`, this setup keeps
  working unmodified, but a mocked `IServiceScope` never implements
  `IAsyncDisposable`, so it cannot reproduce the reported crash. The new
  acceptance test therefore belongs with the real-DI-container integration
  tests in `tests/Chaos.Mongo.Outbox.Tests/Integration/TypedOutboxIntegrationTests.cs`,
  where the built-in container's scope performs genuine async-preferring
  disposal.
- Add an async-only publisher test double alongside the existing
  `TrackedTypedPublisher`/`TypedPublicationHub` pair in that file (e.g.
  `TrackedAsyncOnlyPublisher : IOutboxPublisher, IAsyncDisposable`, tracking
  disposed IDs through the hub the same way `TrackedTypedPublisher` does for
  `IDisposable`). Register it with `WithPublisher<TrackedAsyncOnlyPublisher>`
  at a non-singleton lifetime (`Scoped` or `Transient`) so a scope is actually
  created and disposed per batch, run a batch through
  `WithOutbox<FirstDestination>` (or a dedicated destination), and assert the
  publisher's id appears as disposed — mirroring the assertion style already
  used by `StartAsync_SharedPublisherImplementation_ResolvesPerDestinationAndBatch`.
  Confirming this test fails against the current `CreateScope()`/`using`
  code (by reproducing the reported `InvalidOperationException`) demonstrates
  it actually exercises the fixed defect.
