# Parker — History

## Core Context

- **Project:** A .NET MongoDB library providing migrations, event store, transactional outbox, queues, and helpers published as multiple NuGet packages
- **Role:** Tester
- **Joined:** 2026-04-09T19:19:45.618Z

## Learnings

### Queue Implementation Architecture (2025-04-10)

**Lock Pattern:** Optimistic concurrency using `FindOneAndUpdate` with conditions on `IsLocked` and `IsClosed`. No transactions needed—MongoDB atomic updates guarantee single-consumer semantics.

**Processing Flow:** Dual-task subscription:
- Change stream monitor watches for inserts, signals processing task
- Processing task polls with backoff, acquires locks, invokes handlers
- Failure handling: Handler exceptions are logged and caught; item lock is held until explicitly released or handler completes

**Critical Gap—Lock Expiry:** Currently no mechanism to detect or recover from hung/crashed handlers. Locked items can remain locked forever. Recovery requires:
1. Stale lock detection (compare `LockedUtc` + `LockExpiryDuration` against now)
2. Atomic reset: `FindOneAndUpdate` to clear `IsLocked`, `LockedUtc` when stale
3. Configuration: `LockExpiryDuration` on `MongoQueueDefinition`

**Critical Gap—Retention:** Processed items (marked `IsClosed = true`) are never deleted. Collections grow unbounded. Solution:
1. TTL index on `ClosedUtc` field with partial filter on `IsClosed == true`
2. Configuration: `RetentionPolicy` (Delete vs. Retain) + `RetentionDays` on `MongoQueueDefinition`

**Index Strategy:** Composite index `(IsClosed ASC, IsLocked ASC)` with partial filter for active items. Separate TTL index on `ClosedUtc` for cleanup. Both are idempotent—safe to create multiple times.

**Testing Approach:** 
- Unit tests for lock expiry logic require extracting stale detection to testable method (not yet in code)
- Integration tests use Testcontainers MongoDB to verify full flow with real timing
- Race conditions: Handler success vs. lock expiry (must not double-reset), multiple subscriptions on same collection (atomic operations prevent conflicts)

**Known Behavioral Risks:**
- No distributed locking: Multiple subscriptions on same collection could theoretically conflict, but MongoDB atomic updates prevent actual issues
- Handler thread death not explicitly detected: Relies on timeout to detect crash (no explicit health check)
- At-least-once semantics assumed for handlers (must be idempotent)

### Current Test Coverage (2025-04-10)

**Unit Tests:** 7 test files, mostly mocked. Cover lifecycle (start/stop/dispose), payload validation, null checks. No coverage of lock expiry, retention, or handler timeouts.

**Integration Tests:** ~8 scenarios in `MongoQueueIntegrationTests.cs`. Cover happy path, concurrency, exceptions, empty queue. Use real MongoDB with Testcontainers. Missing:
- Handler hangs/crashes → lock expiry recovery
- Closed item cleanup / retention
- Race conditions (concurrent crash + lock expiry)
- Long-running handlers with timeout
- Query performance degradation with many closed items

**Test Files to Create:**
1. `MongoQueueSubscriptionTests.cs` — unit tests for stale lock logic (mocked)
2. `MongoQueueLockExpiryIntegrationTests.cs` — crash/hang scenarios with recovery
3. `MongoQueueRetentionIntegrationTests.cs` — TTL index, cleanup, performance

### Configuration & DI Pattern (2025-04-10)

**Fluent Builder Pattern:** `.WithQueue<TPayload>()` on `MongoBuilder` returns `MongoQueueBuilder<TPayload>` with chained `.With*()` methods. New queue features should follow this pattern:
- `.WithLockExpiry(duration)` — set `LockExpiryDuration`
- `.WithRetention(policy, days)` — set `RetentionPolicy`, `RetentionDays`

**Storage:** Configuration lives on `MongoQueueDefinition` (passed to `MongoQueueSubscription`). `MongoQueueSubscription` constructor should accept definition and extract expiry/retention settings.

