# Make default collection names stable and collision-free

## Rationale

See [issue #244](https://github.com/chA0s-Chris/Chaos.Mongo/issues/244). Default collection names freeze at 1.0,
and today they have three failure modes:

- `CollectionTypeMap.GetCollectionName` (`src/Chaos.Mongo/CollectionTypeMap.cs:41`) falls back to `Type.Name` for
  an unmapped type. For a closed generic type, `Type.Name` is the backtick-arity form (e.g. `` Envelope`1 ``) with no
  type arguments, so `Envelope<A>` and `Envelope<B>` resolve to the same collection name.
- `MongoQueueCollectionNameGenerator.GenerateQueueCollectionName` (`src/Chaos.Mongo/Queues/MongoQueueCollectionNameGenerator.cs:25-27`)
  hashes `Type.FullName`. For a closed generic type, `.NET`'s `Type.FullName` embeds the assembly-qualified name
  (including version) of every type argument, so bumping a referenced package that defines a generic payload's type
  argument changes the hash and silently moves the queue to a new, empty collection.
- `MongoBuilderExtensions.WithEventStore<TAggregate>` (`src/Chaos.Mongo.EventStore/MongoBuilderExtensions.cs:23`)
  derives every collection name for an aggregate from `MongoEventStoreOptions<TAggregate>.CollectionPrefix`, which
  defaults to `typeof(TAggregate).Name` (`src/Chaos.Mongo.EventStore/MongoEventStoreOptions.cs:48`). Two different
  aggregate types with the same simple name (e.g. `Billing.Order` and `Shipping.Order`) silently share collections,
  with no error, and today's collision check only runs when integrity protection is enabled
  (`src/Chaos.Mongo.EventStore/Integrity/ProtectedStreamRegistry.cs:38-44`, invoked only from
  `MongoBuilderExtensions.cs:74`).

This plan fixes the generic-type naming defect in both places that derive a name from `Type.Name`/`Type.FullName`,
and extends the existing startup collision-detection idiom so every registered event-store aggregate is checked, not
only integrity-protected ones. It does not change the default-naming algorithm for non-generic types: the
`Billing.Order`/`Shipping.Order` case is addressed by detection, not by a new naming scheme, to keep the already
breaking change as narrow as the issue's acceptance criteria require.

## Acceptance Criteria

- [x] `CollectionTypeMap.GetCollectionName` returns a name for an unmapped closed generic type that includes the
      type arguments and contains no assembly-qualification. A test pins the exact literal string for two different
      closed generics of the same generic type definition (e.g. a test-only `Envelope<T>` instantiated with two
      different type arguments) and asserts the two literals differ.
- [x] `MongoQueueCollectionNameGenerator.GenerateQueueCollectionName` produces a name for a closed generic payload
      type that does not depend on the assembly version of any type argument, including an array-typed argument
      whose element type is itself a closed generic (e.g. `Envelope<List<Payload>[]>`). A test pins the exact
      literal name for a closed generic payload type. A second, version-invariance regression test emits two
      in-memory assemblies via `System.Reflection.Emit` that each declare a type with the same namespace and name
      but a different assembly version, closes the same generic payload definition over the type loaded from each
      assembly (including the array-wrapped case above), and asserts `GenerateQueueCollectionName` returns an
      identical name for both — a test that fails against today's `FullName`-hashing implementation. Collection
      names for non-generic payload types are unchanged.
- [x] `WithEventStore<TAggregate>` throws `InvalidOperationException` — naming both aggregate types and the shared
      collection name — when two aggregate types registered on the same `MongoBuilder` resolve to the same
      read-model, events, checkpoint, or (when enabled) integrity-state collection name. This applies regardless of
      whether either aggregate has integrity protection enabled. Tests cover a colliding pair where neither aggregate
      has integrity protection enabled, and confirm non-colliding aggregates still register successfully.
- [x] `tests/Chaos.Mongo.EventStore.Tests/Integrity/AnchorCandidateRegistrationTests.cs:32-42`
      (`AddMongo_ProtectedTypesWithSameEventsCollection_Throws`) still passes, updated if its expected message text
      changes because the general check now reports the collision before `ProtectedStreamRegistry` is reached.
- [x] `docs/compatibility-policy.md`'s "Stored data" section states: a closed generic type's default collection name
      is derived from its type arguments and never from assembly identity, and two event-store aggregates whose
      default collection names collide fail registration instead of silently sharing data.
- [x] `docs/migrating-from-0.x.md` gains a section describing the generic-payload queue collection rename (old
      assembly-version-dependent name to the new stable name) and the manual migration step for an existing queue
      with a generic payload type, plus a section describing the generic default-collection-name fix and the new
      startup collision check for event-store aggregates, consistent with the existing "Event Store: integrity
      collection naming" section's style.
- [x] `docs/compatibility-policy.md` and/or `docs/migrating-from-0.x.md` document that renaming an aggregate class
      changes its default collection names (because they derive from `typeof(TAggregate).Name`), and show how to
      keep the prior names after a rename via `WithEventStore<TAggregate>(...).WithCollectionPrefix(string)`. The
      same section points to `MongoOptions.CollectionTypeMap` as the equivalent explicit pin for the general
      type-to-collection map, and to `MongoQueueBuilder<TPayload>.WithCollectionName(string)` as the equivalent pin
      for queues.
- [x] The Release build (`dotnet build -c Release Chaos.Mongo.slnx`) produces no warnings, and the full test suite
      passes.

## Technical Details

### Stable generic type names

Add one internal helper shared by `Chaos.Mongo.CollectionTypeMap` and `Chaos.Mongo.Queues.MongoQueueCollectionNameGenerator`
(same assembly) that formats a `Type` recursively:

- For a closed generic type, it strips the backtick-arity suffix from the generic type definition's base name and
  appends `<...>` with the comma-joined, recursively formatted names of its type arguments.
- For a closed generic type nested in a generic type, it formats the outermost generic declaring type with the
  caller-supplied base name and each nested level with its simple name, joined with `+`, attaching each type
  argument to the level that declares it (e.g. `Outer<A>+Inner<B>`). `GetGenericArguments()` also returns the
  arguments a nested type inherits from its declaring types, so stripping the arity markers alone would format
  `Outer<A>.Inner` and a non-nested `Inner<A>` identically (raised in PR review).
- For an array type (any rank, including jagged arrays), it recurses into the element type and reconstructs the
  array's rank/jagged suffix around the recursively formatted element name, instead of falling back to the array
  type's own base name. This case is required because `Type.FullName` for an array always embeds the
  assembly-qualified name of a generic element type (confirmed by execution probe during plan review: the element
  type of `List<Payload>[]` reports `IsGenericType == false` but its `FullName` still contains `Version=...`), so a
  closed generic payload with an array-typed argument (e.g. `Envelope<List<Payload>[]>`) would otherwise still hash
  a version-qualified string.
- For every other type shape (plain non-generic, non-array types), it returns the caller-supplied base name
  unchanged.

Illustrative shape (omitting the recursive body):

```csharp
internal static String FormatGenericAwareName(Type type, Func<Type, String> baseName);
```

- `CollectionTypeMap.GetCollectionName` calls this with `baseName = t => t.Name` only when the type is an unmapped
  closed generic type (`type.IsGenericType && !type.IsGenericTypeDefinition`); the existing `type.Name` fallback for
  non-generic types is unchanged, so no existing non-generic default collection name changes.
- `MongoQueueCollectionNameGenerator.GenerateQueueCollectionName` calls this with `baseName = t => t.FullName ?? t.Name`
  only for a closed generic payload type, and hashes the result instead of `payloadType.FullName`. A non-generic
  payload type keeps hashing `payloadType.FullName` directly, so its generated name is unchanged — only generic
  payload types move to a new collection name, which `docs/migrating-from-0.x.md` must describe per the acceptance
  criteria.
- Open generic types and type parameters are not a concern here: both call sites only ever receive closed,
  concrete runtime types.

### Startup collision detection for event-store aggregates

Add a new internal singleton registry (parallel to the existing `ProtectedStreamRegistry` /
`GetOrAddSingleton<T>` idiom at `src/Chaos.Mongo.EventStore/MongoBuilderExtensions.cs:305-315`), tracking every
aggregate type registered via `WithEventStore<TAggregate>` on a given `MongoBuilder`/`IServiceCollection`, together
with its `ReadModelCollectionName`, `EventsCollectionName`, `CheckpointCollectionName`, and — when
`IntegrityProtectionEnabled` — `IntegrityStateCollectionName`. On each `WithEventStore<TAggregate>` call, check the
new aggregate's names against every previously registered aggregate's names and throw `InvalidOperationException`
naming both aggregate types and the shared collection name on any overlap.

Call the read-only collision *check* unconditionally near the top of `WithEventStore<TAggregate>`
(`MongoBuilderExtensions.cs:23`), alongside the existing synchronous `InvalidOperationException` checks at lines
30-33 and 41-52, so it applies to every aggregate, not only integrity-protected ones, and fails fast before class
map registration. The registry *commits* the new aggregate's claimed names only after every later check for this
call has already succeeded — immediately before `builder.Services.AddSingleton(options)` at
`MongoBuilderExtensions.cs:59`, i.e. after `MongoEventStoreSerializationSetup.RegisterClassMaps(options)` (line 56)
has returned without throwing. This split between checking and committing preserves the existing all-or-nothing
corrected-retry contract (`tests/Chaos.Mongo.EventStore.Tests/AllOrNothingRegistrationTests.cs`): a call that passes
the collision check but then fails a later check, such as a reserved discriminator rejected inside
`RegisterClassMaps`, leaves no claim behind, so a corrected retry for the same aggregate type (with the same or a
changed `CollectionPrefix`) is not spuriously rejected as colliding with its own abandoned attempt.

Because this new check already covers the `EventsCollectionName` overlap that
`ProtectedStreamRegistry.Add` (`src/Chaos.Mongo.EventStore/Integrity/ProtectedStreamRegistry.cs:38-44`) currently
detects only for integrity-protected pairs, remove that now-redundant branch from `ProtectedStreamRegistry.Add` and
keep only its `AggregateTypeName` check (lines 29-35), which protects a different invariant — the name anchor
candidates and leaves use to identify a stream — unrelated to collection names. `AddMongo_ProtectedTypesWithSameName_Throws`
(`AnchorCandidateRegistrationTests.cs:44-55`) is unaffected; the general check now fires earlier for
`AddMongo_ProtectedTypesWithSameEventsCollection_Throws`, so its expected message substring at line 41 likely needs
updating to match the new check's wording.

`ICollectionTypeMap`'s `GetCollectionName(Type)` mechanism (used by `MongoHelper.GetCollection<T>()`) has no
equivalent closed registration set at startup — any CLR type can be passed at any time — so it is out of scope for
startup collision detection; only the explicit `MongoOptions.CollectionTypeMap` entries and the event-store
aggregate registration set are enumerable ahead of time, and this plan only adds detection for the latter, which is
what the issue's `Billing.Order`/`Shipping.Order` example describes.

### Documentation

Extend the existing "Default collection names ... are contract" bullet in `docs/compatibility-policy.md`'s
"Stored data" section (around line 24-26) with the generic-naming rule and the aggregate-collision-throws rule, and
with the rename/pinning rule below. Add two new `##` sections to `docs/migrating-from-0.x.md`, following the
existing "Event Store: integrity collection naming" section's style (state old vs. new behavior, what is unaffected
by default, and the manual step required before upgrading): one for the queue collection-name change for generic
payloads, one for the `CollectionTypeMap` generic-name fix and the new aggregate collision check.

Also document, in whichever of the two files best fits each case, that a class rename changes its default
collection name because defaults derive from the CLR type name, and how to pin the previous name explicitly:
`WithEventStore<TAggregate>(...).WithCollectionPrefix(string)` for an aggregate
(`src/Chaos.Mongo.EventStore/MongoEventStoreBuilder.cs:70`), `MongoOptions.CollectionTypeMap` for the general
type-to-collection map (`src/Chaos.Mongo/MongoOptions.cs:38`), and `MongoQueueBuilder<TPayload>.WithCollectionName(string)`
for a queue (`src/Chaos.Mongo/Queues/MongoQueueBuilder.cs:78`). This is the rename/pinning guidance the issue
requests in addition to the generic-naming and collision fixes above.
