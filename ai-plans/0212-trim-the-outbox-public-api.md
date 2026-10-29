# Trim the Outbox public API

## Rationale

[Issue #212](https://github.com/chA0s-Chris/Chaos.Mongo/issues/212) applies the
public-API visibility rule already used elsewhere in the solution (`Chaos.Mongo`
and `Chaos.Mongo.EventStore` both ship `InternalsVisibleTo` plus internal
implementation types behind public contracts) to `Chaos.Mongo.Outbox`. The
project currently exposes all 17 of its types publicly, including construction
details (`MongoOutbox`, `OutboxProcessor`, `OutboxHostedService`,
`OutboxConfigurator`, `OutboxConfiguratorRunner`, `OutboxSerializationSetup`),
a configuration snapshot that is only ever produced by `OutboxBuilder`
(`OutboxOptions`), and mutation members on `OutboxMessage` that only the
processor itself should ever set. None of this is part of the intended public
contract: consumers configure the outbox through `MongoBuilderExtensions` and
`OutboxBuilder`'s fluent methods, consume it through `IOutbox`/`IOutbox<T>`,
`IOutboxProcessor`/`IOutboxProcessor<T>`, `IOutboxPublisher`, and
`IOutboxConfiguratorRunner`, and only ever read `OutboxMessage`. Tightening
visibility to that surface reduces the compatibility commitment of the package
without changing any runtime behavior.

## Acceptance Criteria

- [x] `MongoOutbox`, `OutboxConfigurator`, the `OutboxConfiguratorRunner`
      implementation class, `OutboxHostedService`, `OutboxProcessor`,
      `OutboxSerializationSetup`, and `OutboxOptions` are `internal`.
- [x] `OutboxBuilder` remains public, but its constructor, `Build()`,
      `Validate()`, `PublisherFactory`, `PublisherLifetime`, and
      `PublisherType` are `internal`.
- [x] `OutboxMessage` is `sealed`; all of its property setters/init accessors
      are `internal` while every getter stays `public`.
- [x] `IOutbox`, `IOutbox<T>`, `IOutboxProcessor`, `IOutboxProcessor<T>`,
      `IOutboxPublisher`, `MongoBuilderExtensions`, `OutboxMessageState`, and
      `IOutboxConfiguratorRunner` remain unchanged and public.
- [x] `src/Chaos.Mongo.Outbox/PublicAPI.Unshipped.txt` records a `*REMOVED*`
      entry for every shipped type, member, and accessor this plan moves to
      `internal`, and the `Microsoft.CodeAnalysis.PublicApiAnalyzers` checks
      (RS0016/RS0017) report no errors.
- [x] `Chaos.Mongo.Outbox.csproj` grants `InternalsVisibleTo` to
      `Chaos.Mongo.Outbox.Tests`, matching the pattern used by
      `Chaos.Mongo.csproj` and `Chaos.Mongo.EventStore.csproj`.
- [x] A test demonstrates that an outbox document containing an unrecognized
      extra BSON element still deserializes successfully.
- [x] `docs/transactional-outbox.md` no longer tells readers to configure the
      processing filter (or anything else) through an `OutboxOptions`
      initializer, and no longer lists `OutboxOptions` as something a
      consumer interacts with directly.
- [x] `OutboxMessageState`'s XML documentation states that it is persisted
      using the driver's default `Int32` representation and that the numeric
      values are a stable storage contract.
- [x] Every default previously documented only on `OutboxOptions` members
      (batch size, collection name, lock timeout, max retries, polling
      interval, retry backoff) is documented on the corresponding
      `OutboxBuilder.With*` method.
- [x] The Release build (`dotnet build -c Release Chaos.Mongo.slnx`) produces
      no warnings, and `bash ./build.sh Test` passes the complete suite with
      at least 95% merged line coverage, per `tests/AGENTS.md`.

## Technical Details

**Visibility mechanics.** The MongoDB C# driver's `BsonClassMap.AutoMap()`
maps a property as soon as it has a get accessor; the accessibility of the
setter/init accessor is irrelevant to member discovery, and the driver
invokes setters through compiled expression trees that bypass C# accessibility
checks at runtime, so no `InternalsVisibleTo` grant to the driver assembly is
needed for `OutboxMessage`'s setters to become `internal`. This was confirmed
with a scoped scratch probe against the pinned `MongoDB.Driver`/`MongoDB.Bson`
package version from `Directory.Packages.props`: a POCO with `internal set`
and `internal init` properties round-tripped through `AutoMap()` unchanged.
`OutboxSerializationSetup` already calls `cm.SetIgnoreExtraElements(true)` for
`OutboxMessage`, so unknown-element tolerance is existing behavior that only
needs a regression test, not a code change.

**`OutboxBuilder`.** Add an explicit `internal OutboxBuilder()` constructor
(today's implicit public parameterless constructor must go). Change the
parameterless `Build()`, `Validate()`, and the `PublisherFactory` /
`PublisherLifetime` / `PublisherType` properties to `internal`; the
`internal OutboxOptions Build(String identity)` overload and the fluent
`With*` methods are already correctly accessible and need no change. Only
`MongoBuilderExtensions.Register` and `CreatePublisherDescriptor` call these
members, and both live in the same assembly.

**`OutboxOptions`.** No production code outside `Chaos.Mongo.Outbox` and its
test project references `OutboxOptions`, so it becomes `internal sealed
class OutboxOptions` with its members left exactly as declared (member-level
`public` modifiers become inert under the internal type and do not need to be
individually changed). Move each `Default*` constant's documented value into
the XML docs of the matching `OutboxBuilder.With*` method; `WithRetryBackoff`
and `WithAutoStartProcessor` currently omit their defaults and need that text
added, the other `With*` methods already state theirs.

**`OutboxConfiguratorRunner`.** The interface `IOutboxConfiguratorRunner` and
its implementation class both live in `OutboxConfiguratorRunner.cs`; only the
class declaration changes to `internal sealed class OutboxConfiguratorRunner`.
Leave its constructors' own modifiers as-is.

**`OutboxMessage`.** Keep every property's getter `public`; change `get; set;`
to `get; internal set;` and `get; init;` to `get; internal init;` across all
members (`CorrelationId`, `CreatedUtc`, `Error`, `FailedUtc`, `Id`, `IsLocked`,
`LockId`, `LockedUtc`, `NextAttemptUtc`, `Payload`, `ProcessedUtc`,
`RetryCount`, `State`, `Type`), and add `sealed` to the class declaration.
Publishers (`IOutboxPublisher.PublishAsync`) and processing filters
(`FilterDefinition<OutboxMessage>` built via `Builders<OutboxMessage>`) only
ever read these members through expression trees or direct property access,
never write them, so no public write access is lost from the supported usage
surface.

**Public API records.** `src/Directory.Build.props` raises RS0016 (undeclared
public API) and RS0017 (declared API no longer found) to build errors in
every configuration via `Microsoft.CodeAnalysis.PublicApiAnalyzers`.
`src/Chaos.Mongo.Outbox/PublicAPI.Shipped.txt` already lists every type,
constructor, method, property accessor, and constant this plan moves to
`internal` (`MongoOutbox`, `OutboxConfigurator`, `OutboxConfiguratorRunner`,
`OutboxHostedService`, `OutboxProcessor`, `OutboxSerializationSetup`,
`OutboxOptions` and its members, and `OutboxBuilder`'s constructor,
`Build()`, `Validate()`, `PublisherFactory`, `PublisherLifetime`, and
`PublisherType`, plus every `OutboxMessage` setter/init accessor). Add a
matching `*REMOVED*` entry in `PublicAPI.Unshipped.txt` for each, following
the convention `ai-plans/0215-guard-public-api-signing-package-validation.md`
established for trim PRs; generate the entries from the analyzer's own
RS0016 diagnostic text rather than typing them by hand. Leave the shipped
baseline and the entries for members that stay public untouched.

**Verification.** `tests/AGENTS.md` requires at least 95% merged line
coverage, measured through the repository's Coverlet/XPlat Code Coverage
pipeline. `bash ./build.sh Test` is the documented entry point that both
runs the complete suite and collects that merged coverage; per the
execution contract, do not also run `dotnet test Chaos.Mongo.slnx` for the
same purpose. Remove `artifacts/test-coverage` first so the merge reflects
only this change's run.

**Documentation.** `docs/transactional-outbox.md` currently (a) lists
`OutboxOptions` as one of the services `WithOutbox` registers, and (b) tells
readers they can "Configure the filter at startup through
`WithProcessingFilter` or an `OutboxOptions` initializer." Remove the
`OutboxOptions` bullet from the registration list (it is now an internal
implementation detail, not something a consumer resolves) and rewrite the
filter paragraph to reference only `WithProcessingFilter`.

**`OutboxMessageState`.** Add a `<remarks>` section to the enum's XML doc
noting it is persisted using the driver's default representation for enums
(`Int32`) and that the numeric values (`Pending = 0`, `Processed = 1`,
`Failed = 2`) are part of the storage contract and must not be renumbered.

**Test coverage.** Add a unit test (likely alongside the existing
`OutboxSerializationSetupTests`) that registers the class map, deserializes a
`BsonDocument` for an `OutboxMessage` containing an extra, unmapped element,
and asserts it succeeds and the known fields are populated correctly. No
other new tests are required: the existing `Chaos.Mongo.Outbox.Tests` suite
already exercises every type whose class-level accessibility changes by
constructing it directly, and gains the needed access once
`InternalsVisibleTo` is added, so those tests continue to compile and pass
unchanged.
