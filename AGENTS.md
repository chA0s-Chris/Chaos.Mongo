# Chaos.Mongo - Agent Guidelines

This document provides guidelines for AI agents working on the Chaos.Mongo codebase.

## General Information

`Chaos.Mongo` is a .NET library for working with MongoDB providing additional features like database migrations, distributed locking, message queues, and more. 

Additional information can be found in `README.md`.

## Behavioral Guidelines

Trade-off: These guidelines bias toward caution over speed. For trivial tasks, use judgment.

### 1. Think Before Coding

**Don't assume. Don't hide confusion. Surface trade-offs.**

Before implementing:

- State your assumptions explicitly. If uncertain, ask.
- If multiple interpretations exist, present them - don't pick silently.
- If a simpler approach exists, say so. Push back when warranted.
- If something is unclear, stop. Name what's confusing. Ask.

### 2. Simplicity First

**Minimum code that solves the problem. Nothing speculative.**

- No features beyond what was asked.
- No abstractions for single-use code.
- No "flexibility" or "configurability" that wasn't requested.
- No error handling for impossible scenarios.
- If you write 200 lines and it could be 50, rewrite it.

Ask yourself: "Would a senior engineer say this is overcomplicated?" If yes, simplify.

### 3. Surgical Changes

**Touch only what you must. Clean up only your own mess.**

When editing existing code:

- Don't "improve" adjacent code, comments, or formatting.
- Don't refactor things that aren't broken.
- Match existing style, even if you'd do it differently.
- If you notice unrelated dead code, mention it - don't delete it.

When your changes create orphans:

- Remove imports/variables/functions that YOUR changes made unused.
- Don't remove pre-existing dead code unless asked.

The test: Every changed line should trace directly to the user's request.

### 4. Goal-Driven Execution

**Define success criteria. Loop until verified.**

Transform tasks into verifiable goals:

- "Add validation" → "Write tests for invalid inputs, then make them pass"
- "Fix the bug" → "Write a test that reproduces it, then make it pass"
- "Refactor X" → "Ensure tests pass before and after"

For multi-step tasks, state a brief plan:

```
1. [Step] → verify: [check]
2. [Step] → verify: [check]
3. [Step] → verify: [check]
```

Strong success criteria let you loop independently. Weak criteria ("make it work") require constant clarification.

### 5. Contribution Hygiene

- Use Conventional Commits: `{type}(scope): {subject}`.
- Update the relevant README or feature documentation whenever public behavior or configuration changes.
- For work tied to a GitHub issue, use a dedicated issue branch and leave changes uncommitted for user review unless explicitly instructed otherwise.

## Architectural Knowledge

### Central Package Version Management

NuGet package versions are managed centrally in `/Directory.Packages.props`. Project `.csproj` files reference packages without versions; versions must be added to `Directory.Packages.props`.

### Project Layout

- Source projects live in `src/`, test projects in `tests/`.
- `src/Directory.Build.props` contains shared NuGet packaging metadata (PackageId, authors, license, etc.).
- `tests/Directory.Build.props` pulls in common test dependencies (NUnit, FluentAssertions, Moq, Testcontainers, etc.).
- The solution file is `Chaos.Mongo.slnx` (XML-based slnx format).

### DI Registration Pattern

`ServiceCollectionExtensions.AddMongo()` returns a `MongoBuilder` which exposes fluent `With*()` methods for registering configurators, migrations, queues, etc. New features (like EventStore) should follow this same pattern — provide extension methods on `MongoBuilder`.

### Collection Access

`IMongoHelper.GetCollection<T>()` resolves collection names via `ICollectionTypeMap`. For features that manage their own collection naming (like EventStore with per-aggregate collections), use `IMongoHelper.Database.GetCollection<T>(name)` directly.

### Transaction Support

`MongoHelperExtensions.ExecuteInTransaction()` wraps `session.WithTransactionAsync()` with automatic retry on transient errors. `TryStartTransactionAsync()` is available for optional transaction use when the server may not support them.

### Queue Processing Invariants

MongoDB queues provide at-least-once delivery. Payload handlers must therefore tolerate duplicate execution.

Queue availability is defined by all of the following:

- The item is not closed.
- The item is not terminal. Documents created before `IsTerminal` existed must continue to be treated as non-terminal.
- The item is unlocked, or its lock has a missing or expired `LockedUtc`.

Lock acquisition is atomic. Every subsequent failure, terminal, close, or delete mutation must verify ownership using the item ID and the exact `LockedUtc` written by that consumer. A stale consumer must never complete or unlock an item reacquired by another consumer.

Lease recovery is passive: expired locks become eligible through the availability query. The processing loop must also wake periodically, bounded by the lease duration, because lock expiry does not produce a MongoDB change-stream event.

`RetryCount` records failed processing attempts and is not reset after later success. `WithMaxRetries(N)` permits N retries after the initial attempt, for N + 1 total attempts. A null value means unlimited retries; `WithNoRetry()` makes the first failure terminal.

Terminal items remain in the original queue collection as `IsClosed = true` and `IsTerminal = true`. They form the logical dead-letter view and must be excluded from both active processing and successful-item TTL cleanup. TTL partial filters must continue matching legacy documents where `IsTerminal` is missing, using the MongoDB-supported false-or-null form.

Queue success metrics represent persisted completion transitions, not successful handler invocations. Record success only after the ownership-guarded close or delete succeeds.

`MongoQueueMetrics` names are public compatibility contracts. Queue diagnostics must use its meter, instrument, and tag constants rather than duplicating string literals.

### MongoDB Query and Index Contract Tests

Avoid tests that assert MongoDB `explain()` plans or specific planner stages. Planner choices vary with MongoDB version, topology, statistics, and collection size.

Protect query/index alignment with three complementary layers:

1. Render the production filter and sort definitions to BSON and assert only the durable fields, operators, and branch relationships.
2. Run configurators against Testcontainers MongoDB and inspect `Indexes.ListAsync()` for key order, uniqueness, TTL configuration, and partial filters.
3. Add a small behavioral integration test when ordering, uniqueness, retention, or recovery is the real contract.

When production uses `Find(...).Sort(...).Limit(...)`, exercise that real fluent path through a capturing `IMongoCollection<T>` proxy and inspect the final `FindOptions`. Do not mock the `Find()` extension method directly.

EventStore query-contract tests must initialize the production GUID serializer and class maps before rendering BSON. Tests that start an Outbox background processor must stop it in `finally` so timeouts cannot leak work into later tests.

### Shared MongoDB Testcontainers

Each integration-test assembly has a `MongoAssemblySetup` fixture that owns its shared `MongoDbTestContainer`.

Individual fixtures may call `StartContainerAsync()` to obtain the shared running container, but they must never stop or dispose it. Container shutdown belongs exclusively to the assembly-level setup fixture; violating this ownership causes nondeterministic failures when tests run in parallel.

### BenchmarkDotNet MongoDB Benchmarks

Repository performance work should use BenchmarkDotNet against the real production implementation rather than copied or synthetic helper logic.

MongoDB benchmarks should:

- Start one MongoDB 8 replica-set Testcontainer per benchmark job.
- Perform container startup, dependency-injection setup, index creation, and capability detection outside the measured operation.
- Warm paths with one-time capability detection before measurement.
- Use explicit valid scenario objects through `ParamsSource` rather than independent parameters that create invalid combinations.
- Use unique aggregate IDs or isolated data so duplicate keys and aggregate growth do not skew results.
- Keep the generated benchmark project single-targeted through `benchmarks/Directory.Build.props`.
- Exclude unrelated transactional callback work when comparing EventStore baseline and bulk-write paths.
