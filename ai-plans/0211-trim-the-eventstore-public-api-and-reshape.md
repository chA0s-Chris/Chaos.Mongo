# Trim the EventStore public API and reshape the integrity types

## Rationale

[Issue #211](https://github.com/chA0s-Chris/Chaos.Mongo/issues/211) applies the
public-API visibility rule already used for `Chaos.Mongo`
(`ai-plans/0210-trim-the-public-api-of-chaos-mongo.md`) and
`Chaos.Mongo.Outbox` (`ai-plans/0212-trim-the-outbox-public-api.md`) to
`Chaos.Mongo.EventStore`: public types are a SemVer contract and must be
sealed, and everything a consumer does not configure, construct, or extend
directly becomes `internal`. Of the project's 65 public types, about 45 live
in `.Integrity`, including 18 positional result records whose constructor
shape cannot absorb a new field without a breaking change, and a timestamp
boundary (`ITimestampAuthority`) that mixes `DateTime` and `DateTimeOffset`
inconsistently with the rest of the library's `…Utc`-suffixed `DateTime`
convention. The 1.0 decisions settled for this issue: `DateTime` with a
`…Utc` suffix everywhere, validated for `DateTimeKind.Utc` at the
`ITimestampAuthority` boundary; `init`-property records with `required` core
fields instead of positional records, so a later field addition breaks
neither a constructor call nor a `Deconstruct` pattern; and `CompletenessFinding`
stays one flat record (not a discriminated union per `Kind`), documented so
each `CompletenessFindingKind` value's populated-field subset is clear.
Tightening visibility, reshaping these records, and fixing the time types
reduces the package's 1.0 compatibility commitment without changing stored
data formats or observable runtime behavior, other than the metrics and
documentation additions the issue also asks for.

## Acceptance Criteria

- [ ] `MongoEventStore<TAggregate>`, `MongoAggregateRepository<TAggregate>`,
      `MongoEventStoreConfigurator<TAggregate>`, `MongoEventStoreSerializationSetup`,
      `CheckpointDocument<TAggregate>`, and `CheckpointId` are `internal`.
- [ ] `MongoEventStoreBuilder<TAggregate>`'s constructor and its `Options`
      property getter are `internal`; the class itself stays `public sealed`.
- [ ] `MongoEventStoreOptions<TAggregate>` stays `public sealed`; every
      property setter (`BulkWriteOptimizationEnabled`, `CheckpointCollectionSuffix`,
      `CheckpointInterval`, `CollectionPrefix`, `EventsCollectionSuffix`,
      `MaxAppendRetries`) becomes `internal set`, and `EventTypes` becomes
      `IReadOnlyDictionary<Type, String>` instead of `Dictionary<Type, String>`.
- [ ] `IEventStore<TAggregate>.GetEventStream` is renamed to `GetEventStreamAsync`,
      with its implementation, every call site across `src/`, `tests/`, and
      `docs/event-store.md`, and the type's `IAsyncEnumerable<T>`-returning-method
      naming convention documented consistently with `IEventStore<TAggregate>`'s
      other `…Async` members.
- [ ] `TimestampAuthorityException` moves from `Chaos.Mongo.EventStore.Integrity`
      to `Chaos.Mongo.EventStore.Errors`, alongside `MongoEventStoreException`.
- [ ] `IntegrityProtectionBuilder<TAggregate>` moves from `Chaos.Mongo.EventStore`
      to `Chaos.Mongo.EventStore.Integrity`.
- [ ] `ITimestampAuthority.GetRevocationListsAsync`'s `issuedNotBefore` parameter,
      `RevocationLists.NextUpdate`, `RevocationLists.IssuedTooEarly`'s parameter,
      and `TimestampToken`'s constructor parameter and `Timestamp` property are
      `DateTime` with a `…Utc` suffix (`issuedNotBeforeUtc`, `NextUpdateUtc`,
      `nextUpdateUtc`, `timestampUtc`/`TimestampUtc`) instead of `DateTimeOffset`.
- [ ] `Rfc3161TimestampAuthority.GetRevocationListsAsync` throws `ArgumentException`
      when `issuedNotBeforeUtc.Kind != DateTimeKind.Utc`, `RevocationLists.IssuedTooEarly`
      throws `ArgumentException` when `nextUpdateUtc.Kind != DateTimeKind.Utc`,
      and `TimestampToken`'s constructor throws `ArgumentException` when
      `timestampUtc.Kind != DateTimeKind.Utc`; all three are covered by tests, including a
      test demonstrating that a custom `ITimestampAuthority` returning a non-UTC
      `TimestampToken` is recorded as an ordinary authority failure by `AnchoringRun`, not
      an unhandled exception, and a test demonstrating that a custom `ITimestampAuthority`
      whose `GetRevocationListsAsync` calls `RevocationLists.IssuedTooEarly` with a non-UTC
      `nextUpdateUtc` is recorded as an ordinary confirmation failure by
      `TimestampRevocationConfirmer`, not an unhandled exception, alongside a positive test
      that a UTC `nextUpdateUtc` is accepted and used unchanged.
- [ ] `AnchorAuthorityFailure`, `AnchorChainVerificationResult`, `AnchorLeaf`,
      `AnchorReference`, `AnchorTimestamp`, `CheckpointVerification`,
      `CompletedAnchor`, `CompletenessFinding`, `DerivedDataVerificationResult`,
      `EventAnchorVerificationResult`, `IntegritySealingStatus`,
      `PersonalDataPathStatus`, `StreamVerificationResult`,
      `TimestampConfirmationFailure`, `TimestampVerificationResult`,
      `TrustedAnchorHead`, `UnrecordedErasure`, and `VerifiedEvent` are declared
      with `init`-only properties instead of a positional primary constructor,
      with every field that is always present marked `required`; no production
      or test code constructs any of them positionally afterwards.
- [ ] A fixed BSON fixture captured from the current, pre-conversion
      `AnchorStore`/`AnchorEvidenceStore` output exists for each hand-serialized
      record (`AnchorAuthorityFailure`, `AnchorLeaf`, `TimestampConfirmationFailure`)
      and for one evidence blob, committed under
      `tests/Chaos.Mongo.EventStore.Tests/Integration/Fixtures/`; a test reads each
      fixture with the converted code and confirms every decoded value
      (including UTC timestamps, failure reasons, and resolved evidence bytes)
      matches what was captured before the conversion.
- [ ] `CompletenessFinding`'s XML documentation states, for each
      `CompletenessFindingKind` value, which of its properties are populated and
      which stay at their default.
- [ ] A public `IntegrityMetrics` static class exposes `MeterName`, a nested
      `Instruments` class with the five existing instrument name constants, and
      a nested `Tags` class with an `Authority` tag constant that
      `IntegrityAnchoringDiagnostics.RecordRevokedTimestamp` applies, mirroring
      `Chaos.Mongo.Queues.MongoQueueMetrics`'s shape; `IntegrityAnchoringDiagnostics`
      stays `internal` and sources its instrument names from `IntegrityMetrics`
      instead of declaring its own constants.
- [ ] `IntegritySealMode`'s XML documentation states that it is persisted as a
      string through `MongoEventStoreSerializationSetup`'s explicit
      `EnumSerializer<IntegritySealMode>(BsonType.String)`, so its member names
      (not the hash-chain's separate fixed byte encoding) are the storage
      contract and must not be renamed.
- [ ] A test demonstrates that a `TAggregate` read-model document, an `Event<TAggregate>`
      document, a `CheckpointDocument<TAggregate>` document, and a document for each of
      `EventIntegrity`, `IntegritySealingState`, `PersonalDataCommitment`, and
      `PersonalDataErasureEntry` each still deserialize successfully — through an actual
      `BsonSerializer.Deserialize` call, not a mapping-flag or serialized-field-name
      assertion — from a BSON document containing an extra, unrecognized element, with
      every known value preserved.
- [ ] Every public class in `Chaos.Mongo.EventStore` is sealed except `Aggregate`,
      `Event<TAggregate>`, `ObservationalEvent<TAggregate>`, and `MongoEventStoreException`.
- [ ] `src/Chaos.Mongo.EventStore/PublicAPI.Unshipped.txt` records a `*REMOVED*`
      entry for every shipped type, member, and accessor this plan moves to
      `internal` or removes, and a new entry for every renamed, moved, or
      reshaped public symbol, and the `Microsoft.CodeAnalysis.PublicApiAnalyzers`
      checks (RS0016/RS0017) report no errors.
- [ ] `tests/Chaos.Mongo.EventStore.Tests/Integrity/PublicIntegritySurface.approved.txt`
      is regenerated to match the new public surface and the diff is reviewed to
      confirm it reflects only this plan's changes.
- [ ] `docs/event-store.md` uses `GetEventStreamAsync` throughout and no longer
      warns against pre-registering a `CheckpointDocument<TAggregate>` class map,
      since consumers can no longer reference that now-internal type at all.
- [ ] The Release build (`dotnet build -c Release Chaos.Mongo.slnx`) produces no
      warnings, and `bash ./build.sh Test` passes the complete suite with at
      least 95% merged line coverage, per `tests/AGENTS.md`.

## Technical Details

**Scope confirmation.** `src/Chaos.Mongo.EventStore/Chaos.Mongo.EventStore.csproj`
already grants `InternalsVisibleTo` to `Chaos.Mongo.EventStore.Tests`,
`Chaos.Mongo.EventStore.Benchmarks`, and `Chaos.Mongo.EventStore.LoadTests`; no
new grant is needed. A repository-wide search confirms every public class in
the project is already `sealed` except the four documented base classes and
the 18 records this plan converts (also already `sealed`), so the
"every public class is sealed" criterion needs no additional changes beyond
what follows. Neither project's benchmarks reference any symbol this plan
touches.

**`MongoEventStoreBuilder<TAggregate>`.** Add an explicit
`internal MongoEventStoreBuilder()` constructor (replacing today's implicit
public one) and change `Options`'s getter to `internal`. Only
`MongoBuilderExtensions.WithEventStore` constructs the builder and reads
`Options`, both in the same assembly; `tests/Chaos.Mongo.EventStore.Tests/MongoEventStoreBuilderTests.cs`
constructs it and reads `.Options` directly too, which keeps compiling
because the test assembly already has internal access.

**`MongoEventStoreOptions<TAggregate>.EventTypes`.** Back it with a private
`Dictionary<Type, String>` and change the property to
`public IReadOnlyDictionary<Type, String> EventTypes => _eventTypes;`. Add
`internal void AddEventType(Type eventType, String discriminator)` that sets
`_eventTypes[eventType] = discriminator`, and change
`MongoEventStoreBuilder.cs`'s `WithEvent<TEvent>` from
`Options.EventTypes[eventType] = discriminator;` to
`Options.AddEventType(eventType, discriminator);`. Tests that build options
by hand and mutate `EventTypes` directly (`InheritedMemberRegistrationTests.cs`,
`MongoEventStoreQueryContractTests.cs`, `MongoEventStoreBulkWriteTests.cs`)
switch to the same internal method; the compiler flags every remaining direct
indexer write against the now-read-only interface, so none can be missed.
`MongoBuilderExtensions.cs`, `MongoEventStoreSerializationSetup.cs`, and
`PersonalDataPathResolver.cs` only ever read `EventTypes` and need no change.
`MongoEventStoreOptions<TAggregate>` is already registered as a DI singleton
in `MongoBuilderExtensions.WithEventStore` (`builder.Services.AddSingleton(options);`),
which is why it can stay the public, read-only view that projections and
change-stream consumers inject to read `EventsCollectionName`,
`CheckpointCollectionName`, and `ReadModelCollectionName` without needing the
builder.

**`GetEventStreamAsync` rename.** Rename the interface method, its
implementation in `MongoEventStore.cs`, and every call site; `docs/event-store.md`
has both a code-block declaration, three usage examples, and several bare-word
prose mentions that all need the new name.

**`TimestampAuthorityException` move.** The type is already `Chaos.Mongo.EventStore.Errors`'s
sibling in structure (it derives from `MongoEventStoreException` with
`using Chaos.Mongo.EventStore.Errors;`); move the file into `Errors/` and
change its namespace. Every current reference
(`Rfc3161TimestampAuthority.cs`, `ITimestampAuthority.cs`, and the
`Rfc3161TimestampAuthority*Tests.cs` files) resolves it today through same-namespace
visibility and needs an added `using Chaos.Mongo.EventStore.Errors;`.

**`IntegrityProtectionBuilder<TAggregate>` move.** Move the file into
`Integrity/` and change its namespace. `MongoEventStoreOptions.cs` already has
`using Chaos.Mongo.EventStore.Integrity;`; `MongoEventStoreBuilder.cs` has no
`using` statements today and needs one added.
`tests/Chaos.Mongo.EventStore.Tests/Integrity/PublicIntegritySurfaceTests.cs`
discovers every public type in the `Integrity` namespace by reflection
(`typeof(IEventStoreIntegrity<>).Assembly.GetExportedTypes().Where(t => t.Namespace == typeof(IEventStoreIntegrity<>).Namespace)`)
and separately `.Append(typeof(IntegrityProtectionBuilder<>))`, because today
the type lives outside that namespace. Once it moves, remove that `.Append`
call; otherwise the type is discovered twice and the approved listing gets a
duplicated block.

**Timestamp boundary.** `ITimestampAuthority.GetRevocationListsAsync`'s
`issuedNotBefore` parameter is the only caller-supplied boundary value;
rename it to `issuedNotBeforeUtc` (type `DateTime`) and add
`Rfc3161TimestampAuthority.GetRevocationListsAsync`'s own guard
(`if (issuedNotBeforeUtc.Kind != DateTimeKind.Utc) throw new ArgumentException(...)`),
documented on the interface as a contract every implementation must uphold.
`TimestampRevocationConfirmer.cs`'s only call site already computes
`issuedNotBefore` as a `DateTime` (`timestamps[0].TimestampUtc + _options.RevocationGracePeriod`)
before wrapping it in `new DateTimeOffset(issuedNotBefore, TimeSpan.Zero)`;
drop that wrapper and pass the `DateTime` directly. `RevocationLists.NextUpdate`
and `IssuedTooEarly`'s parameter become `DateTime` (`NextUpdateUtc`,
`nextUpdateUtc`), with the static `IssuedTooEarly(DateTime nextUpdateUtc)`
factory throwing `ArgumentException` when `nextUpdateUtc.Kind != DateTimeKind.Utc`
before constructing the result (the sibling `Issued` factory passes a `null`
`nextUpdate` to the shared private constructor and needs no guard) — the same
boundary-validation shape as `TimestampToken`'s constructor, because
`IssuedTooEarly` is `RevocationLists`'s only public way to carry a
caller-supplied time across the `ITimestampAuthority` boundary, and a custom
authority (not just the built-in `Rfc3161TimestampAuthority`) can call it with
any `Kind`. `Rfc3161TimestampAuthority.cs`'s only construction site already
derives the value from CRL `ThisUpdate`/`NextUpdate` fields decoded as
`DateTimeOffset` — call `.UtcDateTime` there, which always yields
`DateTimeKind.Utc` and so always satisfies the new guard. The one place the
guard can actually fire is a custom `ITimestampAuthority` implementation that
calls `IssuedTooEarly` with a non-UTC value; `TimestampRevocationConfirmer.ConfirmChainAsync`
already awaits `authority.GetRevocationListsAsync` inside a `try` block whose
`catch` records an ordinary `RecordTimestampConfirmationFailureAsync` failure
for any non-cancellation exception, so the constructor's `ArgumentException`
is caught there automatically — exactly the same "treated as a failed
request" behavior as the `TimestampToken` boundary, without any change to
`TimestampRevocationConfirmer`'s control flow. `TimestampRevocationConfirmer.cs`'s
consumption of `result.NextUpdate` drops its now-unnecessary `.UtcDateTime`
call, reading the already-UTC `DateTime` directly. Add a test double (next to
the existing `FixedRevocationListsAuthority` test double) whose
`GetRevocationListsAsync` calls `RevocationLists.IssuedTooEarly` with a
non-UTC value, to cover the confirmation-failure path end to end, plus a
positive test with a UTC value.

`TimestampToken`'s constructor parameter and `Timestamp` property become
`DateTime timestampUtc` / `TimestampUtc`, with the constructor throwing
`ArgumentException` when `timestampUtc.Kind != DateTimeKind.Utc`.
`Rfc3161TimestampAuthority.cs`'s only construction site passes
`token.TokenInfo.Timestamp` (a `DateTimeOffset` from the parsed RFC 3161
token); call `.UtcDateTime` there too, which always satisfies the guard for a
well-formed token. The one place this guard can actually fire is a *custom*
`ITimestampAuthority` implementation registered through
`IntegrityAnchoringBuilder.WithTimestampAuthority` that constructs a
`TimestampToken` with a non-UTC value. `AnchoringRun.StampAsync`
(`src/Chaos.Mongo.EventStore/Integrity/AnchoringRun.cs`) already wraps its
`await authority.TimestampAsync(...)` call in a `catch (Exception ex) when (!IsCancellation(...))`
that records an `AnchorAuthorityFailure` and continues to the next authority;
the constructor's `ArgumentException` is caught there automatically, which is
exactly the "treated as a failed request" behavior the issue asks for,
without any change to `AnchoringRun` itself. `AnchoringRun.StoreEvidenceAsync`'s
`token.Timestamp.UtcDateTime` becomes a plain `token.Timestamp` read, since the
property is already `DateTime` with `Kind == Utc`. Add a test double (next to
the existing `ChangingAuthority`/`FixedRevocationListsAuthority` test doubles
in `AnchoringRunTests.cs`/`TimestampRevocationConfirmationTests.cs`) whose
`TimestampAsync` throws or constructs a non-UTC `TimestampToken`, to cover the
authority-failure path end to end.

**Positional record → `init` conversion.** For each of the 18 types, declare
every field the current primary constructor always receives as a non-nullable
value with `required`; fields already nullable in the primary constructor
(for example `AnchorChainVerificationResult.Failure`, `StreamVerificationResult.FirstBrokenVersion`,
or every non-`Kind` field of `CompletenessFinding`) become plain `init`
properties without `required`. `AnchorTimestamp` and `StreamVerificationResult`
already declare additional `init` properties beyond their primary constructor
(for example `AnchorTimestamp.RevocationStatus`, `StreamVerificationResult.Events`);
leave those exactly as they are. `StreamVerificationResult`'s existing custom
`Equals`/`GetHashCode` override (which deliberately excludes `Events` from
equality) must be preserved unchanged through the conversion. Removing the
primary constructor also removes the compiler-generated `Deconstruct`; a
repository-wide search found no positional pattern matching or `Deconstruct`
usage on any of the 18 types, so none needs to be hand-written back. Every
construction site that currently passes constructor arguments positionally —
including the three types `AnchorStore.cs` hand-serializes to and from BSON
(`AnchorAuthorityFailure`, `AnchorLeaf`, `TimestampConfirmationFailure`) and the
two sites that mix positional arguments with a trailing object initializer
(`AnchorEvidenceResolver.cs`, `AnchorVerifier.cs`, both constructing
`AnchorTimestamp`) — switches to all-named object-initializer syntax; the
compiler rejects every remaining positional call, so none can be missed.
`with`-expression usage (`StreamVerificationResult`, `AnchorTimestamp`) and
constructing an instance through its own static factory methods already
defined in the same file (`CompletedAnchor.From`, `StreamVerificationResult.IntactWith`/`Intact`/`Broken`,
`EventAnchorVerificationResult.NotAnchored`/`NotChecked`) are unaffected by the
primary-constructor removal. Because `AnchorStore.cs`'s hand-written BSON
read/write methods address stored fields by element name, not by reflecting
over the record's constructor, the stored anchor/evidence format is unchanged
by this conversion. The existing `AnchorStore`-backed integration tests only
read back documents written by the same, already-converted code in the same
test run, so they cannot show that a document written by the *current,
pre-conversion* code is still readable afterwards. Capture a frozen BSON
fixture from the current code's actual output for each hand-serialized type
(`AnchorAuthorityFailure`, `AnchorLeaf`, `TimestampConfirmationFailure`) and
one `AnchorEvidenceStore`-encoded evidence blob, commit them under
`tests/Chaos.Mongo.EventStore.Tests/Integration/Fixtures/`, and add a test per
fixture that reads it with the converted `AnchorStore`/`AnchorEvidenceStore`
code and asserts the decoded values match what was captured.

**`CompletenessFinding` per-`Kind` documentation.** Document, on the type or
each property, which of `AggregateId`, `FirstVersion`, `LastVersion`,
`HeadVersion`, `Age`, `Anchor`, and `AnchorChain` are populated for each
`CompletenessFindingKind`: `AnchorChainBroken` populates only `AnchorChain`;
`Unanchored` populates `AggregateId`/`FirstVersion`/`LastVersion`/`HeadVersion`/`Age`;
`StreamDeleted` and `StreamTruncated` populate `AggregateId`/`FirstVersion`/`LastVersion`/`HeadVersion`/`Anchor`;
`RetroactivelyProtected` populates `AggregateId`/`FirstVersion`/`LastVersion`/`HeadVersion`/`Anchor`;
`Unsealed` populates `AggregateId`/`FirstVersion`/`LastVersion`/`HeadVersion`;
`MalformedEvent` populates whichever of `AggregateId`/`FirstVersion`/`LastVersion`
could be read, with `HeadVersion` always unset. This matches
`CompletenessChecker.cs`'s existing construction sites, which this plan does
not change.

**Metrics.** Add `public static class IntegrityMetrics` (in `Integrity/`)
with `public const String MeterName = "Chaos.Mongo.EventStore.Integrity";`, a
nested `public static class Instruments` holding the five existing instrument
name constants moved from `IntegrityAnchoringDiagnostics`
(`AnchorDelayInstrument`, `AnchorDelayExceededInstrument`,
`RevocationConfirmationDelayInstrument`, `RevocationConfirmationDelayExceededInstrument`,
`RevokedTimestampsInstrument`), and a nested `public static class Tags` with
`public const String Authority = "anchoring.authority";`, mirroring
`Chaos.Mongo.Queues.MongoQueueMetrics`'s `MeterName`/`Instruments`/`Tags`
shape. `IntegrityAnchoringDiagnostics` stays `internal`, drops its own
constants in favor of `IntegrityMetrics.MeterName`/`Instruments.*`
(mirroring how `MongoQueueDiagnostics` references `MongoQueueMetrics`), and
changes `RecordRevokedTimestamp()` to `RecordRevokedTimestamp(String authority)`,
tagging the counter with `IntegrityMetrics.Tags.Authority`. Its one call site,
`TimestampRevocationConfirmer.cs`, already has `authority.Name` in scope.
`RecordAnchorDelay`/`RecordRevocationConfirmationDelay` report a pipeline-wide
oldest-candidate delay that is not tied to a single authority at their call
sites in `IntegrityAnchoringService.cs`, so they are not tagged. Tests that
reach into `IntegrityAnchoringDiagnostics`'s constants directly
(`AnchoringMetricsCollector.cs`, `IntegrityAnchoringIntegrationTests.cs`,
`TimestampRevocationConfirmationTests.cs`) switch to the new `IntegrityMetrics`
constants.

**`IntegritySealMode` persistence documentation.** Add a `<remarks>` section
to the enum's XML doc stating it is persisted as a string via the
`EnumSerializer<IntegritySealMode>(BsonType.String)` registered on
`EventIntegrity.SealMode`'s member map in `MongoEventStoreSerializationSetup.cs`,
so the member names `Append`/`Retroactive` are the storage contract and must
not be renamed; note that `EventIntegrityChain.EncodeSealMode`'s fixed
`0x01`/`0x02` byte encoding for the hash chain is a separate, independently
fixed concern and does not change.

**Extra-element tolerance.** Unlike `EventIntegrity`, `IntegritySealingState`,
`PersonalDataCommitment`, and `PersonalDataErasureEntry` (which already call
`cm.SetIgnoreExtraElements(true)`), the read-model (`Aggregate`/direct-`TAggregate`),
`Event<TAggregate>`, `CheckpointId`, and `CheckpointDocument<TAggregate>` class
maps in `MongoEventStoreSerializationSetup.cs` do not. The existing tests for
those four already-tolerant types (`IntegrityRegistrationTests.cs`,
`PersonalDataRegistrationTests.cs`) only assert the `IgnoreExtraElements` flag
or a serialized document's field names, never an actual round-trip
deserialize of a document carrying an extra element, so they do not yet
satisfy the issue's "still read" criterion either; add that missing
deserialize-with-extra-element test for each of the four, alongside the new
tests for the read-model/event/checkpoint class maps below, instead of
treating the flag assertions as equivalent coverage. A scoped scratch probe
against the pinned `MongoDB.Bson` 3.9.0 package confirms `BsonClassMap`'s
default is to throw `FormatException` on an unrecognized element, not to
ignore it. Add `cm.SetIgnoreExtraElements(true)` to the `Aggregate` class map,
the direct-`TAggregate` class map (the branch used when `TAggregate` does not
extend `Aggregate`), the `Event<TAggregate>` base class map, the `CheckpointId`
class map, and the `CheckpointDocument<TAggregate>` class map; add it once,
inside `BuildClassMapHierarchy`, right after `classMap.AutoMap()`, so every
aggregate and event subclass built through that shared helper inherits the
same tolerance without a per-call-site change.

**Public API records and approval snapshot.** `src/Chaos.Mongo.EventStore/PublicAPI.Shipped.txt`
already lists every symbol this plan moves to `internal`, renames, or
reshapes; add the corresponding `*REMOVED*`/added entries to
`PublicAPI.Unshipped.txt`, generated from the `Microsoft.CodeAnalysis.PublicApiAnalyzers`
diagnostics rather than typed by hand, following the convention established
in `ai-plans/0212-trim-the-outbox-public-api.md`. Regenerate
`tests/Chaos.Mongo.EventStore.Tests/Integrity/PublicIntegritySurface.approved.txt`
by running its one test with `UPDATE_PUBLIC_SURFACE=1` and review the diff —
every converted record loses its `ctor(...)`/`Deconstruct(...)` lines and
gains `required` property prefixes, every renamed/moved member's line
changes, and `IntegrityProtectionBuilder<TAggregate>`'s block moves from its
own `.Append`-only entry into the alphabetically sorted `Integrity`-namespace
listing.

**Documentation.** `docs/event-store.md` replaces every `GetEventStream`
mention (the interface code block, three `await foreach` examples, and
several prose references) with `GetEventStreamAsync`, and removes the
"Do not pre-register checkpoint class maps" bullet under "Enabling
Protection", since `CheckpointDocument<TAggregate>` becoming `internal` makes
registering a conflicting class map from outside the assembly impossible, not
merely inadvisable.
