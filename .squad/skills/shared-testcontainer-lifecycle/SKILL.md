---
name: "shared-testcontainer-lifecycle"
description: "Use the assembly-owned MongoDB Testcontainer lifecycle in integration tests without per-fixture disposal"
domain: "testing"
confidence: "high"
source: "earned"
tools:
  - name: "view"
    description: "Inspect setup fixtures and existing integration test patterns"
    when: "Before changing MongoDB integration tests in the test projects"
---

## Context
This applies when adding or fixing MongoDB integration tests in the Chaos.Mongo test projects. The test assemblies already provide a shared `MongoDbTestContainer` lifecycle, so fixture-level cleanup must not fight the assembly-level owner.

## Patterns
- Check `MongoAssemblySetup.cs` first to see whether the test assembly owns container startup and shutdown.
- In fixture `[OneTimeSetUp]`, call `MongoDbTestContainer.StartContainerAsync()` only to obtain the shared running container reference.
- Keep per-test cleanup focused on queues, service providers, and collections created by the fixture.
- Leave container disposal to the assembly-level setup fixture (`MongoAssemblySetup`).

## Examples
- Shared lifecycle owner: `tests/Chaos.Mongo.Tests/Integration/MongoAssemblySetup.cs`
- Correct fixture usage: `tests/Chaos.Mongo.Tests/Integration/MongoHelperIntegrationTests.cs`
- Fixed misuse: `tests/Chaos.Mongo.Tests/Integration/Queues/MongoQueueLockExpiryIntegrationTests.cs`

## Anti-Patterns
- Adding `[OneTimeTearDown]` to dispose `_container` in a single integration test fixture.
- Stopping the shared container from a fixture while other tests in the assembly may still need it.
- Treating `MongoDbTestContainer.StartContainerAsync()` as creating a private container per fixture when it returns a shared singleton.
