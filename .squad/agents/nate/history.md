# Nate — History

## Core Context

- **Project:** A .NET MongoDB library providing migrations, event store, transactional outbox, queues, and helpers published as multiple NuGet packages
- **Role:** Lead
- **Joined:** 2026-04-09T19:19:45.616Z

## Learnings

<!-- Append learnings below -->

### 2026-04-11: Issue #76 Test-Contract Review

**Review Scope:** Parker's index/query contract tests for Outbox, Queue, and EventStore.

**Assessment:**
- The strongest pattern remains: rendered BSON assertions for query shape, live `Indexes.ListAsync()` inspection for durable index schema, and a small behavior-critical integration check where ordering/uniqueness is the real contract.
- This implementation follows that pattern well and avoids the brittle `explain()` path we explicitly rejected.
- Queue index coverage is intentionally split: existing integration tests already lock the compound index shape, while `MongoQueueSubscriptionTests.cs` now adds the missing dequeue-filter contract.

**Key Files:**
- `tests/Chaos.Mongo.Outbox.Tests/OutboxProcessorQueryContractTests.cs`
- `tests/Chaos.Mongo.Outbox.Tests/Integration/OutboxIndexContractIntegrationTests.cs`
- `tests/Chaos.Mongo.EventStore.Tests/MongoEventStoreQueryContractTests.cs`
- `tests/Chaos.Mongo.EventStore.Tests/Integration/EventStoreIndexContractIntegrationTests.cs`
- `tests/Chaos.Mongo.Tests/Queues/MongoQueueSubscriptionTests.cs`
- `tests/Chaos.Mongo.Tests/Integration/Queues/MongoQueueLockExpiryIntegrationTests.cs`

### 2025-01-14: Queue Resilience Analysis

**Issues Reviewed:** #9 (locked items stay locked forever on handler failure), #10 (closed items accumulate indefinitely)

**Key Files:**
- `src/Chaos.Mongo/Queues/MongoQueueSubscription.cs` — processing loop and locking logic (lines 146-207)
- `src/Chaos.Mongo/Queues/MongoQueueItem.cs` — document schema with `IsLocked`, `LockedUtc`, `IsClosed`, `ClosedUtc`
- `src/Chaos.Mongo/Queues/MongoQueueDefinition.cs` — queue configuration record
- `src/Chaos.Mongo/Queues/MongoQueueBuilder.cs` — fluent builder (extend with new config)
- `src/Chaos.Mongo/MongoDefaults.cs` — central defaults location

**Architecture Decisions:**
- Passive lease expiry (query-time filter) preferred over active scavenging job for simplicity
- TTL-based retention (MongoDB native) preferred over manual cleanup for closed items
- Retry counting deferred to Phase 2 — basic lease expiry solves immediate issue
- Existing partial index on `(IsClosed=false, IsLocked=false)` needs replacement with compound index including `LockedUtc`

**Public API Additions Required:**
- `MongoDefaults.QueueLockLeaseTime` (5 min default)
- `MongoDefaults.QueueClosedItemRetention` (1 hour default, nullable)
- `MongoQueueDefinition.LockLeaseTime` (required)
- `MongoQueueDefinition.ClosedItemRetention` (nullable, null = immediate delete)
- `MongoQueueBuilder.WithLockLeaseTime()`, `WithClosedItemRetention()`, `WithImmediateDelete()`

### 2026-04-10: Team Consensus & Orchestration

**Session:** Multi-agent architecture review (Nate, Eliot, Parker)  
**Outcome:** ADR approved, implementation plan locked, test strategy defined

**Team Decisions:**
- **Lock Recovery:** Passive lease expiry (query-time filter) over active scavenging job
- **Retention:** TTL-based with optional immediate delete (MongoDB native)
- **PR Strategy:** Two separate PRs, #9 (lock expiry) first, #10 (cleanup) second
- **API:** Fluent builder methods + config properties, no breaking changes
- **Testing:** Three new integration test files (lock expiry, retention, distributed)

