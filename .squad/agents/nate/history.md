# Nate — History

## Core Context

- **Project:** A .NET MongoDB library providing migrations, event store, transactional outbox, queues, and helpers published as multiple NuGet packages
- **Role:** Lead
- **Joined:** 2026-04-09T19:19:45.616Z

## Learnings

<!-- Append learnings below -->

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
