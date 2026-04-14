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

### 2026-04-12: Queue Bulk-Write Analysis (Exploratory)

**Date:** 2026-04-12T00:00:00Z  
**Context:** User request to check if MongoDB 8 multi-collection bulk writes could optimize queue layer (following Alec's EventStore work)  
**Status:** Completed; Issue #81 created with finding

**Work:** Inspected queue write patterns across publish/process/complete flows to assess multi-collection bulk-write feasibility.

**Finding:** Queues are NOT a bulk-write optimization target.
- Single-collection-per-operation constraint: Each queue publishes to its own collection; all processing targets the same collection
- No transactional cross-collection writes: Queue operations don't use `ExecuteInTransaction`; design is at-least-once, not ACID
- Single-item processing: Default `QueryLimit=1` processes one item at a time; no batching pattern to optimize

**Contrast with EventStore:** `AppendEventsAsync` writes to 3+ collections (events, readmodel, checkpoint) in a transaction—genuine bulk-write target. Queues operate within a single collection.

**Architectural Insight:** Bulk writes optimize multi-collection operations in a transaction. Queues have neither: single collection, optional transactional scope. Future queue improvements (batch processing, index tuning) are separate concerns.

**Decision:** Do NOT create a separate queue bulk-write issue. EventStore optimization and any future queue improvements are independent. Issue #81 documents the analysis and closes this exploratory thread.

---

### 2026-04-14: MongoDB 8 Bulk-Write Queue Analysis Follow-Up (Coordination Spawn)

**Date:** 2026-04-14T19:06:03Z  
**Context:** Christian Flessa spawned follow-up analysis to confirm Queue exclusion from bulk-write optimization scope (Issue #81 update)  
**Status:** Analysis confirmed; decision merged into squad/decisions.md

**Work:** Re-confirmed queue write patterns against bulk-write optimization criteria:
- **All operations single-collection:** Publish, lock acquisition, failure handling, completion all target one collection
- **No multi-collection transactions:** Queue items don't use `ExecuteInTransaction`; design is at-least-once
- **Single-item processing loop:** `QueryLimit=1` means no batching pattern

**Architectural Crystallization:** Bulk writes benefit multi-collection operations in a transaction (EventStore). Queues benefit from single-collection optimizations (indexing, query strategy, processing loop). Document this pattern in team wisdom for future optimization analysis.

**Decision Documentation:** Merged into `.squad/decisions.md` as "Decision: MongoDB 8 Bulk Writes — Queue is Not an Optimization Target" (2026-04-12, Eliot). Orchestration log: `.squad/orchestration-log/2026-04-14T19:06:03Z-eliot.md`.

---

### 2026-04-14 (Second Pass): MongoDB 8 Single-Collection Bulk-Write Queue Reassessment (Coordination Spawn)

**Date:** 2026-04-14T19:15:36Z  
**Context:** Christian Flessa clarified that MongoDB 8 bulk writes can still benefit same-collection workloads if multi-operation batching is possible. Re-assessed Queue candidacy.  
**Status:** Analysis complete; decision merged into squad/decisions.md

**Work:** Analyzed whether MongoDB 8 single-collection bulk writes change Queue assessment:

1. **Current Hot Paths:** All single-operation per query (QueryLimit=1 default)
   - Publish: Single `InsertOneAsync`
   - Lock Acquisition: Single `FindOneAndUpdateAsync`
   - Failure Handling: Two sequential (not batched) operations
   - Completion: Single `UpdateOneAsync` or `DeleteOneAsync`

2. **No Batching Pattern:** Sequential single-item processing with user callbacks between ops; no multi-op bundling opportunity

3. **No Transactional Boundary:** Queue uses at-least-once design, explicitly avoids `ExecuteInTransaction`

4. **Conditions for Queue to Become Candidate (all false):**
   - Multi-operation batching opportunity (currently none)
   - Transactional scope established (currently none)
   - Proven DB round-trip bottleneck (currently no evidence; callback time likely dominates)

**Finding:** Queue architecture (sequential single-item, no transactional scope) precludes bulk-write benefit regardless of MongoDB capability.

**Proposed Issue #81 Update:** Replace outdated rationale "single-collection writes" with architecture-focused explanation: bulk writes batch multiple operations; Queue's sequential one-item-at-a-time loop provides no multi-operation batches to collapse; user callback runs between operations and cannot be batched; no transactional boundary to make bundling beneficial. Queue optimizations should focus on indexing, query strategy, and processing efficiency—not bulk writes.

**Decision Documentation:** Merged into `.squad/decisions.md` as "Analysis: MongoDB 8 Single-Collection Bulk Writes and Queue Candidacy" (Eliot, 2026-04-14). Orchestration log: `.squad/orchestration-log/2026-04-14T19:15:36Z-eliot.md`.

---

## Historical Summary

**2026-04-10 to 2026-04-11 (Early):** Completed Phase 1 queue implementation:
- PR #73: Passive lease expiry recovery with lock ownership guard and timed wake-up
- PR #74: TTL-based retention with optional immediate delete and direct-construction default fix
- PR #77: EventStore serialization bootstrap, Outbox processor cleanup guarantee, Queue lease-recovery contract assertion fixes
- Full test suite: 427 tests passing, no regressions
- All Phase 1 PRs staged for user review and merge approval

