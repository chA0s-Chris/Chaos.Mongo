# Trim the public API of Chaos.Mongo

## Rationale

[Issue #210](https://github.com/chA0s-Chris/Chaos.Mongo/issues/210) applies the
1.0 compatibility rule already used for `Chaos.Mongo.EventStore` and (in the
sibling issue [#212](https://github.com/chA0s-Chris/Chaos.Mongo/issues/212))
`Chaos.Mongo.Outbox` to the core `Chaos.Mongo` project: public types are a
SemVer contract and must be sealed; everything a consumer does not need to
configure, extend, or construct directly becomes `internal`. `Chaos.Mongo`
currently exposes every implementation class behind its public interfaces
(`CollectionTypeMap`, `MongoClientSettingsFactory`, `MongoConnectionFactory`,
`MongoHelper`, the configurator/migration/hosted-service runners, the lock and
queue implementation classes, `MongoOptionsValidation`, `ReflectionHelper`),
none of which the docs or `README.md` ever construct or derive from directly.
Tightening visibility to the documented contract (interfaces, the fluent
builders, options, results, persisted data types, and the documented
extension points `IMongoClientSettingsFactory`/`IMongoConnectionFactory`/
`ICollectionTypeMap`) reduces the package's compatibility commitment without
changing runtime behavior. The issue also folds in three small, related
cleanups: renaming `ExecuteInTransaction` to `ExecuteInTransactionAsync` for
naming consistency, adding a `CancellationToken` to the now-internal
`CreateAndRunAsync`, and changing `MongoDefaults`'s tunable defaults from
`const` to `static` get-only properties so a future default change does not
require consumers to recompile.

## Acceptance Criteria

- [x] `CollectionTypeMap`, `MongoClientSettingsFactory`, `MongoConnectionFactory`,
      `MongoHelper` (already `sealed`), `MongoConfiguratorRunner`,
      `MongoMigrationRunner`, `MongoHostedService`, `MongoLock`,
      `MongoLockDocument`, `MongoOptionsValidation` (already `sealed`),
      `ReflectionHelper`, `Queues.MongoQueue<TPayload>`,
      `Queues.MongoQueuePublisher`, `Queues.IMongoQueuePublisher`,
      `Queues.MongoQueueSubscription<TPayload>`,
      `Queues.MongoQueueSubscriptionFactory`,
      `Queues.IMongoQueueSubscriptionFactory`,
      `Queues.MongoQueuePayloadHandlerFactory`, and
      `Queues.IMongoQueuePayloadHandlerFactory` are `internal`.
- [x] `MongoBuilder`'s constructor and its `DiscoveredConfigurators` and
      `DiscoveredMigrations` properties (getter and setter) are `internal`;
      the class itself stays `public`.
- [x] `Queues.MongoQueueBuilder<TPayload>`'s constructor and `RegisterQueue()`
      are `internal`.
- [x] `MongoBuilder`, `MongoConnection`, `Queues.MongoQueuePayloadPrioritizer`,
      `Queues.MongoQueueCollectionNameGenerator`, and
      `Migrations.MongoMigrationHistoryItem` are `sealed`; `MongoConnection`
      keeps its public `(IMongoClient, IMongoDatabase)` constructor as contract.
- [x] `Queues.MongoQueueDefinition` is a `sealed record`.
- [x] `Queues.MongoQueueItem` (the non-generic base) gets an `internal`
      constructor; `Queues.MongoQueueItem<TPayload>` is `sealed`.
- [x] `MongoOptions` is a `sealed class` (not a `sealed record`); its public
      members and `ConfigureClientSettings` are otherwise unchanged.
- [x] On `MongoLockDocument` and `Queues.MongoQueueItem`, every getter stays
      `public` and every mutable-state setter becomes `internal`: `Holder`,
      `Id`, `LeaseUntilUtc` on `MongoLockDocument`; `ClosedUtc`, `IsClosed`,
      `IsLocked`, `IsTerminal`, `LockedUtc`, `RetryCount` on `MongoQueueItem`.
      `Migrations.MongoMigrationHistoryItem` has no mutable-state setters to
      change (its members are already `required init`); only its class
      declaration gains `sealed`, per the criterion above. All three
      persisted types keep ignoring unknown BSON elements.
- [x] A regression test for each of `MongoLockDocument`,
      `Migrations.MongoMigrationHistoryItem`, and
      `Queues.MongoQueueItem<TPayload>` deserializes a BSON fixture matching
      that type's released 0.x shape (field names/types as shipped before this
      change, without the new accessor restrictions) and asserts the restored
      identifiers, timestamps, payload, and non-default mutable-state values
      match the fixture, including any field the 0.x shape omits taking its
      expected default.
- [x] `MongoHelperExtensions.ExecuteInTransaction<TResult>` and
      `MongoHelperExtensions.ExecuteInTransaction` are renamed to
      `ExecuteInTransactionAsync`, with every in-repo caller (including
      `Chaos.Mongo.EventStore`, its tests and benchmarks, and `docs/`) updated
      to the new name.
- [x] `IMongoQueueSubscriptionFactory.CreateAndRunAsync` and its
      implementation take a `CancellationToken cancellationToken = default`
      parameter, which is threaded through to the subscription it starts.
- [x] A test demonstrates that a specific, non-default `CancellationToken`
      passed to `MongoQueue<TPayload>.StartSubscriptionAsync` reaches
      `IMongoQueueSubscriptionFactory.CreateAndRunAsync` and, from the real
      `MongoQueueSubscriptionFactory` implementation, reaches the started
      `IMongoQueueSubscription<TPayload>`'s startup/index operations, rather
      than being silently replaced by a default token. An `It.IsAny<CancellationToken>()`
      setup alone does not satisfy this criterion.
- [x] `MongoDefaults.QueryLimit`, `ApplyMigrationsOnStartup`,
      `AutoStartSubscription`, `RunConfiguratorsOnStartup`,
      `UseDefaultCollectionNames`, and `UseTransactionsForMigrationsIfAvailable`
      are `static` get-only properties instead of `const`;
      `LockCollectionName`, `MigrationHistoryCollectionName`, and
      `MigrationsLockName` remain `const`.
- [x] `Chaos.Mongo.csproj` grants `InternalsVisibleTo` to
      `DynamicProxyGenAssembly2`, with that item's own explicit `Key` metadata
      set to Castle's DynamicProxyGenAssembly2 public key (not the repository's
      own public key), because `Chaos.Mongo.Tests` uses Moq to mock
      `IMongoQueuePublisher`, `IMongoQueueSubscriptionFactory`, and
      `IMongoQueuePayloadHandlerFactory`, which are now internal interfaces.
- [x] A test demonstrates that a `MongoLockDocument`, a
      `MongoMigrationHistoryItem`, and a `MongoQueueItem<TPayload>` each
      deserialize successfully from a BSON document containing an extra,
      unrecognized element.
- [x] `src/Chaos.Mongo/PublicAPI.Unshipped.txt` records a `*REMOVED*` entry for
      every shipped type, member, and accessor this plan moves to `internal`
      or removes (including the compiler-synthesized members affected by
      sealing `MongoQueueDefinition` and converting `MongoOptions` from a
      record to a class), and the `Microsoft.CodeAnalysis.PublicApiAnalyzers`
      checks (RS0016/RS0017) report no errors.
- [x] The Release build (`dotnet build -c Release Chaos.Mongo.slnx`) produces
      no warnings, and `bash ./build.sh Test` passes the complete suite with
      at least 95% merged line coverage, per `tests/AGENTS.md`.

## Technical Details

**Visibility mechanics.** The MongoDB driver's `BsonClassMap.AutoMap()` maps a
property from its getter alone and invokes setters through compiled expression
trees that bypass C# accessibility checks, so no `InternalsVisibleTo` grant to
the driver assembly is needed for `internal set`/`internal init` accessors.
This was already confirmed with a scoped scratch probe against the pinned
`MongoDB.Driver`/`MongoDB.Bson` version while planning #212
(`ai-plans/0212-trim-the-outbox-public-api.md`); the same package version
applies here. `MongoLockDocument`, `MongoMigrationHistoryItem`, and
`Queues.MongoQueueItem`/`MongoQueueItem<TPayload>` already carry
`[BsonIgnoreExtraElements]`, so unknown-element tolerance is existing behavior
that only needs regression tests, not a code change.

**`MongoQueueItem` base vs. derived.** `MongoQueueItem` (non-generic) is
excluded from the "every public class is sealed" rule because
`MongoQueueItem<TPayload>` derives from it; give it an explicit
`internal MongoQueueItem()` constructor (replacing today's implicit public
one) so external code can neither construct it nor derive from it, while
`MongoQueueItem<TPayload>` becomes `sealed` and keeps its existing implicit
public parameterless constructor, matching the convention already used for
`MongoLockDocument` and `MongoMigrationHistoryItem` as public, constructible
persisted DTOs.

**`MongoDefaults`.** Only the six tunable defaults become `static` get-only
properties (`public static Boolean ApplyMigrationsOnStartup => false;`, etc.);
the three name constants (`LockCollectionName`, `MigrationHistoryCollectionName`,
`MigrationsLockName`) stay `const` per the compatibility policy in
[#218](https://github.com/chA0s-Chris/Chaos.Mongo/issues/218). The five
already-`static` properties (`LockLeaseTime`, `MigrationLockLeaseTime`,
`QueueClosedItemRetention`, `QueueLockLeaseTime`, `QueueMaxRetries`,
`RetryDelay`) are unaffected.

**`MongoOptions` record → class.** `MongoOptions` has no value-equality or
`with`-expression usage anywhere in the solution
(`tests/Chaos.Mongo.Tests/MongoOptionsTests.cs` only asserts on individual
scalar properties), so converting it to `public sealed class MongoOptions`
with the same members is a pure API-shape change. It drops the
compiler-synthesized `Equals(MongoOptions?)`, `Equals(object?)`,
`GetHashCode()`, `ToString()`, `==`/`!=` operators, and `<Clone>$()`; record
each of these out of the shipped baseline via `*REMOVED*` entries generated
from the `PublicApiAnalyzers` diagnostics rather than typed by hand.

**`MongoQueueDefinition` sealing.** Sealing the record changes the
accessibility of its compiler-synthesized copy constructor (from `protected`
to `private`) and removes the `virtual` modifier from `Clone`,
`PrintMembers`, and `Equals(MongoQueueDefinition?)`. No production or test
code uses a `with`-expression or subclasses `MongoQueueDefinition`, so this
is purely a shape change; let the analyzer's own diagnostics drive the exact
`PublicAPI.Unshipped.txt` entries instead of predicting them.

**`ExecuteInTransactionAsync` rename.** Both overloads
(`src/Chaos.Mongo/MongoHelperExtensions.cs:70,99`) are called outside
`Chaos.Mongo` itself, so the rename touches every caller in the solution:
`src/Chaos.Mongo.EventStore/MongoEventStore.cs`,
`src/Chaos.Mongo.EventStore/Integrity/AnchorCandidateConsumer.cs`,
`src/Chaos.Mongo.EventStore/Integrity/AnchoringRun.cs` (two call sites),
`src/Chaos.Mongo.EventStore/Integrity/EventStreamSealingSweep.cs`,
`benchmarks/Chaos.Mongo.EventStore.LoadTests/LoadTestStore.cs`, the
`Chaos.Mongo.EventStore.Tests` integration tests that call it directly
(`AnchorCandidateSourceTests.cs`, `IntegritySealingIntegrationTests.cs`,
`ChangeStreamAnchorCandidateSourceTests.cs`), the
`Chaos.Mongo.Tests` integration tests in
`MongoHelperExtensionsIntegrationTests.cs`, and the documentation in
`docs/transactions.md` and `docs/transactional-outbox.md`. `src/Chaos.Mongo.Outbox/README.md`
also demonstrates the old name and needs updating. Do not rename the
`ExecuteInTransaction_Should...` test method names unless doing so is a
trivial part of the same edit; the acceptance criterion is about the
production API name, not test naming.

**`CreateAndRunAsync` cancellation.** `MongoQueue<TPayload>.StartSubscriptionAsync`
(`src/Chaos.Mongo/Queues/MongoQueue.cs:64,81`) already receives a
`CancellationToken` that it currently drops when calling
`CreateAndRunAsync<TPayload>(QueueDefinition)`; thread that token through to
the new `CreateAndRunAsync` parameter, and from there into
`MongoQueueSubscriptionFactory`'s internal `subscription.StartAsync()` call
(`src/Chaos.Mongo/Queues/MongoQueueSubscriptionFactory.cs:57`), which already
accepts a token on `IMongoQueueSubscription<TPayload>.StartAsync`. Moq
`Setup`/`Verify` expressions on `IMongoQueueSubscriptionFactory.CreateAndRunAsync`
in `tests/Chaos.Mongo.Tests/Queues/MongoQueueTests.cs` and
`MongoQueueSubscriptionFactoryTests.cs` match arguments positionally, so every
existing `It.IsAny<MongoQueueDefinition>()` setup needs an added
`It.IsAny<CancellationToken>()` argument to keep matching once the production
code passes a real (non-default) token. That signature fix alone only keeps
existing setups matching; it does not demonstrate propagation. Satisfy the new
cancellation-propagation criterion with at least one test per layer: a
`MongoQueueTests.cs` case that passes a specific token to
`StartSubscriptionAsync` and verifies (via Moq `Verify` with the exact token,
not `It.IsAny`) that `CreateAndRunAsync` received it; and a
`MongoQueueSubscriptionFactoryTests.cs` or `MongoQueueSubscriptionTests.cs`
case against the real (non-mocked) factory/subscription that passes a
pre-cancelled token through `CreateAndRunAsync` and observes the resulting
`OperationCanceledException` (or equivalent observable cancellation) from the
real startup/index path, confirming the token is not replaced by `default`.

**Moq and newly-internal interfaces.** `MongoQueueTests.cs`,
`MongoQueueSubscriptionFactoryTests.cs`, and `MongoQueueSubscriptionTests.cs`
mock `IMongoQueuePublisher`, `IMongoQueueSubscriptionFactory`, and
`IMongoQueuePayloadHandlerFactory` with Moq, which generates proxies through
Castle DynamicProxy. Proxying an internal interface requires the declaring
assembly to grant `InternalsVisibleTo("DynamicProxyGenAssembly2")`; without
it, these tests fail at runtime with a `TypeLoadException`, not a compile
error. `ai-plans/0215-guard-public-api-signing-package-validation.md` already
noted this requirement exists in principle and found it unnecessary at the
time ("no test currently mocks an internal type"); this plan is the first to
trigger it for `Chaos.Mongo`, so add the grant to `Chaos.Mongo.csproj`
alongside the existing `InternalsVisibleTo Include="Chaos.Mongo.Tests"`.
`src/Directory.Build.props` sets a global `<PublicKey>` that the SDK appends
to every `InternalsVisibleTo` item lacking its own key metadata, binding the
friend declaration to the repository's own strong-name key
(`PublicKeyToken=258e5594a5dc8a36`). Castle's generated proxy assembly is
itself signed, but with Castle's own key, not the repository's, so a plain
`<InternalsVisibleTo Include="DynamicProxyGenAssembly2" />` item would grant
friend access to an assembly identity Castle's proxy does not have, leaving
the mocks unable to access the internal interfaces. Give that one item an
explicit `Key` (or `PublicKey`) metadata attribute containing Castle's actual
`DynamicProxyGenAssembly2` public key (pinned `Castle.Core` 5.1.1, resolved
transitively through `Moq` 4.20.72), overriding the inherited repository key
for this friend only; leave the existing `Chaos.Mongo.Tests` item and the
global signing configuration unchanged.

**Unaffected by this plan.** `IMongoLock`, `IMongoHelper`, `IMongoConnection`,
`IMongoConfigurator`, `IMongoConfiguratorRunner`, `IMongoMigration`,
`IMongoMigrationRunner`, `IMongoQueue`/`IMongoQueue<TPayload>`,
`IMongoQueuePayloadHandler<TPayload>`, `IMongoQueuePayloadPrioritizer`,
`IMongoQueueCollectionNameGenerator`, `IMongoQueueSubscription<TPayload>`,
`ICollectionTypeMap`, `CollectionTypeMapExtensions`, `IMongoClientSettingsFactory`,
`IMongoConnectionFactory`, `MongoLockExtensions`, `MongoIndexManagerExtensions`,
`MongoQueueMetrics`, and `ServiceCollectionExtensions` keep their current
public shape; `Chaos.Mongo.EventStore` and `Chaos.Mongo.Outbox` (the production
projects) only consume `Chaos.Mongo` through `IMongoLock` and
`IMongoHelper`/its extensions, so none of the internal-visibility changes
above affect their compilation.

**Implementation finding: `MongoBuilder`'s constructor and cross-project tests.**
`Chaos.Mongo.EventStore.Tests` and `Chaos.Mongo.Outbox.Tests` construct
`MongoBuilder` directly (bypassing `AddMongo()`) in ~20 files, to exercise
`WithEventStore`/`WithOutbox` in isolation without `AddMongo()`'s other
registrations (several tests, e.g.
`IntegrityRegistrationTests.WithEventStore_WithoutIntegrityProtection_RegistersNoSealingSweep`,
assert on the exact resulting `IServiceCollection` contents, so switching
those call sites to `services.AddMongo()` would change what they observe,
such as adding an unconditional `MongoHostedService` registration). Making
`MongoBuilder`'s constructor `internal` breaks that compilation. Rather than
rewriting those call sites and their assertions, `Chaos.Mongo.csproj` grants
two additional `InternalsVisibleTo` entries (no explicit `Key`, like the
existing `Chaos.Mongo.Tests` grant) to `Chaos.Mongo.EventStore.Tests` and
`Chaos.Mongo.Outbox.Tests`. This is a friend-assembly declaration only; it
does not change the shipped public API surface.

**Verification.** `tests/AGENTS.md` requires at least 95% merged line
coverage, measured through the repository's Coverlet/XPlat Code Coverage
pipeline; `bash ./build.sh Test` is the documented entry point for both the
full suite and that merged coverage, so do not also run
`dotnet test Chaos.Mongo.slnx` for the same purpose. Remove
`artifacts/test-coverage` first so the merge reflects only this change's run.