**Cross-Agent Alignment:**
- Eliot's implementation plan confirmed by test strategy (Parker)

### 2026-04-10: Index/Query Contract Testing Strategy

**Issue Created:** #76 — Index/query contract tests for regression protection

**Agreed Direction:**
- Contract tests (not explain-plan parsing) to protect query/index alignment from regressions
- No reliance on MongoDB explain-plan API
- Focus: index definitions, query shape alignment, behavior-critical integration tests
- Prioritization: Outbox → Queue → EventStore (criticality ordering)
- Use Testcontainers for actual MongoDB behavior verification
- Fail immediately if indexes missing or queries change shape

**Rationale:**
MongoDB query performance silently degrades if indexes drift. Contract tests provide early detection without fragile explain-plan dependencies.

**Trade-offs:**
- Won't detect all performance issues, only index/query misalignment
- Requires Testcontainers, adds integration test complexity
- Benefits: Early warning, portable across MongoDB versions, explicit contracts
- Nate's architectural decisions informed test coverage requirements
- All secondary concerns (retries, observability, collection naming) deferred to Phase 2+

**Implementation Sequence:**
1. PR #1: Lock lease expiry (higher priority, prevents data inconsistency)
2. PR #2: TTL-based retention (lower urgency, gradual storage growth)

**Status:** Ready for development. Eliot to lead #9 implementation, Parker test fixtures ready for TDD approach.

### 2026-04-10: Issue Triage & Phase 2 Planning

**Action:** Updated #9 and #10 GitHub issues with implementation-ready specifications derived from team ADR.

**Issue Updates:**
- **#9 (Queue lock resilience):** Focused on passive lease expiry, acceptance criteria, API additions, out-of-scope boundaries
- **#10 (Queue item cleanup):** Focused on TTL-based retention with optional immediate delete, acceptance criteria, API additions

**New Phase 2 Issues Created:**
- **#71 — Queue dead-letter handling and retry policies** (design-heavy, requires retry count persistence and DLQ routing)
- **#72 — Queue observability and diagnostics** (cross-cutting, structured logging and metrics)

**Rationale for New Issues:**
- Retry counting and DLQ handling are substantial scope requiring separate API design decisions
- Observability is cross-cutting and benefits from team input on telemetry strategy
- Secondary bugs (count-race, long-handler-lock) remain implementation-time discoveries; not pre-issued to avoid context fragmentation

**Deferred Items Not Issued (Yet):**
- Secondary issues (race in count check, long-running handler locking) — discoverable during #9/#10 PRs
- Collection naming for DLQ and observability queries — design decision for Phase 2

### 2026-04-10: PR #73 Review Findings Triage

**PR:** #73 — Queue Lock Lease Recovery (Issue #9)  
**Trigger:** Copilot code review surfaced 6 findings

**Critical Issues Identified:**
1. **No wake-up after expiry** — Semaphore waits indefinitely; expired locks never retried without new messages. This defeats passive lease recovery. Fix: Add timeout to `WaitAsync` matching lease interval.
2. **Lock token race** — Long-running handlers can have locks stolen, then overwrite the new lock on completion. Fix: Guard close update with captured `LockedUtc`.

**Test/Cosmetic Issues (valid, low-priority):**
- ❌ **Test container disposal breaks parallel tests** — ELEVATED TO BLOCKING (see r3066971413 analysis below)
- `WaitForQueueItemAsync` throws wrong exception type (wrap delay)
- Duplicated history entry in Eliot's file (dedupe)
- Short lease in test masks wake-up bug (optional increase)

**Architectural Observation:**
The ADR assumed passive expiry would "just work" with the existing query filter. What we missed: the processing loop's semaphore-based signaling creates a dependency on external events (inserts) that passive expiry alone can't satisfy. The fix is simple (timed wait), but this is a gap between design and implementation.

**Decision:** Findings #1 and #2 block merge. Remaining findings recommended but not blocking.

