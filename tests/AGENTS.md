# AGENTS.md for Tests

## General Rules

- Prefer hand-crafted test doubles for new tests. Do not introduce new mocking-framework usage. Existing Moq-based tests may remain unless migrating them is part of the task.
- Do not write nested test classes. All tests should reside in a class which is directly placed in a namespace.
- Name test methods using `Method_Scenario_ExpectedResult`, with PascalCase for each segment (e.g., `Parse_InvalidInput_Throws`).
- Use FluentAssertions.
- When writing Unit Tests (i.e., tests that only run in-memory and make no I/O calls to third-party systems), prefer Sociable Tests instead of Solitary Tests (according to Martin Fowler's definition). Create as much test coverage as possible by calling higher level production APIs. Only write Solitary Tests to cover otherwise unreachable lower level APIs – for example, Guard Clauses.
- During Integration Tests, at least one I/O call to third-party systems like a database or Web API is made. Some of the third-party system calls can be replaced with Test Doubles or Fakes (according to XUnit Test Patterns by Gerard Meszaros).
- In End-to-End (E2E) Tests, I/O calls must not be replaced with Test Doubles or Fakes.
- Maintain at least 95% merged line coverage. Measure coverage through the repository's existing Coverlet/XPlat Code Coverage pipeline.
