# Resolve queue payload handlers in a DI scope per item

## Rationale

[`MongoQueueSubscription<TPayload>.ProcessQueueItemAsync`](../src/Chaos.Mongo/Queues/MongoQueueSubscription.cs) resolves a handler for every queue item through `IMongoQueuePayloadHandlerFactory`. The default implementation, [`MongoQueuePayloadHandlerFactory`](../src/Chaos.Mongo/Queues/MongoQueuePayloadHandlerFactory.cs), holds the root `IServiceProvider` and resolves handlers from it, even though handlers are registered transient. Two problems follow: a handler cannot depend on scoped services such as a `DbContext`, and any handler implementing `IDisposable`/`IAsyncDisposable` is kept alive by the root provider until the application shuts down, because the root container tracks every disposable it creates. The subscription currently works around the second problem by manually disposing the handler after each item, which only addresses the handler itself, not any scoped dependency it resolved internally. [`OutboxProcessor`](../src/Chaos.Mongo.Outbox/OutboxProcessor.cs) already resolves its publisher from an `IServiceScopeFactory`-created scope per batch; this plan applies the equivalent pattern per queue item (issue [#223](https://github.com/chA0s-Chris/Chaos.Mongo/issues/223)).

Giving each queue item its own DI scope lets handler factories resolve scoped services and lets the container dispose the handler (and anything it resolved) deterministically when the scope ends, regardless of whether handling succeeded, failed, or was canceled.

## Acceptance Criteria

- [ ] Each queue item's payload handler is resolved from a DI scope created for that item, and the scope is disposed after handling completes, on success, on failure, and on cancellation.
- [ ] A handler resolved through `WithPayloadHandler<TPayloadHandler>()` (the handler-type registration) can depend on a scoped service and is disposed when its item's scope ends; a test proves both.
- [ ] A handler resolved through `WithPayloadHandler(Func<IServiceProvider, ...>)` (the factory overload) receives the item's scoped `IServiceProvider`, can depend on a scoped service, and is disposed when its item's scope ends; a test proves both.
- [ ] The tests above build their `IServiceProvider` with scope validation enabled (`ServiceProviderOptions.ValidateScopes = true`) so a regression that resolves a scoped service from the root provider fails the test instead of silently succeeding.
- [ ] `docs/queues.md` describes the per-item scope and no longer states that a factory "runs with the root `IServiceProvider`" or "must not resolve scoped services".
- [ ] The Release build produces no warnings and the full test suite passes.

## Technical Details

- `IMongoQueuePayloadHandlerFactory` (internal, `src/Chaos.Mongo/Queues/IMongoQueuePayloadHandlerFactory.cs`) changes `CreateHandler<TPayload>()` to accept the resolving `IServiceProvider` explicitly:

  ```csharp
  internal interface IMongoQueuePayloadHandlerFactory
  {
      IMongoQueuePayloadHandler<TPayload> CreateHandler<TPayload>(IServiceProvider serviceProvider)
          where TPayload : class, new();
  }
  ```

  `MongoQueuePayloadHandlerFactory` drops its constructor-injected `IServiceProvider` field and resolves `IMongoQueuePayloadHandler<TPayload>` from the parameter instead. This keeps the factory itself free of captured provider state, so the caller controls which provider (root or scoped) resolution uses.
- `MongoQueueSubscription<TPayload>` takes a new `IServiceScopeFactory` constructor dependency (validated with `ArgumentNullException.ThrowIfNull`, matching the existing parameter checks). In `ProcessQueueItemAsync`, wrap handler resolution and `HandlePayloadAsync` in `await using var scope = _serviceScopeFactory.CreateAsyncScope();` and call `_payloadHandlerFactory.CreateHandler<TPayload>(scope.ServiceProvider)`. The `await using` on the method's existing outer `try` ensures the scope is disposed whether the call succeeds, throws (including into `HandleFailedQueueItemAsync`'s `catch`), or observes cancellation.
- Remove the manual `IDisposable`/`IAsyncDisposable` disposal of `payloadHandler` in the `finally` block; disposing the per-item scope now disposes every disposable the scope resolved, including the handler and any scoped services it took as dependencies, without the `SuspiciousTypeConversion` type-check idiom.
- `MongoQueueSubscriptionFactory` gains the same `IServiceScopeFactory` dependency (resolvable from DI since the hosting framework registers it; `MongoHostedService` already consumes it the same way) and passes it through to `MongoQueueSubscription<TPayload>`'s constructor.
- The handler-factory builder overload already registers `Func<IServiceProvider, IMongoQueuePayloadHandler<TPayload>>` via `_services.AddTransient(_payloadHandlerFactory)` (`MongoQueueBuilder.cs`); no registration change is needed there — calling `scope.ServiceProvider.GetRequiredService<...>()` (via the factory's new parameter) already passes the scope's provider into the user-supplied `Func`.
- `docs/queues.md`'s handler-factory section (around line 51) currently states the factory "runs with the root `IServiceProvider` ... so it must not resolve scoped services"; update it to describe per-item scope resolution and that scoped services are supported for both the handler-type and factory registrations.