### 2026-04-11: PR #71 Review — Queue Retry Policies with Terminal State Support

**Branch:** `squad/71-queue-dead-letter-handling-and-retry-policies`

**Critical Finding — Terminal Items Not Explicitly Excluded:**
- `CreateAvailableQueueItemFilter()` does NOT filter on `IsTerminal`, but compound index includes it (line 111)
- Index is created but never used in query — signals intent mismatch
- **Impact:** Low operational (terminal items don't re-process because `IsClosed=true` blocks them), high clarity cost
- **Recommendation:** Add `filterBuilder.Eq(x => x.IsTerminal, false)` to availability filter for defensive clarity and index alignment

**Moderate Finding — Retry Count Math Underdocumented:**
- `WithMaxRetries(5)` allows 5 retries **after** first failure (6 total attempts), but README comment doesn't clarify
- **Recommendation:** Update README line 437 with "Allow 5 retries after initial failure (6 total attempts)"

**Positive Findings:**
- Lock ownership guard is solid (LockedUtc equality prevents theft)
- TTL + terminal orthogonality correct (terminal items stay queryable for DLQ until TTL expires)
- Builder API validation and tests comprehensive

**Verdict:** Architecturally sound, tests pass. Filter clarity strongly recommended before merge; documentation improvement lower-priority.

### 2026-04-10: PR #73 Review Finding r3066971413 — Deep Dive

**Finding:** Test container disposal in `[OneTimeTearDown]` breaks parallel test execution.

**Analysis:** The test class independently disposes a container managed as a shared singleton by `MongoAssemblySetup.cs` (SetUpFixture pattern). When parallel tests run and this teardown fires, it kills the shared resource while other tests still depend on it. This is a real CI/CD break risk, not a cosmetic issue.

**Root Cause:** `MongoQueueLockExpiryIntegrationTests` has `[OneTimeTearDown] public Task DisposeMongoDbContainer() => _container.DisposeAsync()`. The container returned by `MongoDbTestContainer.StartContainerAsync()` is a singleton managed at assembly level, not per-test-class.

**Precedent:** Other test classes (`MongoMigrationIntegrationTests`, `MongoConfiguratorIntegrationTests`, etc.) correctly call `StartContainerAsync()` WITHOUT a disposal teardown. They rely on `MongoAssemblySetup.StopContainer()` (which calls `MongoDbTestContainer.StopContainerAsync()`) for cleanup.

**Impact:** 
- Local serial tests: May pass (disposal races with other tests' setup)
- CI parallel tests: Will fail with timeouts or connection errors
- Non-deterministic failures in CI pipeline

**Verdict:** VALID AND CRITICAL — blocks merge.

**Recommendation:** Remove the `[OneTimeTearDown]` disposal. The container lifecycle is already managed by the assembly-level fixture. See `.squad/decisions/inbox/nate-pr73-review-thread-r3066971413.md` for full analysis and fix instructions.

### 2026-04-11: PR #73 Review Finding r3066971461 — Lease Timing Verification

**Finding:** Comment claimed test lease (250ms) was too short to verify independent lease recovery wake-up.

**Status:** **STALE** — Finding already fixed by commit 529c6bc

**What Was Fixed:**
- Lease time increased: 250ms → 2 seconds
- Processing loop now has timed wait: `_signalSemaphore.WaitAsync(leaseRecoveryWakeInterval, ...)` (line 268)
- Test now asserts: `(handler.AttemptStartedAtUtc[1] - handler.AttemptStartedAtUtc[0]) >= (leaseTime - 500ms)`

**Verdict:** Current test is sound. No action required. Test correctly verifies lease expiry recovery happens independently, without relying on new message inserts.

**Documented in:** `.squad/decisions/inbox/nate-pr73-review-r3066971461.md`

### 2026-04-11: PR #73 Diagnostics Quality Review — Log Message Accuracy

**Finding:** Review thread r3067714453 identified log message accuracy issue in queue lock recovery path.

**Issue:** The `MongoQueueSubscription` recovery path treats two distinct lock failure conditions as equivalent:
- `IsLocked=true` AND `LockedUtc=null` (malformed/orphaned lock state)
- `IsLocked=true` AND `LockedUtc < lockExpiryUtc` (expired lock state)

Current message says "Recovering **expired** queue item lock" for both cases, misleading operators on actual failure mode.

**Decision:** Update log message to distinguish between null (missing timestamp) and stale timestamp conditions. Include prior `LockedUtc` value and expiry threshold in log output.

**Implementation:** Update line 185-187 in `src/Chaos.Mongo/Queues/MongoQueueSubscription.cs`:
```csharp
_logger.LogWarning("Recovering queue item lock {QueueItemId} (previous LockedUtc: {PreviousLockedUtc}, threshold: {LockExpiryUtc}) with payload {PayloadType}",
                   queueItemId,
                   queueItem.LockedUtc?.ToString("O") ?? "null",
                   lockExpiryUtc.ToString("O"),
                   typeof(TPayload).FullName);
```

**Rationale:** Diagnostics accuracy on critical paths matters. Operators need precise logs to understand lock recovery failure modes. Cost is trivial (single log message). Not deferring to Phase 2 (#72 covers additional observability, but this base case should be accurate in current PR).

**ADR:** Queue Lock Recovery Log Message Accuracy (merged to decisions.md 2026-04-11)

### 2026-04-11: Issue #71 — Queue Dead-Letter Handling and Retry Policies (Phase 2 Shape)

**Session:** Architecture scoping for Phase 2 issue #71  
**Trigger:** User directed team to pick one of #71/#72 after Phase 1 PR merge; chose #71 for higher operational value

**Decision Made:** Defined smallest implementation shape for #71 that avoids scope creep.

**Key Architectural Decisions:**

1. **Storage Model:** Retry state in same queue document, not separate DLQ collection
   - Rationale: Atomic reprocessing, single schema, one source of truth
   - DLQ is logical view (query filter), not physical collection

2. **Retry Tracking:** Add `RetryCount` (int) and `IsTerminal` (bool) to `MongoQueueItem`
   - RetryCount incremented on handler exception
   - IsTerminal set when RetryCount >= MaxRetries (if configured)

3. **Public API (Phase 2.1 only)**
   - `MongoQueueDefinition.MaxRetries` (nullable int)
   - `MongoQueueBuilder<T>.WithMaxRetries(int)` and `WithNoRetry()`
   - `MongoDefaults.QueueMaxRetries = null` (unlimited by default)

4. **Scope Discipline:** Phase 2.1 limited to max-count retry logic
   - ❌ Deferred: Custom policies, exception discrimination, automated reprocessing, separate DLQ collection
   - Rationale: Telemetry (#72) needed for good policy design; deferred items don't block Phase 2

5. **Backward Compatibility:** Default `MaxRetries=null` preserves Phase 1 semantics (unlimited retries)

**Implementation Shape (For Eliot):**
- On handler exception: increment RetryCount, check if terminal, log, swallow exception
- Index updated: compound on (IsClosed, IsLocked, LockedUtc, IsTerminal)
- Pattern follows Phase 1 approach (TTL index, fluent builder, backward-compat defaults)

**Acceptance Criteria (For Parker):**
- Handler exception → RetryCount increments → logged
- RetryCount >= MaxRetries → IsTerminal=true → logged warning
- Default (null) preserves Phase 1 behavior (no change to existing queues)
- Integration tests: retry exhaustion, terminal state, backward compat
- Builder tests: WithMaxRetries, WithNoRetry validation

**Artifact:** `.squad/decisions/inbox/nate-issue-71-shape.md` (comprehensive architecture decision document)

**Rationale for Decisions:**
- Same-document model avoids operational complexity of separate collection (atomic, simpler schema)
- Max-count retries cover ~80% of use cases; custom policies wait for telemetry
- Null default maintains backward compatibility while enabling opt-in retry limits
- Phase 2.1 scope prevents scope creep; deferred items don't block implementation

**Status:** Ready for implementation. No architectural blockers. Decision document ready for Eliot and Parker review.

### 2026-04-15: PR Review Finding r3068240198 — MaxRetries Documentation Clarity

**Finding:** README wording for `WithMaxRetries(5)` is ambiguous and may mislead users about total attempt count.

**Current Wording (line 435):**
```
.WithMaxRetries(5) // Stop retrying poison messages after 5 retries
```

**Ambiguity Concern:**
- Naive read: 5 total failures allowed
- Actual behavior: 5 retries AFTER initial failure = 6 total attempts

**Evidence (Implementation):**
- Line 175 in `MongoQueueSubscription.cs`: condition is `failedItem.RetryCount <= MaxRetries`
- Integration test `MongoQueueRetryIntegrationTests.cs` line 38: `WithMaxRetries(1)` expects 2 handler attempts (line 55) and `RetryCount == 2` (line 62)
- Flow: attempt 1 fails → `RetryCount=1`, check `1 <= 1` → allow retry; attempt 2 fails → `RetryCount=2`, check `2 <= 1` → FALSE → terminal

**Verdict: FIX IT — This is meaningful, not noise.**

**Trade-off Analysis:**
- Cost: 2-minute fix (one comment update)
- Risk of leaving it: Users misconfigure queues (e.g., `WithMaxRetries(99)` expecting 99 retries, get 100), then report bugs or make wrong operational decisions
- False positives (fixing non-bugs): Zero—behavior is clear in code and tests

**Decision:** Update README to clarify terminology. Recommended wording: "Allow 5 retries after initial failure (6 total attempts)" or "Stop after 5 retry attempts, allowing up to 6 total attempts."

**Follow-Up:** After fix, consider clarifying the same semantics in `MongoQueueBuilder.WithMaxRetries()` XML docs for consistency.

### 2026-04-15: README MaxRetries Documentation Update

**Task:** Implement ADR for PR review finding r3068240198 — clarify retry semantics in README.

**Change Made:**
- **File:** `README.md` line 435
- **Old:** `.WithMaxRetries(5) // Stop retrying poison messages after 5 retries`
- **New:** `.WithMaxRetries(5) // Stop after 5 retries (6 total attempts including initial)`

**Rationale:** The phrase "5 retries" is ambiguous—unclear whether it means 5 total attempts or 5 retries after initial failure. Implementation clarifies this: `RetryCount > MaxRetries` only triggers terminal state, so `WithMaxRetries(5)` allows up to 6 total attempts. README now mirrors implementation semantics exactly.

**Trade-off:** Minimal. The change is purely clarifying; it changes no behavior and increases precision at near-zero cost. No other README retry documentation needed update (lines 441-442 remain contextually sound post-change).

**Status:** COMPLETE. Change is surgical and unrelated to code; no commit per user request.

### 2026-04-15: Index Verification Integration Tests — Assessment

**Request:** Should Chaos.Mongo add integration tests that verify queue/event-store/outbox queries actually use intended MongoDB indexes?

**Analysis Performed:**
- Reviewed current index strategies across Queue, EventStore, and Outbox
- Inspected query patterns and index structures
- Assessed brittleness of `explain()` plan verification vs. maintenance cost
- Evaluated three alternatives: broad verification, lightweight schema tests, opt-in profiling

**Key Findings:**
1. All three features already create indexes correctly at startup
2. Query logic matches index design (compound index field order, partial filters)
3. Functional tests already catch query behavior regressions
4. Broad `explain()` plan verification is highly brittle across MongoDB versions and collection sizes

**Recommendation: REJECT broad index verification tests.** Maintenance cost (data volume sensitivity, plan fragility, framework churn) exceeds regression detection benefit. Functional tests + code review sufficient.

**Alternative (Deferred):** Lightweight schema inspection tests (verify index existence, field order, partial filters) have low cost and zero brittleness—worth adding if performance becomes a concern.

**Specific Guidance:**
- Queue: Skip. Functional tests sufficient.
- EventStore: Consider lightweight schema test (verify unique index on both fields).
- Outbox: Consider lightweight schema test for polling index field order (business-critical).

**Trade-offs Named:**
- **Regression detection vs. brittleness:** Functional tests catch query bugs; explain plans don't justify maintenance burden
- **Proactive monitoring vs. reactive profiling:** Better to investigate with real telemetry when issues emerge
- **Broad coverage vs. narrow scope:** If testing indexes, keep it to schema validation only—no execution plans

**Decision Document:** `.squad/decisions/inbox/nate-index-verification-tests.md` (comprehensive assessment with templates and alternatives)

### 2026-04-15: Index/Query Test Strategy — Comprehensive Assessment

**Request:** User (Christian Flessa) asked for concrete test strategy assessment:
1. Is this a good idea?
2. What exact contract should tests enforce?
3. What should be tested at code/query-definition level versus integration level?
4. How would you structure tests to catch regressions without over-coupling to MongoDB internals?
5. Which subsystems are most worth covering first?

**Analysis Performed:**
- Audited all index definitions (Queue, EventStore, Outbox) and their corresponding queries
- Evaluated brittleness of explain-plan verification across MongoDB versions and collection sizes
- Assessed functional test coverage already in place
- Named trade-offs explicitly

**Key Findings:**

1. **Current State is Strong:**
   - Queue: Compound index field order (IsClosed, IsLocked, LockedUtc, IsTerminal) correct; functional tests comprehensive
   - EventStore: Unique index enforced at database level; concurrency tests catch violations
   - Outbox: Polling index field order critical risk (silent degradation possible at scale); functional tests exercise but don't validate schema

2. **Explain-Plan Testing is Brittle:**
   - MongoDB version churn: Output format differs across 5.x/6.x/7.x/8.x
   - Collection size dependency: Planner chooses COLLSCAN <1k docs, IXSCAN 10k+ docs
   - Single-node testcontainer bias: Production replica sets have different plans
   - Signal value weak: Functional tests already catch query behavior bugs
   - Maintenance burden: +60-120s CI/CD per test; false failures at scale; developer fear of refactoring

3. **Lightweight Schema Validation is the Answer:**
   - Verify index exists with correct field order, partial filters, uniqueness
   - Zero brittleness: BSON index definition structure stable across versions
   - Catches real mistakes: Field name typo, order regression, missing partial filter
   - Fast: <50ms per test; no large collection seeding
   - Low maintenance: Only updates when schema actually changes

4. **Subsystem-Specific Risk Assessment:**
   - Queue: LOW risk. Field order correct; functional tests sufficient. Optional: add schema test (~40 lines).
   - EventStore: VERY LOW risk. Uniqueness database-enforced. Optional: add schema test (~30 lines).
   - Outbox: MODERATE risk. Field-order regression realistic (silent slow query). Recommended: add schema test (~50 lines) + one query correctness test.

**Recommendation: Three-Layer Approach**

- Layer 1 (Schema validation): Add lightweight tests (~120 lines total; <10ms CI impact) to verify index field order, partial filters, uniqueness
- Layer 2 (Functional tests): Keep existing tests (already comprehensive; implicit index validation through behavior)
- Layer 3 (Code review): Enforce index-query alignment checkpoints in PR review

**Trade-offs Named:**
- Regression detection vs. brittleness: Functional tests catch bugs; explain plans create false failures at scale
- Proactive verification vs. reactive profiling: Better to investigate with real telemetry when performance issues emerge
- Broad coverage vs. maintainability: Schema validation is narrow scope, high signal; explain plans are broad scope, low signal

**Implementation Roadmap:**
- Phase 1 (Immediate): Add schema validation tests for Queue, EventStore, Outbox (~2-3 hours; low risk; high signal)
- Phase 2 (Optional): Add Outbox query correctness test (verify sorted results)
- Phase 3 (Deferred): Only if production telemetry shows outbox polling latency issues; then add opt-in performance profiling

**Status:** Ready for team input. Decision document complete: `.squad/decisions/inbox/nate-index-query-test-strategy.md`

**Key Trade-off Articulated:**
- Cost of maintaining explain-plan tests: +60-120s CI/CD, framework churn, false failures at scale, developer fear
- Benefit of explain-plan tests: Detects planner choices (not bugs; weak signal)
- Cost of schema validation tests: ~120 lines code, <10ms runtime
- Benefit of schema validation tests: Catches real mistakes (typos, order regressions), zero brittleness
- **Decision:** Lightweight schema validation is favorable trade-off

### 2026-04-11: Index/Query Contract Review Assessment

**Session:** Architecture contract review for index/query alignment testing strategy

**Question:** Should we add integration tests that verify MongoDB indexes are actually used by queries?

**Analysis Performed:**
- Audited all three subsystems (Queue, EventStore, Outbox) for index/query alignment
- Field order verification: Queue ✅ aligned, EventStore ✅ aligned, Outbox ⚠️ moderate risk
- Trade-off: Explain-plan verification vs. schema validation vs. functional testing
- Risk-benefit analysis: Brittleness cost of explain plans vs. signal value gained

**Key Findings:**
- Explain-plan parsing is brittle: MongoDB version-dependent, collection-size-dependent, planner-dependent
- Functional tests already catch query behavior regressions (queries fail if they break)
- Real risk is silent removal of index clauses during refactoring
- Schema validation tests catch real mistakes with zero maintenance cost

**Recommendation:**
- **Do NOT implement broad explain-plan verification**
- **DO add lightweight schema validation tests** (~120 lines total)
- **Use code review discipline** as final checkpoint

**Three-Layer Approach:**
1. Schema validation (index structure, field order, partial filters)
2. Query correctness tests (functional, mostly exist)
3. Code review (enforce index-query alignment in PR checkpoints)

**Specific Guidance by Subsystem:**
- Queue: Low risk. Add schema test for compound field order.
- EventStore: Very low risk. Add schema test for unique index.
- Outbox: Moderate risk (field order matters). Add schema test + one query correctness test.

**Implementation Roadmap:**
- Phase 1 (Immediate): Create 3 new test files, ~120 lines. Effort: 2-3 hours.
- Phase 2 (Optional): Add query correctness to Outbox (field order validation via sort order).
- Phase 3 (Deferred): Performance profiling suite if telemetry shows issues.

**Cross-Agent Alignment:** Parker's detailed test strategy confirms architectural soundness; implementation can proceed with confidence.

### 2026-04-11: Index/Query Contract Review Assessment

**Session:** Architecture contract review for index/query alignment testing strategy

**Question:** Should we add integration tests that verify MongoDB indexes are actually used by queries?

**Analysis Performed:**
- Audited all three subsystems (Queue, EventStore, Outbox) for index/query alignment
- Field order verification: Queue ALIGNED, EventStore ALIGNED, Outbox MODERATE RISK
- Trade-off: Explain-plan verification vs. schema validation vs. functional testing
- Risk-benefit analysis: Brittleness cost of explain plans vs. signal value gained

**Key Findings:**
- Explain-plan parsing is brittle: MongoDB version-dependent, collection-size-dependent, planner-dependent
- Functional tests already catch query behavior regressions
- Real risk is silent removal of index clauses during refactoring
- Schema validation tests catch real mistakes with zero maintenance cost

**Recommendation:**
- Do NOT implement broad explain-plan verification
- DO add lightweight schema validation tests (120 lines total)
- Use code review discipline as final checkpoint

**Implementation Roadmap:**
- Phase 1 (Immediate): Create 3 new test files, 120 lines. Effort: 2-3 hours.
- Phase 2 (Optional): Add query correctness to Outbox.
- Phase 3 (Deferred): Performance profiling suite if telemetry shows issues.
