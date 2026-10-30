# Configure the integrity collection names with a prefix

> Issue: [#214](https://github.com/chA0s-Chris/Chaos.Mongo/issues/214)

## Rationale

Anchoring's five module-level collections (`IntegrityAnchors`, `IntegrityAnchorLeaves`, `IntegrityAnchorEvidence`,
`IntegrityAnchorEvidenceChunks`, `IntegrityChangeStreamState`) have names that live on the internal
`IntegrityAnchoringOptions` (`src/Chaos.Mongo.EventStore/Integrity/IntegrityAnchoringOptions.cs`). Today, only
`IntegrityVerificationBuilder.WithAnchorCollections` can rename four of them, and only when it does so correctly,
`IntegrityAnchoringBuilder` has no way to set any of them, and `IntegrityChangeStreamState` cannot be renamed at all.
Custom names therefore only work in a container that exclusively verifies, which defeats the purpose for the common
case of a container that both anchors and verifies. The event store's own per-aggregate collections already solve the
same problem with a prefix (`MongoEventStoreBuilder<TAggregate>.WithCollectionPrefix`,
`src/Chaos.Mongo.EventStore/MongoEventStoreBuilder.cs:70-75`); this plan applies the same model to the anchoring
collections. Separately, `IAnchorLeafReader` is registered only by `WithIntegrityAnchoring`
(`src/Chaos.Mongo.EventStore/MongoBuilderExtensions.cs:168`), so a verification-only container cannot resolve it even
though nothing about the interface requires anchoring to be configured.

## Acceptance Criteria

- [x] `IntegrityAnchoringBuilder.WithCollectionPrefix(String prefix)` sets a module-level collection-name prefix; the
      five collection names become `{prefix}Anchors`, `{prefix}AnchorLeaves`, `{prefix}AnchorEvidence`,
      `{prefix}AnchorEvidenceChunks` and `{prefix}ChangeStreamState`. A null, empty or whitespace-only prefix throws
      `ArgumentException`.
- [x] `IntegrityVerificationBuilder.WithCollectionPrefix(String prefix)` offers the same configuration for a
      verification-only container, with the same validation. `WithAnchorCollections` is removed.
- [x] The default prefix is `Integrity`, so the five collection names are unchanged from today without any
      configuration, and existing deployments need no migration.
- [x] In one container that configures both `WithIntegrityAnchoring` and `WithIntegrityVerification`, registration
      fails with `InvalidOperationException` when their configured prefixes differ, exactly as mismatched names fail
      today. This holds regardless of which of the two is configured first. A test covers both orders.
- [x] Two containers configured with different prefixes against the same database maintain independent anchor chains
      with no overlapping collections, and an automated test covers this.
- [x] A container that configures only `WithIntegrityVerification` can resolve `IAnchorLeafReader` from its service
      provider, and a test covers this.
- [x] `docs/event-store.md` reflects the prefix-based naming: the "Collections Created" list, the "Evidence
      collections" bullet, the `WithAnchorCollections` reference in the verification-only example's prose, and the
      "Anchoring" row of the MongoDB roles table.
- [x] The Release build has no warnings and the full test suite passes.

## Technical Details

### `IntegrityAnchoringOptions`

Replace the five independently settable name properties with a single `CollectionPrefix` property (default
`"Integrity"`) and derive the names as computed, read-only properties, mirroring
`MongoEventStoreOptions<TAggregate>`'s `EventsCollectionName => $"{CollectionPrefix}{EventsCollectionSuffix}"`
(`src/Chaos.Mongo.EventStore/MongoEventStoreOptions.cs:58`):

```csharp
internal sealed class IntegrityAnchoringOptions
{
    public String CollectionPrefix { get; set; } = "Integrity";
    public String AnchorsCollectionName => $"{CollectionPrefix}Anchors";
    public String AnchorLeavesCollectionName => $"{CollectionPrefix}AnchorLeaves";
    public String AnchorEvidenceCollectionName => $"{CollectionPrefix}AnchorEvidence";
    public String AnchorEvidenceChunksCollectionName => $"{CollectionPrefix}AnchorEvidenceChunks";
    public String ChangeStreamStateCollectionName => $"{CollectionPrefix}ChangeStreamState";
    // CandidateReadBatchSize, DelayThreshold, Interval, MaxLeavesPerAnchor, MinimumTimestampTokens,
    // RevocationConfirmationDelayThreshold, RevocationGracePeriod stay unchanged.
}
```

The class stays `internal`; nothing downstream (`AnchorStore`, `AnchorEvidenceStore`,
`ChangeStreamAnchorCandidateSource`, `IntegrityAnchoringConfigurator`) needs to change, since all of them read the
name properties lazily through the shared options instance rather than caching strings.

### `IntegrityAnchoringBuilder.WithCollectionPrefix`

Add, next to the builder's other `With*` methods:

```csharp
public IntegrityAnchoringBuilder WithCollectionPrefix(String prefix)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
    Options.CollectionPrefix = prefix;
    return this;
}
```

