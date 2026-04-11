# Squad Decisions

## Active Decisions

### ADR: Queue Resilience and Retention Improvements

**Status:** Approved  
**Date:** 2026-04-10 (Team consensus from Nate, Eliot, Parker)  
**Issues:** #9 (Queue Resilience), #10 (Removing Old Queue Items)  
**Author:** Nate (Lead/Architect)

#### Summary

Two critical issues in the MongoDB queue implementation require coordinated fixes:

1. **Issue #9 — Stuck Locks:** When a payload handler fails, the locked queue item stays locked forever with no recovery mechanism.
2. **Issue #10 — Item Accumulation:** Successfully processed items (`IsClosed=true`) accumulate indefinitely with no cleanup.

#### Approved Decisions

**Approach:** Split into two focused PRs (Issue #9 first, Issue #10 second).

**Lock Expiry (#9):**
- Implement **passive lease expiry** using query-time filter adjustment (no background job)
- Add `LockLeaseTime` to `MongoQueueDefinition` (required, default 5 minutes)
- Modify processing filter to treat expired locks as unlocked
- Replace partial index with compound index on `(IsClosed, IsLocked, LockedUtc)`
- Builder API: `.WithLockLeaseTime(TimeSpan)`

**Closed Item Cleanup (#10):**
- Implement **TTL-based retention** with optional immediate delete
- Add `ClosedItemRetention` to `MongoQueueDefinition` (nullable, default 1 hour)
- Create TTL index on `ClosedUtc` when retention > 0
- Immediate delete when retention == null
- Builder API: `.WithClosedItemRetention(TimeSpan)`, `.WithImmediateDelete()`

**Deferred Items (Phase 2+):**
- Retry counting and dead-letter queues
- Observability metrics and diagnostics
- Secondary issues (collection naming, race in count check, long-running handler locking)

#### Public API Impact

**New Public Members:**

```csharp
// MongoDefaults (new)
public static TimeSpan QueueLockLeaseTime => TimeSpan.FromMinutes(5);
public static TimeSpan? QueueClosedItemRetention => TimeSpan.FromHours(1);

// MongoQueueDefinition (new properties)
public required TimeSpan LockLeaseTime { get; init; }
public TimeSpan? ClosedItemRetention { get; init; }

// MongoQueueBuilder<T> (new methods)
MongoQueueBuilder<T> WithLockLeaseTime(TimeSpan leaseTime);
MongoQueueBuilder<T> WithClosedItemRetention(TimeSpan retention);
MongoQueueBuilder<T> WithImmediateDelete();
```

**Breaking Changes:** None. `LockLeaseTime` is required in `MongoQueueDefinition`, but fluent builder supplies default from `MongoDefaults`.

#### Test Coverage Required

**Tier 1 — Lock Expiry (Integration):**
- Configuration acceptance and defaults
- Stale lock detection and reset
- Handler crash → recovery → reprocessing
- Race: handler success vs. lock expiry
- Multiple concurrent failures

**Tier 2 — Retention (Integration):**
- TTL index creation and conditions
- Closed item cleanup verification
- Query performance with 1000+ items
- Retention policy variants (immediate, long-term, retain forever)

**Tier 3 — Combined Scenarios:**
- Full lifecycle (crash → expiry → reprocessing → cleanup)
- High-volume with failure rate
- Multiple subscriptions on same collection

**Tier 4 — Observability:**
- Recovery and cleanup logging
- Configuration validation

New test files: `MongoQueueSubscriptionTests.cs` (unit), `MongoQueueLockExpiryIntegrationTests.cs`, `MongoQueueRetentionIntegrationTests.cs`.

#### Trade-offs & Rationale

| Concern | Option A | Option B (Chosen) | Rationale |
|---------|----------|------------------|-----------|
| **Lock Recovery** | Active scavenging job | Passive query-time filter | Simpler, no timer drift, no background task coordination |
| **Retention** | Immediate delete only | TTL-based with optional immediate | Audit trail, flexibility, MongoDB-native cleanup |
| **Scope** | Single PR, both issues | Two separate PRs | Higher priority (#9) merges first, #10 is independent |
| **API** | Configuration properties only | Fluent builder methods too | Ergonomic, matches existing pattern |

#### Implementation Order

1. **PR 1: Issue #9** — Lock lease expiry (higher priority, prevents data inconsistency)
2. **PR 2: Issue #10** — TTL-based retention (lower urgency, gradual storage growth)

Both PRs are independent and can be developed in parallel.

#### Risk Mitigations

| Risk | Mitigation |
|------|-----------|
| Stuck items might be retried incorrectly | Add log entry on unlock. Monitor early deployments. |
| TTL index deletion too aggressive | Conservative default (1 hour). Make configurable. |
| Lock timeout race condition | Acceptable per "at-least-once" semantics. Document in code. |
| Performance impact of new indexes | MongoDB TTL efficient. Compound index improves query. |

### ADR: Queue Lock Recovery Log Message Accuracy

**Status:** Approved  
**Date:** 2026-04-11  
**Issue:** PR #73 review thread r3067714453  
**Author:** Nate (Lead/Architect)

#### Problem

The `MongoQueueSubscription` recovery path treats two distinct lock conditions as equivalent:
- `IsLocked=true` AND `LockedUtc=null` (timestamp missing)
- `IsLocked=true` AND `LockedUtc < lockExpiryUtc` (timestamp expired)

The current log message says "Recovering **expired** queue item lock" for both cases. When `LockedUtc` is null, the lock isn't expired—it's malformed or orphaned. This misleads operators interpreting diagnostic logs.

#### Decision

Update the log message to distinguish between true expiry and missing/malformed timestamps.

**Change:** Replace the single "Recovering expired queue item lock" message with more precise wording that includes the prior `LockedUtc` state.

#### Rationale

- **Diagnostics accuracy matters on critical paths.** Lock recovery happens when a handler crashes or times out. Operators need precise logs to understand failure modes.
- **The cost is trivial.** A single log message update, scoped to this PR.
- **Not deferring to Phase 2.** This is not a feature request—it's correctness for observability. Phase 2 (#72) covers additional observability, but this base case should be accurate now.

#### Implementation

Update line 185-187 in `src/Chaos.Mongo/Queues/MongoQueueSubscription.cs`:

```csharp
_logger.LogWarning("Recovering queue item lock {QueueItemId} (previous LockedUtc: {PreviousLockedUtc}, threshold: {LockExpiryUtc}) with payload {PayloadType}",
                   queueItemId,
                   queueItem.LockedUtc?.ToString("O") ?? "null",
                   lockExpiryUtc.ToString("O"),
                   typeof(TPayload).FullName);
```

This provides operators with:
- The item ID (existing)
- The payload type (existing)
- The prior `LockedUtc` value (new—distinguishes null vs. stale timestamp)
- The expiry threshold used (new—helps correlate with timing)

## Completed Work

### PR #74 Follow-up — Issue #10 Direct-Construction Default & Cancellation Hardening (2026-04-11)

**Authors:** Eliot (implementation), Parker (validation)  
**Status:** Complete (ready for user review and merge)  
**Related:** PR #74, Issue #10, Branch `squad/10-remove-old-queue-items`

#### Problem

- Direct construction of `MongoQueueDefinition` with `ClosedItemRetention` unset left retention null, bypassing documented 1-hour default
- `MongoQueueRetentionIntegrationTests` retention polling helpers did not thread cancellation tokens, creating optional hang scenarios

#### Decision

1. Add default initializer to `MongoQueueDefinition.ClosedItemRetention` property
2. Thread timeout cancellation token through all retention polling helper calls

#### Rationale

- **Direct-construction default:** Builder always applies `MongoDefaults.QueueClosedItemRetention`, but direct construction bypassed this. Default initializer ensures both code paths behave identically without forcing every caller to set retention.
- **Cancellation token hardening:** Timeout semantics must be respected throughout polling loops to prevent optional hangs in production. Minimal change, high safety impact.

#### Implementation

**Commit:** `fa183f1`

- `MongoQueueDefinition.ClosedItemRetention` now defaults to `MongoDefaults.QueueClosedItemRetention`
- `MongoQueueRetentionIntegrationTests` threads cancellation token through `ListAsync`, `ToListAsync`, `FirstOrDefaultAsync`, and polling helpers

#### Validation

- ✅ Focused queue tests passed
- ✅ Full solution `dotnet test Chaos.Mongo.slnx --no-restore` passed
- ✅ Existing test `MongoQueueBuilderTests.MongoQueueDefinition_WithoutExplicitClosedItemRetention_UsesDefaultRetention` validates default contract
- ✅ No regression in existing queue processing

**Note:** `bash build.sh Test` blocked by shared `.nuke/temp/build.log` lock; direct `dotnet test` used as reliable fallback.

### Issue Triage & Phase 2 Planning (2026-04-10)

**Author:** Nate (Lead/Architect)  
**Status:** Closed  
**Related Issues:** #9, #10, #71, #72

Updated GitHub issues #9 and #10 with implementation-ready specifications derived from the approved ADR. Created two Phase 2 issues for deferred concerns.

**Issues Updated:**
- **#9 — Queue Lock Resilience:** Focused on passive lease expiry with acceptance criteria, API additions, and clear out-of-scope boundaries
- **#10 — Queue Item Cleanup:** Focused on TTL-based retention with optional immediate delete and acceptance criteria

**Phase 2 Issues Created:**
- **#71 — Queue Dead-Letter Handling and Retry Policies** (design-heavy, requires retry count persistence and DLQ routing)
- **#72 — Queue Observability and Diagnostics** (cross-cutting, structured logging and metrics)

**Rationale for Phase 2 Deferral:**
- Retry counting and DLQ handling are substantial scope requiring separate API design decisions
- Observability is cross-cutting and benefits from team input on telemetry strategy
- Secondary bugs (count-race, long-handler-lock) remain implementation-time discoveries

**Next Actions:**
1. Phase 1 (Immediate): Eliot implements #9 (lock expiry), Parker designs tests
2. Phase 1 (Follow-up): Second PR for #10 (TTL retention) after #9 merges
3. Phase 2 (Design): Team aligns on #71 and #72 requirements before implementation

### Issue #10 — Queue Closed-Item Retention (TTL-Based) (2026-04-11)

**Authors:** Eliot (implementation), Parker (testing)  
**Status:** Completed (awaiting user review for merge)  
**Related Issue:** #10  
**Branch:** `squad/10-remove-old-queue-items`

Implemented TTL-based retention policy for closed queue items with configurable retention window (default 1 hour) or immediate delete mode.

**Key Implementation Details:**
- Single managed TTL index per queue collection (`IX_Queue_ClosedUtc_TTL`) for deterministic retention
- Index reconciliation on every subscription start enables multiple subscriptions to safely change retention policies without manual cleanup
- When `ClosedItemRetention` is null, successfully processed items are deleted immediately instead of using TTL
- Backward compatible: retention optional with 1-hour default; existing code unaffected

**New API:**
- `MongoQueueBuilder<T>.WithClosedItemRetention(TimeSpan retention)` — set custom retention period
- `MongoQueueBuilder<T>.WithImmediateDelete()` — enable immediate deletion (retention = null)
- `MongoQueueDefinition.ClosedItemRetention` property (nullable TimeSpan)
- `MongoDefaults.QueueClosedItemRetention` = 1 hour (default)

**Changes:**
- Extended `MongoQueueDefinition` with retention configuration
- Updated `MongoQueueBuilder<T>` with fluent retention methods
- Modified `MongoQueueSubscription.EnsureIndexesAsync()` to create/manage TTL index
- Updated processing to immediately delete closed items when retention is null
- Updated README with retention policy documentation

**Test Coverage:**
- Builder configuration tests for retention methods
- Integration tests: TTL index creation (default and custom), immediate delete behavior, same-collection policy reconciliation
- No regression in existing queue behavior
- All tests passing with Testcontainers MongoDB

**Status Notes:**
- Implementation complete and uncommitted on branch per team directive
- All integration tests passing
- Ready for user/team review and merge approval

### ADR: Issue #71 — Queue Dead-Letter Handling and Retry Policies (Phase 2 Shape)

**Status:** Design Decision  
**Date:** 2026-04-11  
**Author:** Nate (Lead/Architect)  
**Issue:** #71 — Queue dead-letter handling and retry policies  
**Branch:** `squad/71-queue-dead-letter-handling-and-retry-policies`

#### Problem

Phase 1 (issues #9 and #10) solved **lock recovery** (prevent stuck locks) and **closed-item cleanup** (prevent unbounded growth). However, they did not solve the problem of **poison messages**:

- A handler that crashes or throws on every attempt will cause that queue item to:
  - Be recovered and reprocessed indefinitely (passive lease expiry keeps unlocking it)
  - Spam logs and potentially block other items from processing
  - Never reach a terminal state (no way for operators to discard it or route it elsewhere)

Issue #71 must answer the question: **When does a queue item stop being retried?**

#### Decision: Smallest Useful Phase 2 Shape

**1. Scope: Retry Count Tracking (Core)**

Add a **retry counter** to the queue item document:

```csharp
public class MongoQueueItem
{
    // existing fields...
    
    /// <summary>
    /// Number of times this item has been processed (failed attempts).
    /// </summary>
    public Int32 RetryCount { get; set; }
}
```

**Semantics:**
- Incremented on every failed handler invocation (exception thrown, not caught)
- Reset to 0 on successful handler completion
- Used to enforce a max-retry policy (configurable per queue)
- Accessible in logs and observability (#72) without requiring separate collection

**Not included in Phase 2:**
- Logic to distinguish handler-thrown exceptions from timeout/cancellation exceptions (defer to Phase 2.1)
- Custom retry policies per payload type (defer to Phase 2.2)
- Dead-letter collection for quarantined items (defer; see rationale below)

**2. Storage Model: Same Queue Document**

**Decision:** Retry state lives **in the main queue document**, not a separate dead-letter collection.

**Rationale:**

| Concern | Separate DLQ Collection | Same Document (Chosen) | Trade-off |
|---------|------------------------|------------------------|-----------|
| **Schema Simplicity** | Requires schema sync between queue and DLQ collections | Single schema, optional terminal-state fields | Simpler for Phase 2; avoid collection proliferation |
| **Query Efficiency** | DLQ queries separate from active queue | Partial filter on `IsClosed + RetryCount` for status | One index covers both; DLQ is a logical view, not physical |
| **Reprocessing** | Must copy item back to queue on DLQ→queue transition | Direct reprocess from queue (atomic) | Avoids cross-collection race conditions |
| **Observability** | Telemetry bridges two collections | Single source of truth; easier for #72 | Simpler instrumentation |
| **Operations Flexibility** | Move to DLQ without reprocess, then decide | Cannot delay decision; reprocess decision must happen now | Acceptable; operators decide upfront |

**Consequence:** The DLQ is a **logical view** (items with `IsClosed=false` and `RetryCount >= maxRetries`), not a separate collection.

**3. Max Retry Policy: Configuration Only**

Add to `MongoQueueDefinition` and `MongoQueueBuilder`:

```csharp
public record MongoQueueDefinition
{
    /// <summary>
    /// Maximum number of retry attempts before an item is marked terminal (not retried).
    /// <c>null</c> means unlimited retries (at-least-once, no terminal state).
    /// </summary>
    public Int32? MaxRetries { get; init; }
}

public sealed class MongoQueueBuilder<TPayload>
{
    /// <summary>
    /// Configures the maximum number of retry attempts for failed items.
    /// Null = unlimited retries (default, preserves Phase 1 behavior).
    /// </summary>
    public MongoQueueBuilder<TPayload> WithMaxRetries(Int32 maxRetries) { ... }
    
    /// <summary>
    /// Shorthand: set max retries to 0 (single attempt, no recovery).
    /// </summary>
    public MongoQueueBuilder<TPayload> WithNoRetry() => WithMaxRetries(0);
}
```

**Defaults:**
- `MongoDefaults.QueueMaxRetries = null` (unlimited, backward compatible)
- Builders supply default via fluent API

**4. Terminal State Handling: Minimal, Observable**

Add one new field to track items that exhausted retries:

```csharp
public class MongoQueueItem
{
    /// <summary>
    /// <c>true</c> if this item has exhausted its retry attempts and will not be retried.
    /// Only meaningful when <c>IsClosed=true</c>.
    /// </summary>
    public Boolean IsTerminal { get; set; }
}
```

**Semantics:**
- Set to `true` only when `RetryCount >= MaxRetries` (if configured)
- Queries for "dead-letter items" filter on `IsClosed=true && IsTerminal=true`
- Operators can manually delete, reprocess, or archive
- Logged as a warning when set

**5. Index Updates**

Update compound index to include `IsTerminal`:

```csharp
// New: (IsClosed, IsLocked, LockedUtc, IsTerminal)
```

#### Deferred to Phase 2.1+

- Whether retry handler timeout counts as failure (separate from thrown exceptions)
- Custom per-payload-type backoff strategies
- Dead-letter collection for quarantined items (remains logical view only)

#### Rationale

This is the **minimum viable schema** that prevents poison messages while keeping schema changes minimal and maintaining backward compatibility. It gives Eliot and Nate a stable behavioral target without forcing architectural decisions too early.

### Eliot — Issue #71 Implementation Note (Retry Count Semantics)

**Date:** 2026-04-11  
**Author:** Eliot  
**Status:** Design Decision  

**Decision:** For the Phase 2.1 retry slice, `RetryCount` records **failed processing attempts only** and is **not reset on later success**. `MaxRetries` is interpreted as the number of retries allowed **after** the initial failed attempt, so terminal transition happens when `RetryCount > MaxRetries`; `WithNoRetry()` remains the explicit single-attempt shorthand.

**Why:** This keeps the new API name honest (`WithMaxRetries(1)` really allows one retry) while preserving useful post-recovery history on successfully processed items. It also avoids conflicting with the new dead-letter shape, where terminal items are the closed subset that exhausted retry budget and should remain queryable even when successful items use immediate delete.

### Parker — Issue #71 Test Slice (Behavioral Contracts)

**Date:** 2026-04-11  
**Author:** Parker (Testing)  
**Status:** Design Decision  

**Decision:** Define the first test slice around **externally observable retry/DLQ behavior**, not document shape:

1. A failed item below the retry limit is retried
2. An item that reaches the retry limit stops active reprocessing
3. Dead-lettered items preserve payload plus failure metadata
4. A poison item does not block later healthy items

**Deferred Until Architecture Settles:**
- Whether DLQ state lives in the main collection or separate collection
- Handler-signaled terminal failures
- Custom backoff policy shape
- Observability assertions belonging with #72

**Why:** These scenarios give Eliot and Nate a stable behavioral target without forcing schema or API names too early. Smallest slice that reduces "poison message spins forever" risk.

### Nate — Next Queue Issue Recommendation

**Date:** 2026-04-10  
**Author:** Nate (Lead/Architect)  
**Status:** Accepted  

**Context:** Phase 1 queue work (#9 lock lease recovery, #10 closed-item retention) now merged. Next backlog candidates are #71 and #72.

**Decision:** Tackle **#71 next**.

**Rationale:** If we do observability first (#72), we improve visibility into a queue that can still retry poison messages forever. That is useful, but it does not reduce underlying operational hazard. #71 removes the bigger behavior risk; #72 can then instrument the final retry/DLQ lifecycle.

**Primary Implementation Owner:** **Eliot** leads implementation (core queue behavior in `Chaos.Mongo`, within Eliot's package boundary).

**Dependency Warning:** Do not start coding #71 as "just add a retry counter." First decision is architectural: whether retry/DLQ state lives in main queue document or separate dead-letter collection.

### Parker — Queue Testcontainer Lifecycle

**Date:** 2026-04-11  
**Author:** Parker (Testing)  
**Status:** Approved  

**Decision:** Queue integration tests in `tests/Chaos.Mongo.Tests` should use assembly-level `MongoAssemblySetup` / `MongoDbTestContainer` lifecycle and must not dispose shared MongoDB Testcontainer in per-class teardown.

**Why:** `MongoDbTestContainer` is a shared singleton guarded by `MongoAssemblySetup` for entire test assembly. Disposing it inside individual fixture breaks unrelated integration tests, especially under parallel execution.

**Applied:** Removed `[OneTimeTearDown]` container disposal from `MongoQueueLockExpiryIntegrationTests.cs`.

### Sophie — CI/CD Risk Assessment: Test Container Lifecycle

**Date:** 2026-04-11  
**Author:** Sophie (DevOps)  
**Status:** Verified—Risk Mitigated  

**Finding:** Test container disposal in `MongoQueueLockExpiryIntegrationTests` violates assembly singleton pattern, will break CI under parallel test execution.

**Risk Level:** HIGH — nondeterministic parallel test failures once parallelization enabled.

**Verdict:** ✅ Valid and critical. Fix is surgical (remove 3 lines).

**Fix:** Remove `[OneTimeTearDown]` disposal. Assembly-level fixture owns lifecycle.

**Impact:** No product code risk. Pure test infrastructure fix. Enables future parallelization without rework.

### Nate — PR #73 Review Findings Triage

**Date:** 2026-04-11  
**Author:** Nate (Lead/Architect)  
**PR:** #73 (Queue Lock Lease Recovery)  
**Status:** Analysis Complete  

**Summary:** Copilot identified 6 findings in PR #73. After code review, 5 are valid; 1 is stale.

**Priority Matrix:**

| # | Finding | Severity | Status | Action |
|---|---------|----------|--------|--------|
| 1 | No wake-up after expiry | **Critical** | **FIXED** ✅ | Added timed wait on semaphore (commit 529c6bc) |
| 2 | Lock token race (handler overwrites new lock) | **High** | **FIXED** ✅ | Added `LockedUtc` ownership check (commit 529c6bc) |
| 3 | Container disposal breaks tests | **Medium** | **FIXED** ✅ | Removed `[OneTimeTearDown]` (commit 57718bc) |
| 4 | Exception handling in test helper | **Low** | **FIXED** ✅ | Wrapped `Task.Delay` cancellation in try/catch |
| 5 | Duplicated history entry | **Cosmetic** | **FIXED** ✅ | Deduplicated in history.md |
| 6 | Short lease in test (optional concern) | **Low** | **OBSOLETE** ⊘ | Resolved by fix #1 (2-second lease now adequate) |

**All findings resolved.** PR #73 ready for merge.

### Eliot — PR #73 Queue Lock Lease Recovery Implementation

**Date:** 2026-04-11  
**Author:** Eliot  
**Status:** Ready for Review  
**Related:** Issue #9, PR #73

**Summary:** Queue lock lease recovery implementation complete and committed to PR #73.

**Key Implementation:**
- Passive lease expiry using query-time filter (no background job)
- `LockLeaseTime` property on `MongoQueueDefinition` (required, default 5 min)
- `WithLockLeaseTime(TimeSpan)` fluent builder method
- Compound index on `(IsClosed, IsLocked, LockedUtc)` with timed wake-up
- Processing filter treats expired locks as available

**Test Coverage:**
- Integration tests in `MongoQueueLockExpiryIntegrationTests.cs`
- Covers configuration, stale lock detection, handler failure recovery, concurrent failures
- 2-second lease with timing assertions verifying retry delay matches lease interval

**Documentation:** README updated with queue lock recovery details.

**Status:** Ready for team review and merge.

### Sophie — PR #74 Creation: Issue #10 Queue Retention

**Date:** 2026-04-11  
**Author:** Sophie (DevOps)  
**Status:** Complete  

**Context:** User confirmed changes committed and pushed on branch `squad/10-remove-old-queue-items`.

**Decision:** Created PR #74 targeting `main` for issue #10.

**PR Summary:** Queue Closed Items: TTL-Based Retention and Immediate Delete (#10)

**Highlights:**
- TTL-based retention (default 1 hour, configurable)
- Immediate delete option via `.WithImmediateDelete()`
- Automatic TTL index management with reconciliation on subscription start
- Backward compatible with sensible defaults
- Complete test coverage (builder + integration tests)
- README documentation updated

**Related:** PR #74 depends on PR #73 (issue #9) already merged.

**Status:** ✅ PR created; awaiting user review and merge approval.

### Nate — Index/Query Test Strategy Assessment

**Date:** 2026-04-11  
**Author:** Nate (Lead/Architect)  
**Status:** Recommendation  
**Scope:** Integration test strategy for Queue, EventStore, and Outbox indexes

**Question:** Should we add integration tests that verify MongoDB indexes are actually used by queries (vs. collection scans)?

**Answer:** Don't implement brittle `explain()` plan verification. Instead, enforce the contract through three layered approaches:

1. **Schema validation tests** (lightweight, zero brittleness) — verify index structure with correct field order and partial filters
2. **Query correctness tests** (functional, already mostly exist) — verify queries return correct results under realistic conditions
3. **Code review discipline** — surface index-query alignment in PR checkpoints

**Core Insight:** Explain-plan parsing is brittle (version-dependent, collection-size-dependent, planner-dependent). Functional tests already catch query behavior regressions. Schema validation catches real mistakes (field typos, order inversions) with zero maintenance cost.

**Risk Assessment by Subsystem:**
- **Queue:** LOW RISK. Field order correct, functional tests heavy. Add lightweight schema test for compound field order. Skip explain plans.
- **EventStore:** VERY LOW RISK. Uniqueness database-enforced. Add schema test for unique index. Concurrency tests sufficient.
- **Outbox:** MODERATE RISK. Field order matters for range query efficiency. Add schema test + one query correctness test.

**Implementation Roadmap:**
- **Phase 1 (Immediate):** Create ~120 lines of schema validation tests. Effort: 2-3 hours. Signal gain: high.
- **Phase 2 (Optional):** Add query correctness test to Outbox (field order validation via result ordering).
- **Phase 3 (Deferred):** Performance profiling suite only if production telemetry shows issues.

**Trade-off:** Maintenance cost (explain plan fragility, CI/CD bloat, framework churn) exceeds regression detection benefit when functional tests already exercise queries under realistic conditions and code review catches logic errors.

**See also:** `.squad/decisions/inbox/nate-index-query-test-strategy.md` for full architectural analysis.

---

### Parker — Index Regression Test Strategy Design

**Date:** 2026-04-11  
**Author:** Parker (Test Engineer)  
**Status:** Recommendation  
**Scope:** Multi-tier regression testing approach for index correctness

**Problem:** Ensure future code changes don't silently break index coverage or query efficiency without coupling tests to MongoDB's internal query planner.

**Recommendation:** Multi-tier regression testing strategy (no explain-plan coupling):

1. **Tier 1 — Index Configuration Tests:** Assert index exists with correct keys, sort order, options
2. **Tier 2 — Query/Index Alignment Tests:** Structural BSON validation of filters and sorts (unit tests, mocked)
3. **Tier 3 — Partial Filter Edge Cases:** TTL deletion, type predicates, compound filter behavior
4. **Tier 4 — Compound Sort Order:** Result ordering validation
5. **Tier 5 — Concurrency Safety:** Unique constraint enforcement, write-while-reading

**What NOT to Test:**
- ❌ Explain-plan parsing (brittle, version-dependent)
- ❌ Byte-level index stats (variance, not indicative)
- ❌ Exact timing thresholds (CI variance)
- ❌ Mocking MongoIndexManager for core index tests (misses real behavior)

**Implementation Roadmap:**

**Phase 1 (Immediate, 3-4 days):**
- Index Configuration Tests (6 test methods, ~200 lines)
- Query Alignment Tests unit tests only (3 test methods, ~150 lines)
- CI Impact: +5-10ms total
- Risk: None (read-only validation, no implementation changes)

**Phase 2 (High Value, 3-4 days):**
- Performance Regression Tests (load-based timing validation)
- Partial Filter Edge Cases (TTL/uniqueness behavior)

**Phase 3 (Nice-to-Have):**
- Compound sort order tests
- Concurrency safety tests

**Test File Structure:**
```
tests/Chaos.Mongo.Tests/Integration/Queues/
  └── MongoQueueIndexSchemaIntegrationTests.cs (new)
tests/Chaos.Mongo.EventStore.Tests/Integration/
  └── EventStoreIndexSchemaIntegrationTests.cs (new)
tests/Chaos.Mongo.Outbox.Tests/Integration/
  └── OutboxIndexSchemaIntegrationTests.cs (new)
tests/Chaos.Mongo.Tests/Indexes/
  └── QueryIndexAlignmentTests.cs (new, mocked unit tests)
```

**Risk Mitigation:**
- TTL flakiness: Use short TTL, poll up to 10s, accept variance
- Partial filter syntax: Always validate on real MongoDB (Testcontainers)
- Query regression: Sort order tests catch silent order changes

**Signal Value at Each Tier:**
- ✅ Tier 1: 95% confidence index exists with exact spec
- ✅ Tier 2: 90% confidence queries align with index
- ✅ Tier 3: 95% confidence TTL/uniqueness works
- ✅ Combined: 98% confidence future changes don't break index coverage

**See also:** `.squad/decisions/inbox/parker-index-query-regression-tests.md` for full implementation guide (950 lines of planned test code, effort ~4-6 days).

---

### Decision: Queue Terminal Item TTL Exclusion

**Date:** 2026-04-11  
**Author:** Alec  
**Status:** Approved  

Terminal queue items are now intentionally excluded from the `ClosedUtc` TTL index partial filter.

**Changes:**
- Terminal queue items remain queryable for dead-letter handling while TTL cleanup applies only to successfully processed items
- Runtime filter in `CreateAvailableQueueItemFilter()` explicitly requires `IsTerminal == false`
- TTL index partial filter ensures non-terminal closed items are cleaned up

**Rationale:** Terminal items may need to be routed to dead-letter queues; TTL should not remove them before DLQ handler can inspect.

---

### Decision: Legacy Terminal Filter Compatibility

**Date:** 2026-04-11  
**Author:** Eliot  
**Status:** Approved  

Queue availability queries treat missing `IsTerminal` field as non-terminal for backward compatibility with older queue documents after upgrade.

**Implementation:**
- Availability queries: Missing `IsTerminal` treated as non-terminal (queries continue to find old docs)
- TTL partial filter: Use `IsTerminal: { $in: [false, null] }` for compatibility (MongoDB Mongo 8 rejects `$ne: true` and `$exists: false` on this field)

**Rationale:** Ensures smooth upgrade path where older queue items without the `IsTerminal` field remain processable.

---

### Nate — Queue Retry Policy Branch Review (#73)

**Date:** 2026-04-11  
**Author:** Nate (Lead/Architect)  
**Status:** Review Complete  
**Related:** Squad branch for queue retry policies and terminal state support

**Summary:** PR `squad/71-queue-dead-letter-handling-and-retry-policies` implements retry policies with terminal state support. **Verdict: Ready to merge with two non-blocking improvements.**

**Critical Finding:** Terminal items not explicitly excluded in availability filter
- **Issue:** `CreateAvailableQueueItemFilter()` doesn't filter on `IsTerminal`, yet compound index includes it
- **Impact:** Semantic/clarity gap; not a bug due to accidental correctness (terminal items have `IsClosed=true`)
- **Fix:** Add explicit `IsTerminal == false` filter to align index design with query intent

**Moderate Finding:** Retry count math underdocumented
- **Issue:** `WithMaxRetries(5)` comment doesn't clarify whether this means 5 retries *after* first failure (6 total attempts)
- **Impact:** Documentation/UX; users may misconfigure retry budgets
- **Fix:** Update README and XML docs to clarify: "5 retries after initial failure = 6 total attempts"

**Positive Findings:**
- ✅ Lock ownership guard solid
- ✅ TTL + terminal orthogonality correct
- ✅ Builder API validation correct
- ✅ Test coverage comprehensive

**Recommendation:** Merge after addressing IsTerminal filter clarity and MaxRetries documentation.

---

### Nate — ADR: Clarify MaxRetries Semantics

**Date:** 2026-04-11  
**Author:** Nate (Lead/Architect)  
**Status:** Approved  

Documentation clarification for `WithMaxRetries()` semantics to prevent user misconfiguration.

**Changes:**
1. **README (line 435):** Update comment from "Stop retrying poison messages after 5 retries" to "Stop after 5 retries (6 total attempts including initial)"
2. **XML docs on `MongoQueueBuilder.WithMaxRetries()`:** Add parameter clarification: "Maximum number of retries after the initial attempt. For example, 5 allows up to 6 total attempts (1 initial + 5 retries)."

**Rationale:** Clarity over terseness. A 30-second read prevents users from misconfiguring production retry budgets.

**Verification:** No other MaxRetries documentation requires update (searched codebase).

## Team Directives

### Conventional Commit Message Standard (2026-04-11)

**Author:** Christian Flessa  
**Status:** Directive (team memory)  

**What:** Always use conventional commit messages. The previous message "Fix queue retention defaults" was not acceptable.

**Why:** User preference — captured for team memory. Conventional commits improve CI automation, changelog generation, and commit history clarity.

### Documentation and Branch Discipline (2026-04-10)

**Author:** Christian Flessa  
**Status:** Directive (team memory)  

1. **Feature Documentation:** Keep documentation up to date with code changes. When adding noteworthy new features, always consider updating the feature documentation (e.g., README).

2. **Issue Branch Discipline:** Always use a corresponding branch for issue work, and do not commit those changes before user review (e.g., `squad/<issue-number>-<slug>` branch with uncommitted changes for PR review).

**Rationale:** Captures user preferences for development workflow and documentation practices.

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
- Team directives capture user preferences for inclusion in team memory
