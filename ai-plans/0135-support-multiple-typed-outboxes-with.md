# Support multiple typed outboxes with independent configuration and processing

> Issue: [#135](https://github.com/chA0s-Chris/Chaos.Mongo/issues/135)

## Rationale

Applications publishing to independent integrations need separate outbox
destinations with their own publishers, processing capacity, and policies.
The current registration permits only one outbox and resolves one shared
publisher contract.

Add marker-type outboxes while preserving the existing default outbox API.
Deliver the registration, processing, compatibility, and verification changes
in one ordinary PR.

## Acceptance Criteria

- [ ] Two typed outboxes and a default outbox can coexist and resolve
      independently through dependency injection. Existing single-outbox
      APIs and tests remain compatible.
- [ ] Each outbox writes only to its configured collection, validates payloads
      against its own registry, and resolves its configured publisher with
      the selected transient, scoped, or singleton lifetime.
- [ ] Collection indexes, batch size, polling interval, lock timeout,
      processing filter, retry settings, and retention policy remain
      isolated per outbox.
- [ ] Starting or stopping one processor does not affect another.
      A slow or failing publisher does not prevent another outbox from
      processing messages.
- [ ] Automatic startup initializes each enabled outbox before starting its
      processor, exactly once per host startup, including when general
      MongoDB configurator startup is also enabled. Outboxes without
      automatic startup remain available for manual lifecycle control.
- [ ] Host shutdown signals every auto-started processor to stop without
      waiting for another processor to finish. A delayed publisher does not
      delay cancellation of another outbox.
- [ ] Writes to multiple outboxes using the same compatible MongoDB session
      commit or roll back atomically in the caller's transaction.
- [ ] Duplicate marker identities and collection conflicts, including
      conflicts with the default outbox, produce clear configuration errors.
- [ ] Payload types can be reused across outboxes without BSON class-map
      conflicts; each destination retains its own message discriminators.
- [ ] Processing and lifecycle logs identify the associated outbox.
- [ ] Automated tests cover registration, publisher lifetimes, configuration
      and lifecycle isolation, shared payloads, and compatibility.
      MongoDB integration tests verify collection and index isolation,
      independent publication, and transactional commit and rollback.
      The complete test suite and Release build pass, and merged line
      coverage remains at least 95%.
- [ ] Public API documentation and the transactional outbox documentation
      explain registration, injection, lifecycle control, manual
      initialization, publisher lifetimes, collection conflicts, and
      transactional usage.

## Technical Details

Add these new public APIs:

- MongoBuilderExtensions.WithOutbox<TOutbox>(
  Action<OutboxBuilder> configure), returning MongoBuilder.
- IOutbox<TOutbox>, extending IOutbox.
- IOutboxProcessor<TOutbox>, extending IOutboxProcessor.

TOutbox supplies identity only and is never instantiated. Reuse OutboxBuilder
and the existing IOutboxPublisher contract. Keep existing public signatures
and constructors compatible; internal registration and runtime plumbing
should remain non-public.

Maintain registration identity across the shared IServiceCollection, rather
than only within one MongoBuilder instance. Validate duplicate identities and
collection assignments before adding the registration's services or BSON
mappings. Outboxes use the existing IMongoHelper database; this change does
not introduce per-outbox connections or databases.

Retain the existing "Outbox" collection default. Multiple registrations must
select distinct collection names through WithCollectionName. Compare names
ordinally within the shared database and include the conflicting identities
and collection in configuration errors.

Give each registration its own immutable OutboxOptions, writer, processor,
and index configurator. Preserve the default registration's untyped service
resolution; typed registrations must not replace or populate those default
services. Reuse MongoOutbox, OutboxProcessor, and OutboxConfigurator behavior
through shared implementation rather than maintaining separate algorithms.

Replace the typed processing path's unqualified IOutboxPublisher resolution
with registration-specific resolution. Preserve the existing per-batch scope:
scoped and transient publishers resolve within that scope, while singleton
publishers belong to their outbox registration. Reusing one publisher
implementation type across registrations must still honor each registration's
identity and lifetime.

Coordinate OutboxHostedService, OutboxConfiguratorRunner, and the
IMongoConfigurator registration path so general MongoDB startup and outbox
auto-start share successful initialization for each outbox. Each
registration's OutboxConfigurator owns its completion state: a successful run
is remembered and later callers return without repeating index work; failed
or canceled runs are not remembered and must not permit the processor to
start. Make this safe for concurrent callers, because MongoHostedService and
the outbox hosted service may start concurrently under
HostOptions.ServicesStartConcurrently. AddHostedService<T> deduplicates by
implementation type, so register one outbox hosted service that initializes
and starts every auto-start registration, keeping the existing
OutboxHostedService constructor compatible.

During host shutdown, the shared outbox hosted service initiates stopping all
auto-started processors before awaiting their completion, respecting the host
shutdown cancellation token. Add a coordinated test that holds one publisher's
completion and verifies another processor receives cancellation before that
publisher is released.

IOutboxConfiguratorRunner.RunAsync initializes every registered outbox,
default and typed, and remains the manual initialization entry point.
Enabling MongoOptions.RunConfiguratorsOnStartup is equivalent because each
registration's configurator is also registered as IMongoConfigurator.

Each processor owns its processing task and cancellation state. Preserve
existing claim ownership, cancellation cleanup, retries, and processing-filter
semantics. Include outbox identity and collection in diagnostic context.
Render identity as the marker type name for typed outboxes and as "Default"
for the untyped outbox, in both logs and configuration errors.

OutboxSerializationSetup continues to share process-wide BSON class maps.
Keep destination-specific discriminators in each options registry and in
OutboxMessage.Type; do not encode marker identity into the stored document
or change the existing wire format.

Typed writes forward the caller's session using the existing active-transaction
requirement. They do not create or commit transactions. Delivery remains
at-least-once, with no cross-outbox publication ordering guarantee.

Extend tests/Chaos.Mongo.Outbox.Tests using hand-crafted test doubles for new
tests and the existing replica-set Testcontainers infrastructure for MongoDB
integration tests. Use coordinated publishers to prove another outbox
progresses while one is blocked or failing. Cover default/typed registration
in both orders, shared publisher types with different lifetimes, and startup
with general configurators both enabled and disabled.

Update docs/transactional-outbox.md and src/Chaos.Mongo.Outbox/README.md.