### `IntegrityVerificationBuilder`

Replace the `String[]? _anchorCollections` field with `String? _prefix`. `WithAnchorCollections` is removed and
replaced by:

```csharp
public IntegrityVerificationBuilder WithCollectionPrefix(String prefix)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
    _prefix = prefix;
    return this;
}
```

The three internal orchestration methods keep their roles but operate on the single prefix instead of the four-name
array:

- `ApplyAnchorCollections()` → sets `_anchoring.CollectionPrefix = _prefix` when `_prefix` is not null (verification
  configured without anchoring).
- `EnsureAnchorCollectionsMatch()` → throws `InvalidOperationException` (same message, reworded for a prefix) when
  `_prefix` is not null and differs (ordinal comparison) from `_anchoring.CollectionPrefix`.
- `RestoreDefaultAnchorCollections()` → resets `_anchoring.CollectionPrefix` to `new IntegrityAnchoringOptions().CollectionPrefix`
  when `_prefix` is not null, before an anchoring configuration runs.

`MongoBuilderExtensions.WithIntegrityAnchoring` and `WithIntegrityVerification` keep calling these three methods in
the same order and under the same conditions as today (`MongoBuilderExtensions.cs:147,151,231,235`); only the
methods' internal comparison changes from a 4-element array to a single string, so the existing order-independence
behavior (whichever of anchoring/verification is configured first, the other's prefix must still match) is preserved
without changing the call sites' structure.

### `IAnchorLeafReader` for verification-only containers

Extract the registration at `MongoBuilderExtensions.cs:168`
(`builder.Services.AddSingleton<IAnchorLeafReader>(sp => new AnchorLeafReader(sp.GetRequiredService<IMongoHelper>(), options));`)
into a small private idempotent helper, following the existing `AddAnchorVerifier` pattern
(`MongoBuilderExtensions.cs:270-281`, which checks for an existing registration before adding one), and call it from
both `WithIntegrityAnchoring` and `WithIntegrityVerification`. `AnchorLeafReader` only needs `IMongoHelper` and the
shared `IntegrityAnchoringOptions`, neither of which requires an anchoring run, so no other behavior changes.

### Public API

Both builders are `public sealed class` (`AGENTS.md`: "treat every new public API as a compatibility commitment"),
so removing `WithAnchorCollections` is a breaking change, consistent with existing project precedent for breaking
changes (e.g. the `refactor(event-store)!: trim EventStore public API and reshape integrity types` commit). Update,
for `Chaos.Mongo.EventStore`:

- `PublicAPI.Unshipped.txt`: add a `*REMOVED*` line for `IntegrityVerificationBuilder.WithAnchorCollections(...)`
  (since it is still in `PublicAPI.Shipped.txt`) and add lines for the two new `WithCollectionPrefix` overloads, on
  `IntegrityAnchoringBuilder` and `IntegrityVerificationBuilder`. `dotnet build` reports the exact required line text
  through RS0016/RS0017 if it is omitted.
- `tests/Chaos.Mongo.EventStore.Tests/Integrity/PublicIntegritySurface.approved.txt`: regenerate by running the test
  project with `UPDATE_PUBLIC_SURFACE=1` and reviewing the diff, which is expected to drop the `WithAnchorCollections`
  line and add one `WithCollectionPrefix` line under each of the two builder sections.

### Tests

- `IntegrityAnchoringRegistrationTests.cs` and `IntegrityVerificationRegistrationTests.cs`: replace the
  `WithAnchorCollections`-based cases (matching names accepted in both registration orders, mismatched names throw,
  invalid names throw, verification-only names fill the shared options) with `WithCollectionPrefix` equivalents using
  a single prefix string instead of a four-name array.
- A new test resolving `IAnchorLeafReader` from a container configured only with `WithIntegrityVerification`.
- An integration test (alongside `IntegrityAnchoringIntegrationTests.cs`, using the existing `MongoDbContainer`
  fixture pattern) configuring two containers against the same database with two different prefixes, appending and
  anchoring through both, and asserting each container's anchor collections and chain only contain its own data.

### Documentation

In `docs/event-store.md`:

- "Collections Created" (~line 496-498): replace the five literal names with the `{Prefix}`-based form already used
  for the per-aggregate collections in the same section, and mention `WithCollectionPrefix` as the way to change them.
- The "Evidence collections" bullet (~line 947): replace the three literal collection names it mentions with their
  `{Prefix}`-based equivalents.
- The verification-only example's prose (~line 996): replace "it uses anchor collections with other names,
  `WithAnchorCollections` names them" with the `WithCollectionPrefix` equivalent, keeping the existing statement that
  the names must match the anchoring configuration in a container that also anchors.
- The "Anchoring" row of the MongoDB roles table (~line 1022): replace the three literal collection names with their
  `{Prefix}`-based equivalents, consistent with the `{Prefix}_IntegrityState` notation already used in the same
  table.
