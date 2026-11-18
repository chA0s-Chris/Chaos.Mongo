# Correct the Migration Guide and Fill the Remaining Docs Gaps

> Issue: [#253](https://github.com/chA0s-Chris/Chaos.Mongo/issues/253)

## Rationale

`docs/migrating-from-0.x.md` grew incrementally while the 1.0 milestone landed. It omits some public API removals,
misdescribes others, and documents tamper-evidence API changes for APIs that never shipped in v0.9.0. A few reference
docs use stale terms, and several topics consumers need before going to production are undocumented.

With the milestone's code changes merged, the public surface is final. The guide can now be derived from an actual
comparison with v0.9.0, and the remaining gaps closed before 1.0.

## Acceptance Criteria

### Migration guide

- [x] Every public type or member of the three v0.9.0 packages that was removed, made non-public, renamed, or changed
  in signature, constraint or kind appears in `docs/migrating-from-0.x.md`, as established by comparing the published
  0.9.0 packages with the current build.
- [x] The guide describes no API that did not exist in v0.9.0 as a change. This includes every tamper-evidence API
  (`.Integrity`, `IntegrityProtectionBuilder<T>`, `ITimestampAuthority`, `TimestampAuthorityException`,
  `IntegrityMetrics`, the integrity collection prefix); tamper evidence appears only as new in 1.0.
- [x] The guide describes `MongoLockDocument` and `MongoMigrationHistoryItem` with their actual 1.0 visibility, and
  states that `IMongoQueuePublisher` and the related queue implementation types are internal.

### Corrections

- [x] `docs/event-store.md` lists the `EventAnchorVerificationResult.Status` values as `Anchored`, `Failed`,
  `NotAnchored` and `NotChecked`.
- [x] The docs use "batch size" instead of "query limit" and "lock lease time" instead of "lock timeout" wherever they
  refer to the renamed options.
- [x] Every C# sample in `docs/`, `README.md` and `src/*/README.md` compiles with the `using` directives it shows, and
  every sample names its cancellation token `cancellationToken`.

### New topics

- [x] `docs/queues.md` states that publishing from a queue handler, or from work protected by an `IMongoLock`, is not
  atomic with completing that work, so a retry or lease-expiry reclaim can publish again; follow-up payloads must
  tolerate duplicates, and the outbox is the option when the publish must be atomic with a database write.
- [x] `docs/transactional-outbox.md` states that `IOutboxPublisher.PublishAsync` must complete within the configured
  lock lease time, because otherwise another processor reclaims the message and publishes it again.
- [x] `docs/event-store.md` has an event-evolution section describing what happens to stored events that lack an
  element, contain an element the class no longer declares, or hold a value of a changed type; how renaming an event
  class interacts with discriminators; and the recommended approach (additive changes, explicit discriminators, a new
  event type for incompatible changes, no built-in upcasting), including that changing an event class never rewrites
  stored events, which keep their stored shape and keep verifying; apart from retroactive sealing, which adds an
  `_integrity` element to events written before protection was enabled, personal-data erasure is the only operation
  that changes a stored event.
- [x] A collection-names section recommends pinning collection names explicitly for production and covers
  `CollectionTypeMap`, the event store's and integrity builders' `WithCollectionPrefix`, and the queue's and outbox's
  `WithCollectionName`, linking to the naming rules in `docs/compatibility-policy.md`.
- [x] `docs/event-store.md` states that `IEventStore<TAggregate>`, `IAggregateRepository<TAggregate>` and
  `IPersonalDataEraser<TAggregate>` (the latter when integrity protection is enabled) are registered as scoped services
  and shows how to use them from singletons and hosted services.
- [x] A new `docs/production-checklist.md`, linked from `README.md`'s docs table, states for each package which indexes
  are created and how (configurators at startup, or out-of-band with the required roles), which collections expire or
  are retained under which option and default, and which meters to export, with links to the suggested alerts.
- [x] Automated tests pin each documented event-evolution behavior for class maps the event store builds (a missing
  element deserializes to the member's default unless the member is marked `[BsonRequired]`, an undeclared element is
  ignored, an element whose stored type no longer matches fails as documented) and the scoped registration of
  `IEventStore<TAggregate>` and `IAggregateRepository<TAggregate>`, using existing tests where they already cover it.

## Technical Details

**API comparison.** v0.9.0 has no `PublicAPI` files and no `Integrity` code, so the issue's proposed diff of
`PublicAPI` files is not possible. Compare against the published 0.9.0 NuGet packages instead with package validation
(`PackageValidationBaselineVersion=0.9.0`, generating a suppression file to collect every difference). This is a
one-off run; do not commit the baseline. By default the tool reports only breaking changes, so its output is the
minimum the guide must cover; the guide's existing entries for non-breaking changes, such as the removed `new()`
constraints, stay. Breaking additions, such as new members on public interfaces (for example
`IAggregate.Revision` and `IAggregateRepository<TAggregate>.GetAtRevisionAsync`), belong in the guide; purely
additive APIs do not. The baseline targets `net8.0`/`net9.0` while the current packages target `net10.0`/`net11.0`;
the dropped targets are already documented.

**Guide structure.** Correct the existing per-change sections rather than rewriting them; their behavioral notes stay.
Remove what never shipped: the "Event Store: integrity collection naming" section, the integrity bullets of the
EventStore API trim section, the "sealed events still verify" clause, and the integrity-state collection in the
collision-detection section. A single pointer to tamper evidence as a new 1.0 feature is allowed. The issue's line
numbers are stale; locate errors by content. The `MongoDefaults` const-to-static bullet still names `QueryLimit`,
which was later renamed to `QueueBatchSize`.

**Sample compilation.** Compile every sample once in a throwaway project that references the current `src/`
projects, and report the result in the PR. Assume only the implicit usings of `Microsoft.NET.Sdk.Web`; every
`Chaos.Mongo.*`, `MongoDB.*` or other namespace must appear in the sample itself. Fragments may receive wrapping
context, but no hidden `using` directives.

**Placement.** The queue lease rule goes in `docs/queues.md` and the publisher lease rule in
`docs/transactional-outbox.md`. Collection-name pinning becomes a new "Collection names" section in
`docs/configuration.md`. The production checklist links to existing sections (MongoDB roles, TTL indexes, suggested
alerts, metrics tables) instead of duplicating them.
