# Eliot — History

## Core Context

- **Project:** A .NET MongoDB library providing migrations, event store, transactional outbox, queues, and helpers published as multiple NuGet packages
- **Role:** Library Dev
- **Joined:** 2026-04-09T19:19:45.616Z

**Queue Architecture Summary:**
- `MongoQueueItem`: State machine with `IsClosed`, `IsLocked`, `LockedUtc`, `ClosedUtc`, `RetryCount`, `IsTerminal` fields
- Processing: Dual-mechanism (change stream + polling loop with backoff)
- Phase 1 (Complete): Lock recovery (#9 — passive lease expiry), Retention (#10 — TTL-based cleanup)
- Phase 2 (Active): Retry counting, dead-letter handling, observability
- Index strategy: Compound index on `(IsClosed, IsLocked, LockedUtc, IsTerminal)` for active items; TTL index on `ClosedUtc` for cleanup

**Key Queue Implementation Files:**
- `MongoQueueSubscription.cs`: Core processing engine (ProcessQueueItemsAsync, ProcessQueueItemAsync, lock recovery, terminal handling)
- `MongoQueueItem.cs`: Schema definition with state fields
- `MongoQueueDefinition.cs`: Configuration holder with LockLeaseTime, ClosedItemRetention, MaxRetries
- `MongoQueueBuilder.cs`: DI registration and fluent configuration
- `MongoQueueDiagnostics.cs`: Metrics instrumentation (7 instruments, 4 tag names)
- Tests: Unit tests in `MongoQueueTests.cs`, integration tests in subdirectories (LockExpiryIntegrationTests, RetentionIntegrationTests, SubscriptionTests)

**Configuration Defaults (MongoDefaults.cs):**
- `QueueLockLeaseTime` = 5 minutes (for passive lock recovery)
- `QueueClosedItemRetention` = 1 hour (for TTL cleanup)
- `QueueMaxRetries` = null (unlimited, backward compatible)
- `AutoStartSubscription` = false (opt-in)
- `QueryLimit` = 1 (single-item-at-a-time processing)

**API Pattern:**
- Fluent builder: `MongoBuilder.WithQueue<T>()` returns `MongoQueueBuilder<T>` with chainable `.With*()` methods
- New features follow builder pattern; all new fields optional with sensible defaults; backward compatible
- Phase 2 observability: Structured ILogger messages + System.Diagnostics.Metrics instruments (no new DI surface)

**Phase 1 Implementation (2026-04-10 to 2026-04-11):**
- Passive lease expiry: `IsLocked=true && LockedUtc < now - LockLeaseTime` treated as available
- TTL-based retention: Single managed index with optional immediate delete (retention=null)
- Direct-construction defaults: `ClosedItemRetention` property initializer ensures builder/direct paths align
- Lock ownership guard: Closing guarded by `LockedUtc` match to prevent cache-clearing race
- Timed wake-up: Polling loop rechecks queue periodically to catch expired locks without new publishes

**Phase 2 Implementation (2026-04-11):**
- Retry counting: `RetryCount` field on queue items (not reset on later success)
- Terminal state: `IsTerminal` field set when `RetryCount >= MaxRetries` (if configured)
- Configuration: `MaxRetries` property on `MongoQueueDefinition`, `WithMaxRetries()` / `WithNoRetry()` builder methods
- Observability: 7 metrics (published, processing success/failed/duration, queue age, lock recovered/recovery age) + structured logging
- Dead-letter logic: Terminal items are logical view (closed+terminal), accessible via queries, operators decide disposition

**Learnings & Patterns:**
- Queue availability filtering in `CreateAvailableQueueItemFilter()` treats missing `IsTerminal` as non-terminal for backward compatibility
- TTL index partial filters use `IsTerminal: { $in: [false, null] }` instead of `$ne: true` (MongoDB compatibility)
- EventStore query-contract tests must explicitly bootstrap `MongoEventStoreSerializationSetup` before BSON rendering
- Outbox processor tests must stop background processor in `finally` to prevent leak into subsequent tests
- Terminal items excluded from TTL cleanup to allow DLQ handlers to inspect before expiry

## Recent Work

### 2026-04-11 (Late): Queue Metrics Public API Recommendation

**Date:** 2026-04-11T21:35:17Z  
**Context:** Issue #72 observability merged; Phase 2 planning  
**Status:** Decision staged for team review

**Work:** Documented recommendation to expose public `MongoQueueMetrics` constants surface for OpenTelemetry integration.

**Rationale:** Current private literals + README docs create refactoring fragility. Public constants (meter name, 7 instrument names, 4 tag names) follow .NET framework practice and eliminate copy-paste friction.

**API Shape:** Static class with ~15 `const string` fields in nested Instruments and Tags classes. Minimal, stable, semver-safe surface.

**Decision:** Merged into `decisions.md` under "Active Decisions" with full specification. Tara's merge-gate review appended with approval pending API shape confirmation.

**Ownership:** Implementation deferred to Phase 2. Requires: (1) API finalization (flat vs. nested), (2) XML docs, (3) `MongoQueueDiagnostics` constant references, (4) README links.

**Status:** Ready for team consensus and Tara's final review.

---

## Historical Summary

**2026-04-10 to 2026-04-11 (Early):** Completed Phase 1 queue implementation:
- PR #73: Passive lease expiry recovery with lock ownership guard and timed wake-up
- PR #74: TTL-based retention with optional immediate delete and direct-construction default fix
- PR #77: EventStore serialization bootstrap, Outbox processor cleanup guarantee, Queue lease-recovery contract assertion fixes
- Full test suite: 427 tests passing, no regressions
- All Phase 1 PRs staged for user review and merge approval

