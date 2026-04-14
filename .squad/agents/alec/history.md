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
- MongoDB 8.0+ bulk-write gating assessment (2026-04-11): Evaluated auto-enable vs explicit opt-in for multi-collection `BulkWriteAsync` optimization in `MongoEventStore.AppendEventsAsync`. Recommendation: **explicit opt-in** due to detection reliability risks, topology variance, debuggability, rollout control, and fallback complexity. Detailed assessment in `.squad/decisions/inbox/alec-bulk-write-gating.md`.
- Multi-collection bulk-write feasibility analysis (2026-04-14): Inspected EventStore, Outbox, and Queue write patterns. Finding: **EventStore is suitable** (3–4 collections per transaction: events, read model, checkpoint, user callback); **Outbox is not** (single-collection per-message pattern); **Queue is not** (independent per-queue collections). EventStore bulk-write optimization can consolidate 4 round-trips into 1 via MongoDB 8.0+ BulkWriteAsync, yielding 10–30ms latency gain and 20–40% throughput improvement on bulk workloads. Analysis and 6 task work items captured in `.squad/decisions.md`. Orchestration log: `.squad/orchestration-log/2026-04-14T19:06:03Z-alec.md`.
- MongoDB 8 single-collection bulk-write reassessment (2026-04-14): Christian Flessa clarified that MongoDB 8 bulk writes can still be beneficial for same-collection workloads if multiple operations can be batched. Re-assessed Outbox candidacy against this clarification. Finding: **Outbox remains out of scope**. Rationale: Outbox publish loop is fundamentally per-message serialized with external publisher failures; bulk batching would reduce failure isolation without matching current design intent. Detailed decision in `.squad/decisions.md` (merged from inbox). Orchestration log: `.squad/orchestration-log/2026-04-14T19:15:36Z-alec.md`.
