# Write the 1.0 Upgrade Guide, Compatibility Policy and Requirements

## Rationale

[#218](https://github.com/chA0s-Chris/Chaos.Mongo/issues/218): 1.0 makes promises about compatibility, supported
platforms and the upgrade path from 0.x that are not written down anywhere. Breaking-change notes are scattered
through the reference text of `docs/event-store.md`, interrupting its API reference and, in one place, splitting a
bullet list. MongoDB server requirements (the replica-set requirement and the specific server versions individual
features need) are likewise spread across several docs instead of being stated once. Readers upgrading from 0.9 have
no single page that tells them what changed and what to do about it, and no page states the compatibility
guarantees 1.x makes going forward.

## Acceptance Criteria

- [ ] `docs/migrating-from-0.x.md` exists and covers the upgrade path from v0.9.0 to 1.0, including: every
      inline breaking-change note currently in `docs/event-store.md` (see Technical Details for the exact list),
      and every `breaking`-labeled pull request merged since the v0.9.0 release that is not already covered by
      those notes (at least #208 and #210 through #214).
- [ ] `docs/event-store.md` contains no inline "Breaking change" or "Breaking batch behavior" notes. The reference
      text they interrupted reads as continuous prose/lists afterward (in particular, the `AppendEventsAsync`/
      `GetEventStreamAsync` bullet list under [Event Store](docs/event-store.md) is contiguous).
- [ ] `docs/compatibility-policy.md` exists and states the policy from the issue: SemVer for public API source/binary
      compatibility; the stored-data guarantees (cross-1.x readability, same-feature-set rolling upgrades, and which
      names/values are contract); tamper-evidence format versioning; the platform support policy for dropping .NET
      and MongoDB server versions and raising dependency minimums; and what the policy does not cover.
- [ ] `docs/requirements.md` exists and states: the supported .NET version(s); the minimum MongoDB server version and
      the replica-set/sharded-cluster requirement, plus the higher versions specific features need; the minimum
      `MongoDB.Driver` version; and the MongoDB server lines the project tests against.
- [ ] `README.md` and all three package READMEs (`src/Chaos.Mongo/README.md`, `src/Chaos.Mongo.EventStore/README.md`,
      `src/Chaos.Mongo.Outbox/README.md`) link to all three new pages, following each file's existing documentation
      link convention.
- [ ] Every link and heading anchor added or changed by this work resolves (relative paths in `README.md`'s
      documentation table, absolute `blob/main` GitHub URLs in the package READMEs, and in-page anchors).

This is a documentation-only change: no production code, build configuration or test changes are expected, so no
test-coverage criterion applies.

## Technical Details

### Scope confirmation

No other open issue changes `docs/event-store.md`'s breaking-change notes or the MongoDB version requirements it
documents; #208 and #210–#214 (this issue's stated dependencies) are already merged into `main`. `.NET 11` support
(#209) is not: per the issue, it is added later and is not breaking, so `docs/requirements.md` states .NET 10 only
and makes no claim about .NET 11.

### Inline notes to relocate from `docs/event-store.md`

Move these blocks to `docs/migrating-from-0.x.md` and delete them from `docs/event-store.md`, leaving the
surrounding reference text intact and continuous:

1. The `IAggregate.Revision` property addition (currently right after the `IAggregate` interface listing).
2. The "Breaking batch behavior" note on per-event `Version`/`Revision` updates during a batch append (currently in
   [Stream Position and Revision](docs/event-store.md)).
3. The `AppendEventsAsync` parameter-order note (currently splits the `GetExpectedNextVersionAsync`/
   `AppendEventsAsync`/`GetEventStreamAsync` bullet list under [Event Store](docs/event-store.md) — removing it must
   restore that list to three contiguous bullets).
4. The `IAggregateRepository.GetAtRevisionAsync` addition (currently after the `IAggregateRepository` interface
   listing).
5. The discriminator-collision `InvalidOperationException` note together with the "Earlier versions" paragraph
   immediately above it (currently in [Class Hierarchies](docs/event-store.md)) — move both as one unit; they
   describe the same historical behavior change (silently dropped inherited members) and its consequence, and
   splitting them would orphan the context.

Each note already states the concrete consequence and any required caller action; carry that content into the
migration page rather than re-deriving it. Where a note is removed, an implementer may leave a short pointer to the
migration page if it helps readers, but this is not required.

### Breaking PRs since v0.9.0

No `v0.9.0` git tag exists; the v0.9.0 section of `CHANGELOG.md` was cut at the merge of #139 (commit `7d538ee`).
Enumerate pull requests merged into `main` after that commit carrying the `breaking` GitHub label (the same label
`.github/release-drafter.yml` uses for its "💥 Breaking Changes" category) and cross-check against commits in that
range using the Conventional Commits `!` marker. Commits already visible in this range include (not exhaustive,
and labels must be confirmed rather than inferred from the marker alone): stale-event-version conflict reporting,
lock-lease extension, stream position/revision separation, revision-based concurrency and reconstruction,
`IServiceProvider`-aware `MongoOptions` configuration, the public-API trims in #210–#212, the outbox/queue option
renames in #213, the integrity collection prefix in #214, and the net10.0-only/dependency-minimum change in #208.
Reconcile this list against the full merged-PR history before writing the page, per the issue's own caveat that it
"needs checking against the full changelog" — do not treat the list above as final.

### `docs/compatibility-policy.md`

Transcribe the issue's proposal section 2 (public API SemVer; stored-data guarantees a/b/c; tamper-evidence format
versioning; platform support policy; what's not covered) as prose/headings matching this project's documentation
style. The content is specified by the issue; no additional design decisions are needed here.

### `docs/requirements.md`

Ground every number in the current repository state rather than restating this plan's findings as fixed facts,
since they can drift before implementation:

- **.NET**: current target from `Directory.Build.props`' `LatestTarget` (net10.0 as of this plan).
- **MongoDB server baseline**: the replica-set/sharded-cluster requirement (stated today in `docs/transactions.md`
  and `docs/event-store.md`'s anchoring operating requirements) applies to every feature built on transactions
  (Event Store, Outbox, the `Transactions` helpers). State the minimum tested server line from
  `tests/MongoDbTestImages.cs`'s `MinimumSupported` constant (7.0.x as of this plan) as the baseline.
- **Feature-specific floors**: bulk-write optimization needs MongoDB 8.0+ (enforced in code; see
  `WithBulkWriteOptimization` in `docs/event-store.md`); tamper-evidence anchoring's change-stream recovery scan
  needs MongoDB 5.1+; `VerifyDerivedDataAsync`'s snapshot session needs MongoDB 5.0+. The latter two are already
  satisfied by the stated baseline — document them anyway, as the issue asks for the full set of version floors,
  and note that they are automatically satisfied by the baseline rather than independently binding.
- **Minimum `MongoDB.Driver`**: read the current minimum and its justification from the `src/` minimums comment
  block in `Directory.Packages.props` (3.9.0 as of this plan, from #208).
- **Tested server versions**: reference `tests/MongoDbTestImages.cs`'s three named constants by minor version line
  (currently 7.0.x/8.0.x/9.0.x) rather than hardcoding exact patch numbers, since Renovate updates the patches
  independently of this page.

### README and package README links

`README.md`'s existing "Documentation" table takes three new rows using the same relative-path style as its other
entries. Each package README's "Documentation" list takes three new entries using that file's existing absolute
`https://github.com/chA0s-Chris/Chaos.Mongo/blob/main/docs/...` style — the package READMEs already link this way
because they are rendered on NuGet, where relative links do not resolve.
