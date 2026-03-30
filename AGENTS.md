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
