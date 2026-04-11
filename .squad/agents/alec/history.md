# Alec — History

## Core Context

- **Project:** A .NET MongoDB library providing migrations, event store, transactional outbox, queues, and helpers published as multiple NuGet packages
- **Role:** Feature Dev
- **Joined:** 2026-04-09T19:19:45.617Z

## Learnings

<!-- Append learnings below -->
- Queue subscriptions must exclude `IsTerminal=true` items both from the available-item filter and from the closed-item TTL partial filter so dead-letter queries remain possible; touchpoints: `src/Chaos.Mongo/Queues/MongoQueueSubscription.cs`, `tests/Chaos.Mongo.Tests/Queues/MongoQueueSubscriptionTests.cs`, and `tests/Chaos.Mongo.Tests/Integration/Queues/MongoQueueRetentionIntegrationTests.cs`.
- Queue review-fix validation succeeded with targeted queue tests in `tests/Chaos.Mongo.Tests/Chaos.Mongo.Tests.csproj` and the full `bash build.sh Test` pipeline.
- Outbox and EventStore query-contract tests should exercise the real fluent `Find(...).Sort(...).Limit(...)` path by capturing the final `FindOptions` from a proxy `IMongoCollection`, then render the captured filter/sort BSON; touchpoints: `tests/Chaos.Mongo.Outbox.Tests/OutboxProcessorQueryContractTests.cs` and `tests/Chaos.Mongo.EventStore.Tests/MongoEventStoreQueryContractTests.cs`.
- Outbox polling-order integration timeouts should wrap cancellation-driven polling in `TimeoutException` to keep failures deterministic; touchpoint: `tests/Chaos.Mongo.Outbox.Tests/Integration/OutboxIndexContractIntegrationTests.cs`.
- PR #77 revision pass (2026-04-11): Completed all Copilot review notes—rewired query-contract tests to capture real fluent pipeline, hardened outbox `WaitUntilAsync` timeout, tests passing. Recorded fluent query-contract capture and index contract test strategy decisions in squad/decisions.md.
