# Fix and Complete the Reference Documentation

## Rationale

A documentation review for [#219](https://github.com/chA0s-Chris/Chaos.Mongo/issues/219) found that the reference
docs under `docs/`, the package READMEs and `tools/tamper-evidence-verifier` have fallen behind the 1.0 API: one
sample does not compile, a chapter's sections are out of order, several public extension points and option types
ship without documentation, and the Python reference verifier's usage text and README are incomplete. This plan
closes those gaps so the documentation set matches the shipped 1.0 API and every documented public member carries
an XML doc comment (tracked by `PublicAPI.Shipped.txt` per #215).

Several line numbers the issue gives have drifted from the current `main` (for example, the broken retry sample is
at `docs/event-store.md:706`, not `:727-752`); this plan anchors each change to current content instead of the
issue's line numbers.

## Acceptance Criteria

**`docs/event-store.md`**

- [ ] The "Handling Concurrency Conflicts with Explicit Versions" sample (currently `docs/event-store.md:706`,
  which references an undeclared `event` identifier) is valid, compilable C#.
- [ ] "Benchmarking Bulk Writes" and "Architecture Decision: Bulk-Write Scope" no longer sit between the
  Configuration chapter's "Builder Options" and "Checkpoints"/"Collections Created" subsections; the table of
  contents reflects the new order.
- [ ] The Quick Start's "4. Use the Event Store" sample appends with a store-assigned version (`Version` left at
  its default of `0`) instead of calling `GetExpectedNextVersionAsync` for an explicit version. The same change
  applies to `src/Chaos.Mongo.EventStore/README.md`'s own "Quick start" section, which shows the same
  explicit-version anti-pattern.
- [ ] The docs show how to construct a `TrustedAnchorHead` for `CheckCompletenessAsync`/`VerifyAnchorChainAsync`.
- [ ] The docs describe the `ITimestampAuthority` contract (`Name`, `GetRevocationListsAsync`, `TimestampAsync`) for
  implementers adding a custom authority via `WithTimestampAuthority`.
- [ ] The docs describe every member of `Rfc3161TimestampAuthorityOptions` (`AttemptTimeout`, `HttpClientFactory`,
  `MaximumClockSkew`, `MaximumResponseSize`, `Name`, `PolicyId`, `RevocationCheckTimeout`, `RevocationMode`,
  `TrustedRoots`, `Uri`).
- [ ] The docs describe `IntegrityAnchoringBuilder.WithMaxLeavesPerAnchor` and
  `IntegrityProtectionBuilder<TAggregate>.WithSealingSweepRetryDelay` alongside the other builder options.
- [ ] The docs describe the members of `StreamVerificationResult`, `EventAnchorVerificationResult`,
  `DerivedDataVerificationResult`, `AnchorChainVerificationResult` and `CompletenessFinding`, including the
  supporting `VerifiedEvent` and `TimestampVerificationResult` members they expose.

**`docs/tamper-evidence-specification.md`**

- [ ] The `verify.py check` example (currently `docs/tamper-evidence-specification.md:272`) notes that its
  `--aggregate-type` value is the aggregate's CLR class name — the string the store wrote to the event's
  `AggregateType` element.

**`docs/transactional-outbox.md`**

- [ ] "Multiple Typed Outboxes" lists what `WithOutbox<TOutbox>` registers, the way "Registering the Outbox" already
  lists what `WithOutbox` registers; both lists name `IOutboxConfiguratorRunner`, which neither currently does.
- [ ] Outbox shutdown behavior is described once, consistently, covering: every automatic processor is canceled
  concurrently (a slow one does not delay another's cancellation); a publish in flight when canceled releases the
  message's lock for immediate redelivery rather than failing it; a publish that already succeeded gets a short
  grace period to finish being marked `Processed` before the lock is released for redelivery instead; and that
  failure bookkeeping (`RetryCount`/`Error`/backoff) is not bound by the shutdown token and runs to completion.

**Other files**

- [ ] Every C# sample in `docs/` and in every README (the root `README.md` and each package README under `src/`) is
  valid, compilable C# against the 1.0 API — not only the samples touched for another reason in this plan. This
  includes moving the misplaced `using MongoDB.Driver;` in `docs/queues.md`'s custom-prioritizer sample (currently
  line 133, between two type declarations) to the top of that snippet, and adding the missing
  `using Microsoft.Extensions.DependencyInjection;` to `src/Chaos.Mongo.Outbox/README.md`'s `ServiceLifetime`
  samples ("Multiple destinations" and the publisher-factory example).
- [ ] Every public type and member listed in `src/Chaos.Mongo/PublicAPI.Shipped.txt`,
  `src/Chaos.Mongo.EventStore/PublicAPI.Shipped.txt` and `src/Chaos.Mongo.Outbox/PublicAPI.Shipped.txt` has an XML
  documentation comment.
- [ ] The reference docs document `IMongoClientSettingsFactory` and `IMongoConnectionFactory`: when to replace one
  of them instead of using `MongoOptions.ConfigureClientSettings`; that a registration of either one, placed before
  or after `AddMongo`, replaces the library's default (both are registered with `TryAddSingleton`); and an example
  implementation that reuses an already-registered `MongoClient` and returns a `MongoConnection`.
- [ ] The reference docs cover every other public core API not already documented by an existing `docs/*.md` page
  or README (the issue's "remaining minor core APIs").
- [ ] A Testing section documents: mocking the public interfaces (`IMongoHelper`, `IMongoMigrationRunner`,
  `IMongoConfiguratorRunner`, and similar) for unit tests; building a `ServiceCollection` and calling `AddMongo`
  with the test database URL for integration tests, so collection names and other builder-configured values match
  production, and resolving the real implementations; registering a `TimeProvider` before `AddMongo` to replace the
  system clock (for example, for lock-lease tests); and testing a single migration by calling its `ApplyAsync`
  directly.

**`tools/tamper-evidence-verifier`**

- [ ] `tools/tamper-evidence-verifier/README.md` exists, describing the tool and linking to
  `docs/tamper-evidence-specification.md` and `docs/event-store.md`'s tamper-evidence section.
- [ ] `verify.py`'s module-level `Usage` docstring (what `--help`/usage errors display) lists `--element-name`,
  which the `check` command already accepts and the specification document already documents.
- [ ] The README states the Python and OpenSSL versions the tool supports.

## Technical Details

**Verified symbols.** `TrustedAnchorHead` (`src/Chaos.Mongo.EventStore/Integrity/TrustedAnchorHead.cs`),
`ITimestampAuthority` (`.../Integrity/ITimestampAuthority.cs`), `Rfc3161TimestampAuthorityOptions`
(`.../Integrity/Rfc3161TimestampAuthorityOptions.cs`), `IntegrityAnchoringBuilder.WithMaxLeavesPerAnchor`
(`.../Integrity/IntegrityAnchoringBuilder.cs:98`), `IntegrityProtectionBuilder<TAggregate>.WithSealingSweepRetryDelay`
(`.../Integrity/IntegrityProtectionBuilder.cs:53`), and the verification result records
(`StreamVerificationResult`, `EventAnchorVerificationResult`, `DerivedDataVerificationResult`,
`AnchorChainVerificationResult`, `CompletenessFinding`, `VerifiedEvent`, all under `.../Integrity/`) already carry
full XML doc comments; this is a documentation-surfacing task, not new source, and the new prose should draw on
those comments rather than restate the issue's phrasing. `IMongoClientSettingsFactory` and `IMongoConnectionFactory`
live in `src/Chaos.Mongo/IMongoClientSettingsFactory.cs` and `IMongoConnectionFactory.cs`; both are registered with
`services.TryAddSingleton(...)` in `ServiceCollectionExtensions.AddMongoInternal` (`src/Chaos.Mongo/ServiceCollectionExtensions.cs:193-194`),
which is the basis for the "replaces the default from either side of `AddMongo`" claim.

**Configuration-chapter reorder.** In `docs/event-store.md`, "## Benchmarking Bulk Writes" and
"## Architecture Decision: Bulk-Write Scope" currently sit between "Builder Options" and "### Checkpoints", even
though the table of contents already lists them as siblings of "## Configuration", not children of it. Move both
sections to after "### Collections Created" (the end of the Configuration chapter) and before
"## Working with Events"; update the table of contents to match the new order. Do not otherwise edit their content.

**Retry sample fix.** The broken sample is the `UpdateWithRetryAsync` example under "Optimistic Concurrency" /
"Handling Concurrency Conflicts with Explicit Versions". It never constructs the event it appends
(`AppendEventsAsync([event])` references an undeclared, keyword-named identifier). Replace it with a sample that
builds a real event (consistent with the method's `Action<OrderAggregate> update` parameter and the surrounding
explicit-version narrative) and appends it with an explicit version, keeping the retry/backoff structure.

**Quick Start store-assigned versions.** Change the Quick Start's `CreateOrderAsync` sample in `docs/event-store.md`
to leave `OrderCreatedEvent.Version` unset (store-assigned) instead of calling `GetExpectedNextVersionAsync` first.
Apply the same change to `src/Chaos.Mongo.EventStore/README.md`'s "Quick start" section (its `OrderCreated` append
sample), which calls `GetExpectedNextVersionAsync` and sets `Version = version` the same way. The "Appending Events"
section in `docs/event-store.md` already shows both the store-assigned and the explicit-version patterns
deliberately and should keep doing so; only the two quick starts need to switch to the store-assigned pattern the
docs recommend in "Best Practices".

**Outbox registrations.** `MongoBuilderExtensions.WithOutbox` (`src/Chaos.Mongo.Outbox/MongoBuilderExtensions.cs:23-31`)
registers `IOutbox`, `IOutboxProcessor`, `OutboxConfigurator`, the configured publisher, and (via the shared
`Register` helper) `IOutboxConfiguratorRunner` and, when auto-start is enabled, `OutboxHostedService`.
`WithOutbox<TOutbox>` (same file, lines 51-64) registers keyed `OutboxConfigurator`/`MongoOutbox`/`OutboxProcessor`,
`IOutbox<TOutbox>`, `IOutboxProcessor<TOutbox>`, the publisher, and the same shared `IOutboxConfiguratorRunner` /
`OutboxHostedService`. Mirror the existing "Registering the Outbox" registration list for the typed case, and add
`IOutboxConfiguratorRunner` to both lists.

**Outbox shutdown behavior.** `OutboxProcessor.StopAsync` (`src/Chaos.Mongo.Outbox/OutboxProcessor.cs:425-452`)
cancels the processor's own `CancellationTokenSource`. A publish in flight observes that cancellation and, in
`ProcessMessageAsync` (same file, lines 362-371), releases the message's lock without recording a failure, making it
immediately eligible for another processor. A publish that already succeeded is finalized in `MarkProcessedAsync`
(lines 151-206): the finalizing write itself is not bound by the caller's cancellation token, but once a stop is
requested, a 3-second grace period (`FinalizationTimeout`) bounds how long that write may still take before the lock
is released for redelivery instead. `HandleFailureAsync` writes its retry/failure bookkeeping without a
cancellation token at all, so it is unaffected by shutdown. At the host level, `OutboxHostedService.StoppingAsync`
(`src/Chaos.Mongo.Outbox/OutboxHostedService.cs:120-135`) stops every registered processor concurrently
(`Task.WhenAll`) and bounds the overall wait by the host's own shutdown token, letting any remaining cleanup finish
in the background rather than extending that deadline. Fold today's two partial descriptions (in "Processor
Startup" and "Multiple Typed Outboxes") into one description that is accurate for both the single default outbox
and multiple typed outboxes.

**`--aggregate-type` note.** `verify.py`'s `genesis()` and `verify_stream()` read the stream's genesis hash from the
event documents' `AggregateType` element (`EVENT_ELEMENTS["AggregateType"]`). `MongoEventStore<TAggregate>` always
writes `typeof(TAggregate).Name` as that element's *value* (`src/Chaos.Mongo.EventStore/MongoEventStore.cs:30,108-112`);
a class map can only rename the BSON *element name* that value is stored under (what `--element-name` addresses,
via `EventDocumentFields<TAggregate>`), not the CLR-class-name value itself. Add a short note at the
`--aggregate-type` example stating the value is the aggregate's CLR class name as it was at the time the event was
written (so it can survive a later CLR rename), and keep that distinct from the separate `--element-name` note for
a renamed BSON element.

**Core extension points.** Document `IMongoClientSettingsFactory`/`IMongoConnectionFactory` next to the existing
`ConfigureClientSettings` documentation (`docs/configuration.md`), or in `docs/getting-started.md` alongside the
other core-service documentation — either is acceptable, but keep the three pieces discoverable from the same page
once Documentation lists are updated. Cover: an ordinary settings change, including one that depends on registered
services, does **not** need a custom `IMongoClientSettingsFactory` — the provider-aware `AddMongo` overloads
(`Action<IServiceProvider, MongoOptions>`, `ServiceCollectionExtensions.cs:49,122,169`) already let the configure
delegate resolve services from the `IServiceProvider` and set `MongoOptions.ConfigureClientSettings` from them,
exactly as `docs/configuration.md`'s "Configuring options with registered services" section already shows; replace
`IMongoClientSettingsFactory` only to change how settings are *constructed* from the URL itself (the factory's own
`CreateMongoClientSettings(MongoUrl)` responsibility), not merely because the settings depend on a service.
Replace `IMongoConnectionFactory` to change how `IMongoConnection` is constructed entirely, for example to reuse an
already-registered `MongoClient` or to validate connectivity eagerly. Cover the `TryAddSingleton` replacement rule
(register before `AddMongo` so the library's default never runs, or register with `AddSingleton` after `AddMongo`
so the later registration wins — both work, state which the docs should recommend); and a short example of a
custom `IMongoConnectionFactory` that wraps a `MongoClient` the caller already registered, returning it inside a
`MongoConnection`.

**Testing section.** Place it where the existing Documentation lists point (e.g. `docs/getting-started.md`, updating
its "Next steps" list and the three README "Documentation" lists if it becomes a new page). Base the mocking guidance
on the already-public `IMongoHelper`, `IMongoMigrationRunner`, `IMongoConfiguratorRunner`, and note any other public
interface in the shipped API surface worth naming (e.g., `IMongoQueue<TPayload>`, `IOutbox`, `IEventStore<TAggregate>`)
as fitting the same pattern. The integration-test guidance should show `new ServiceCollection().AddMongo(testDatabaseUrl, ...)`
resolving real implementations from the built provider. The `TimeProvider` guidance should show registering a test
`TimeProvider` with `services.AddSingleton<TimeProvider>(...)` before the `AddMongo` call, relying on the library's
own `TryAddSingleton(TimeProvider.System)` (`ServiceCollectionExtensions.cs:192`) to leave it untouched. The migration
guidance should show instantiating an `IMongoMigration` implementation directly and calling `ApplyAsync` with a test
`IMongoHelper`/session, without going through `IMongoMigrationRunner`.

**Minor core APIs and README `using` fixes.** Treat "remaining minor core APIs" and "every sample compiles" as
acceptance criteria to satisfy by auditing `PublicAPI.Shipped.txt` against the existing `docs/*.md` pages during
implementation, rather than a fixed list to enumerate here; the two concrete compile bugs already identified above
(`docs/queues.md:133`'s misplaced `using MongoDB.Driver;`, and the missing
`using Microsoft.Extensions.DependencyInjection;` in `src/Chaos.Mongo.Outbox/README.md`'s `ServiceLifetime` samples)
must be fixed either way.

**Validation approach.** This is a documentation-only change; no production code or its behavior changes, so no new
automated test coverage is required. Verify compilability of **every** C# sample in `docs/` and in every README
(root `README.md` and each `src/*/README.md`) — not only the samples changed or added for another reason in this
plan — with small, scoped scratch compilations (for example, pasting a snippet into a throwaway console project
referencing the relevant package). Where a snippet is an intentional fragment (e.g., it relies on a type or `using`
shown earlier in the same page), supply that documented enclosing context rather than treating the fragment as
broken. Every published C# sample must compile as shown with that context; a bare `...` placeholder standing in
for omitted statements is invalid C# and is not an acceptable fragment on its own — fix it in the published sample
itself, either by supplying the minimum initializer values the surrounding narrative implies or, where the
omission is genuinely incidental to the point being shown, by turning it into a C# comment (`/* ... */`) instead
of a bare token. A label in the surrounding prose does not substitute for compiling. Two such samples are already
known: the Idempotency section's `AppendEventsAsync([new OrderCreatedEvent { Id = eventId, ... }])` (currently
`docs/event-store.md:729`) and the Transactional Outbox Pattern section's
`AppendEventsAsync([new OrderCreatedEvent { ... }], onBeforeCommit: ...)` (currently `docs/event-store.md:797`);
both must become compilable object initializers. Keep a reviewable per-sample record of what was compiled and its
outcome outside Git (e.g. under the task's workflow-state directory); do not add a markdown-snippet-extraction
pipeline or any other permanent tooling for this. Verify the XML-doc acceptance criterion by cross-referencing each `PublicAPI.Shipped.txt` entry against
its symbol's doc comment (for example, by comparing against the generated `obj/**/*.xml` documentation file with
`GenerateDocumentationFile` enabled for a scoped build); `<TreatWarningsAsErrors>` does not catch missing XML docs
here because the repository suppresses `CS1591` (`Directory.Build.props:14`).
