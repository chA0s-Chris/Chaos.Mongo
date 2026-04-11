---
name: "mongodb-index-query-contract-tests"
description: "Protect MongoDB query/index alignment with rendered BSON, live index inspection, and behavior checks instead of explain-plan parsing"
domain: "testing"
confidence: "high"
source: "earned"
tools:
  - name: "view"
    description: "Inspect index creation code and the query methods that should stay aligned with it"
    when: "Before writing MongoDB contract tests"
---

## Context
This applies when Chaos.Mongo features depend on stable MongoDB index/query alignment but explain-plan parsing would be too brittle. Use it for outbox polling, queue dequeue or recovery, event-stream reads, checkpoint replay, and similar query contracts.

## Patterns
- Write a **unit/query-contract** test that captures or reflects the actual `FilterDefinition` or `SortDefinition`, renders it to BSON, and asserts on the fields and operators that matter.
- Write an **integration/index-contract** test that runs configurators against Testcontainers MongoDB and inspects `Indexes.ListAsync()` for key order, uniqueness, TTL options, and partial filters.
- Add one **behavior-critical integration** assertion when the real contract is observable behavior like processing order or uniqueness enforcement.
- Prefer recursive BSON assertions that check required clauses instead of exact whole-document equality when safe refactors may change boolean composition shape.

## Examples
- Outbox polling query plus claim contract: `tests/Chaos.Mongo.Outbox.Tests/OutboxProcessorQueryContractTests.cs`
- Outbox index and ordering contract: `tests/Chaos.Mongo.Outbox.Tests/Integration/OutboxIndexContractIntegrationTests.cs`
- Event store query contract: `tests/Chaos.Mongo.EventStore.Tests/MongoEventStoreQueryContractTests.cs`
- Event store unique index contract: `tests/Chaos.Mongo.EventStore.Tests/Integration/EventStoreIndexContractIntegrationTests.cs`
- Queue lease-recovery filter contract: `tests/Chaos.Mongo.Tests/Queues/MongoQueueSubscriptionTests.cs`

## Anti-Patterns
- Parsing `explain()` plans or planner stage names in regression tests.
- Mocking only the index manager and never validating the rendered BSON or real MongoDB index document.
- Locking tests to an entire rendered BSON tree when only a few fields or operators are the durable contract.
