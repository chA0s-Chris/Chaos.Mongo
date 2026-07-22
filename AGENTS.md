# Root AGENTS.md

`Chaos.Mongo` is a .NET library for working with MongoDB providing additional features like database migrations, distributed locking, message queues, and more.

## Implementation rules

When implementing an active plan, mark each acceptance criterion as complete only after verifying it. Do not modify historical plans during unrelated work.

## General Rules for the Code Base

- `<TreatWarningsAsErrors>` is enabled in Release builds, so your code changes must not generate warnings.
- Expose types and members publicly when consumers need them for configuration, extension, or testing. Keep implementation details non-public, and treat every new public API as a compatibility commitment.
- Target frameworks are defined in `Directory.Build.props` and directory-specific props files. Do not change them unless the task explicitly requires it.
- NuGet package versions are managed centrally in `Directory.Packages.props`; project files reference packages without versions.

### Code Style

For the project's code style, refer to `CODESTYLE.md`.

## Production Code Rules

Read ./src/AGENTS.md for details about the production code.

## Testing Rules

Read ./tests/AGENTS.md for details about how to write tests.

## Plan Rules

Read ./ai-plans/AGENTS.md for details on how to write plans.

## Benchmark Rules

Read ./benchmarks/AGENTS.md for details about how to write benchmarks.

## Here is Your Space

If you encounter something worth noting while working on this code base, report it in your final response instead of editing this file. I will discuss it with you, and we can decide where to put the note.
