# Provider-Aware Publisher Factory for WithOutbox; Document Queue Handler Factory

> Issue: [#194](https://github.com/chA0s-Chris/Chaos.Mongo/issues/194)

## Rationale

Following [#193](https://github.com/chA0s-Chris/Chaos.Mongo/issues/193), consumers expect provider-aware configuration, but `WithOutbox` and `WithQueue` cannot accept `Action<IServiceProvider, …>` delegates: their configure delegates run at registration time and their output determines which services are registered and enables fail-fast conflict checks. The only outbox component that actually needs DI dependencies is the publisher, which is resolved at runtime, yet it can only be configured as a type today.

The goal is a lazy, provider-aware publisher factory for the default and typed outboxes, while keeping eager registration and its fail-fast checks. Queues already offer a payload-handler factory overload; it only needs documentation.

## Acceptance Criteria

- [ ] `OutboxBuilder.WithPublisher(Func<IServiceProvider, IOutboxPublisher>, ServiceLifetime)` exists, defaulting to `ServiceLifetime.Transient`. The processor resolves the factory-created publisher with the configured lifetime for both the default outbox and `WithOutbox<TOutbox>` outboxes.
- [ ] `OutboxBuilder.PublisherFactory` is a new public property exposing the configured factory. Once a publisher is configured, exactly one of `PublisherType` and `PublisherFactory` is non-null. The XML documentation of both properties states this.
- [ ] The type-based and factory-based `WithPublisher` overloads replace each other's configuration, including the lifetime; the last call wins.
- [ ] `OutboxBuilder.Validate()` accepts either a publisher type or a factory. When neither is configured, it throws `InvalidOperationException` with a message naming both overloads. A null factory throws `ArgumentNullException`.
- [ ] Unit tests cover factory registration for each lifetime and both outbox kinds, last-call-wins in both directions between the type and factory overloads, and validation when neither is set. Merged line coverage remains at least 95%.
- [ ] An integration test shows a factory-created publisher receiving messages from the processor.
- [ ] `docs/transactional-outbox.md` and `src/Chaos.Mongo.Outbox/README.md` document the factory overload, including which service provider the factory receives for each lifetime. The Builder Options table lists the factory overload and states that one of the two `WithPublisher` forms is required.
- [ ] `docs/queues.md` documents the existing `MongoQueueBuilder<TPayload>.WithPayloadHandler(Func<IServiceProvider, IMongoQueuePayloadHandler<TPayload>>)` overload with an example.

## Technical Details

New public API on `OutboxBuilder` (exact signatures, XML documentation required):

```csharp
public Func<IServiceProvider, IOutboxPublisher>? PublisherFactory { get; private set; }

public OutboxBuilder WithPublisher(Func<IServiceProvider, IOutboxPublisher> factory,
                                   ServiceLifetime lifetime = ServiceLifetime.Transient);
```

- `MongoBuilderExtensions.Register` no longer requires `PublisherType`. It registers either the existing type descriptor or a factory descriptor: unkeyed for the default outbox, and keyed by the `OutboxRegistration` for typed outboxes, so `OutboxProcessor`'s `GetRequiredKeyedService<IOutboxPublisher>(_publisherKey)` keeps working. The existing order of fail-fast checks (duplicate marker, validation, collection-name conflict, then class-map registration) is unchanged. The registered descriptor wraps the factory and throws `InvalidOperationException` naming the outbox identity when it returns `null`.
- The processor resolves the publisher from a fresh scope once per nonempty batch. Transient and scoped factories therefore receive that batch scope's provider and can resolve scoped dependencies. A singleton factory is invoked once, with the root provider, and must not resolve scoped services, consistent with the existing rule for singleton publishers. The XML documentation and user documentation must state this.
- `ServiceLifetime` is not validated, matching the existing `WithPublisher<TPublisher>(ServiceLifetime)`.
- The change is additive. Only the text of the `Validate()` error message changes.
- Non-obvious test scenarios: with `ValidateScopes = true`, a scoped or transient factory that resolves a scoped dependency succeeds; a factory returning `null` fails resolution with the outbox-specific `InvalidOperationException`.
- Out of scope, per the issue: provider-aware delegates for `WithOutbox(Action<OutboxBuilder>)` or `WithQueue(Action<MongoQueueBuilder<T>>)` themselves, lazily resolved scalar settings, and any new queue API.