**Backwards Compatibility:** Existing code without expiry/retention config should have defaults:
- `LockExpiryDuration = TimeSpan.FromMinutes(5)` (reasonable default, prevents forever-locked items)
- `RetentionPolicy = Retain` (don't break existing behavior by deleting items)

### Team Collaboration Notes (2025-04-10)

**Backwards Compatibility:** Existing code without expiry/retention config should have defaults:
- `LockExpiryDuration = TimeSpan.FromMinutes(5)` (reasonable default, prevents forever-locked items)
- `RetentionPolicy = Retain` (don't break existing behavior by deleting items)

### Team Collaboration Notes (2025-04-10)

- Nate (architect) should review recovery job architecture—should it be inline in subscription polling loop or separate scheduled task?
- Eliot (dev) should define cleanup job: TTL index (fire-and-forget) vs. scheduled cleanup job (observable)?
- Sophie (CI) should verify Testcontainers MongoDB handles TTL correctly in test environment
- Decisions written to `.squad/decisions/inbox/parker-queue-test-plan.md` for scribe review

### Team Orchestration & Consensus (2026-04-10)

**Session:** Multi-agent coordination (Nate, Eliot, Parker) — Architecture review and planning

**Consensus Decisions:**

1. **Lock Expiry Implementation:**
   - Passive query-time filter approach confirmed (simpler than background job)
   - Configuration stored in `MongoQueueDefinition` with sensible default (5 min)
   - Eliot to implement in ProcessQueueItemsAsync filter modification
   - Parker to write stale lock detection unit tests (mocked)

2. **Retention Cleanup Implementation:**
   - TTL index approach confirmed (MongoDB native, most reliable)
   - Partial filter on `IsClosed == true` prevents accidental deletion
   - Configuration optional with backward-compatible defaults
   - Parker to write TTL index creation verification tests

3. **Test Coverage Strategy Approved:**
   - TDD approach: Parker creates test fixtures before Eliot codes
   - Three new test files ready for implementation
   - Tier 1–4 coverage comprehensive (lock expiry, retention, integration, observability)
   - Race condition tests validate atomic MongoDB operations prevent conflicts

4. **API Contract Locked:**
   - No breaking changes (all config optional)
   - Fluent builder methods follow existing MongoBuilder pattern
   - `MongoDefaults` extended with queue-specific constants
   - Configuration validation in builder (reject invalid timeouts/retention)

5. **PR Strategy:**
   - **PR #9 (Lock Lease Expiry):** Higher priority, prevents data inconsistency
   - **PR #10 (TTL Retention):** Lower urgency, gradual storage impact
   - Both independent, can be developed in parallel
   - Secondary concerns (retries, observability, collection naming) deferred Phase 2+

**Risk Validation:**
- Multiple subscription race conditions → MongoDB atomic updates prevent conflicts
- Handler success vs. lock expiry race → acceptable per at-least-once semantics
- TTL index doesn't delete active items → partial filter on `IsClosed=true` guarantees safety
- Configuration not respected → unit tests validate config flow

**Implementation Status:** ✅ Ready for development
- Ownership clear (Eliot lead, Parker test fixtures, Nate review)
- Architecture locked, API contract finalized
- Test strategy comprehensive, no blockers identified

### Shared Integration Testcontainer Lifecycle (2026-04-11)

- `tests/Chaos.Mongo.Tests/Integration/MongoAssemblySetup.cs` owns starting and stopping the shared `MongoDbTestContainer` for the whole test assembly.
- Individual integration fixtures in `tests/Chaos.Mongo.Tests` may call `MongoDbTestContainer.StartContainerAsync()` in `[OneTimeSetUp]` to get the running container reference, but they should not dispose it in fixture teardown.
- `tests/Chaos.Mongo.Tests/Integration/Queues/MongoQueueLockExpiryIntegrationTests.cs` was corrected to follow this pattern after PR #73 review feedback.

### 2026-04-11: Issue #10 Queue Closed-Item Retention — Test Coverage

**Session:** Issue #10 implementation validation  
**Branch:** `squad/10-remove-old-queue-items`  
**Status:** Completed (awaiting user review for merge)

**Test Coverage Added:**

**Builder Configuration Tests:**
- `.WithClosedItemRetention(TimeSpan)` accepts valid durations and configures policy
- `.WithImmediateDelete()` sets retention to null
- Configuration validation rejects invalid values
- Default retention (1 hour) applied when no explicit config

**Integration Tests:**
- TTL index creation with default (1 hour) and custom retention periods
- Immediate delete behavior (null retention): items deleted within expected window
- Same-collection policy reconciliation: multiple subscriptions with different policies
- Index lifecycle: TTL index dropped when switching to immediate-delete mode
- Query performance with large closed-item collections (1000+ items)

**Validation Outcomes:**
- ✅ All retention configuration paths exercise correctly
- ✅ TTL index created with configured retention period
- ✅ Immediate delete removes items within expected window
- ✅ Multiple policies reconcile without conflicts
- ✅ No regression in existing queue processing
- ✅ All tests passing with Testcontainers MongoDB

**Status:** Full coverage achieved, ready for production validation after merge.

### 2026-04-11: PR #74 Follow-up — Direct-Construction Default & Cancellation Hardening Validation

**Session:** PR #74 follow-up test validation  
**Branch:** `squad/10-remove-old-queue-items`  
**Status:** Complete (ready for user review and merge)

**Work Completed:**

1. **Reviewed Direct-Construction Default Fix**
   - Analyzed `MongoQueueDefinition.ClosedItemRetention` default initialization
   - Confirmed alignment with builder-supplied default from `MongoDefaults`
   - Validated existing test `MongoQueueBuilderTests.MongoQueueDefinition_WithoutExplicitClosedItemRetention_UsesDefaultRetention` covers contract

2. **Reviewed Cancellation Token Hardening**
   - Analyzed retention polling helper cancellation token threading
   - Confirmed `ListAsync`, `ToListAsync`, `FirstOrDefaultAsync`, polling helpers all respect timeout
   - Determined no additional test scenarios required—hardening is implementation-detail safety, not API behavior change

**Validation Results:**
- ✅ Focused queue tests passed
- ✅ Full solution `dotnet test Chaos.Mongo.slnx --no-restore` passed
- ✅ Existing test coverage sufficient for follow-up scope
- ⚠️  Note: `bash build.sh Test` blocked by shared `.nuke/temp/build.log` lock; used direct `dotnet test` as reliable fallback

**Outcome:** PR #74 follow-up is test-complete and ready for user review/merge. No additional test code needed.
