# AGENTS.md for Tests

## General Rules

- Prefer hand-crafted test doubles for new tests. Do not introduce new mocking-framework usage. Existing Moq-based tests may remain unless migrating them is part of the task.
- Do not write nested test classes. All tests should reside in a class which is directly placed in a namespace.
- Name test methods using `Method_Scenario_ExpectedResult`, with PascalCase for each segment (e.g., `Parse_InvalidInput_Throws`).
- Use FluentAssertions.
- When writing Unit Tests (i.e., tests that only run in-memory and make no I/O calls to third-party systems), prefer Sociable Tests instead of Solitary Tests (according to Martin Fowler's definition). Create as much test coverage as possible by calling higher level production APIs. Only write Solitary Tests to cover otherwise unreachable lower level APIs – for example, Guard Clauses.
- During Integration Tests, at least one I/O call to third-party systems like a database or Web API is made. Some of the third-party system calls can be replaced with Test Doubles or Fakes (according to XUnit Test Patterns by Gerard Meszaros).
- Place integration tests in the `Integration` folder and namespace of their test project (for example `Chaos.Mongo.Tests.Integration`), where the shared MongoDB setup fixture applies, and mark every integration test class with `[Category("Integration")]`.
- Name every database an integration test creates through `TestDatabases.NewName` (or `TestDatabases.Register`), so it is dropped after the test; a database left on the shared server fails the run.
- Fixtures of `Chaos.Mongo.EventStore.Tests` and `Chaos.Mongo.Outbox.Tests` run in parallel. Keep fixtures independent of process-wide or server-wide state, and mark a fixture that cannot be isolated `[NonParallelizable]` with a comment naming the shared state.
- In End-to-End (E2E) Tests, I/O calls must not be replaced with Test Doubles or Fakes.
- Maintain at least 95% merged line coverage. Measure coverage through the repository's existing Coverlet/XPlat Code Coverage pipeline.
