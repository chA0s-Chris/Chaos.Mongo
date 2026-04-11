# Nate — Issue #71 Scoping Session (2026-04-11)

## Task
Define the smallest implementation shape for issue #71 (Queue Dead-Letter Handling and Retry Policies) that is safe to build now. Decide the retry/DLQ storage model and minimal public API additions. Create a decision note for the implementation team.

## Outcome

### Decision Document
Created **`.squad/decisions/inbox/nate-issue-71-shape.md`** — comprehensive Phase 2 architecture shape.

### Key Architectural Decisions

**1. Storage Model: Same-Document Retry State**
- Retry count lives in the main queue document (`MongoQueueItem.RetryCount`)
- Dead-letter is a **logical view**, not a separate collection
- Rationale: Simpler schema, atomic reprocessing, one source of truth for observability

**2. Retry Tracking Fields**
- `Int32 RetryCount` — incremented on handler exception
- `Boolean IsTerminal` — set when RetryCount >= MaxRetries
- Both optional from query perspective (backward compatible)

**3. Configuration API**
- `MongoQueueDefinition.MaxRetries` (nullable int, default null)
- `MongoQueueBuilder<T>.WithMaxRetries(int)` — validate > 0
- `MongoQueueBuilder<T>.WithNoRetry()` — shorthand for MaxRetries=0
- `MongoDefaults.QueueMaxRetries = null` (unlimited, backward compatible)

**4. Scope Discipline: Phase 2.1 Minimal Slice**
- ✅ In scope: Max-count retry logic, terminal state marking
- ❌ Deferred (Phase 2.2+): Custom policies, exception discrimination, automated reprocessing

**5. Processing Logic (For Eliot)**
- On handler exception: increment RetryCount, check if terminal, log, swallow exception
- On handler success: leave RetryCount as-is (don't reset)
- Index updated: compound on (IsClosed, IsLocked, LockedUtc, IsTerminal)

### Acceptance Criteria (For Parker)
- Handler throws → RetryCount increments → logged with threshold
- RetryCount >= MaxRetries → IsTerminal=true → logged warning
- Default (null) preserves Phase 1 behavior (no change to existing queues)
- New integration tests: retry exhaustion, terminal state, backward compat
- New builder tests: WithMaxRetries, WithNoRetry validation

### Trade-offs Named
| Decision | Rationale |
|----------|-----------|
| Same document vs. separate DLQ | Atomic, simpler schema, one source of truth. DLQ physical sep. deferred. |
| Max-count only | Covers 80% of use cases. Exponential backoff/jitter wait for telemetry (#72). |
| Null default (unlimited) | Backward compatible. Operators opt-in to limits. |
| No handler-level changes | Retry logic substrate-level, not handler interface. Handler continues unchanged. |

### Implementation Readiness
- ✅ Architectural shape is solid, no contradictions found
- ✅ Index model follows Phase 1 pattern (issue #10)
- ✅ Fluent builder pattern established (extensible)
- ✅ Backward compatible (defaults preserve Phase 1 behavior)
- ✅ Deferred scope keeps phase focused (prevents scope creep)

### Next Steps
1. Share decision document with Eliot (implementation) and Parker (testing)
2. Validate implementation feasibility (no major hurdles expected)
3. Merge decision to `.squad/decisions.md` once approved
4. Implementation phase: Eliot leads, Parker test strategy pre-planned

## Decisions Made
- ✅ Storage model: same queue document (not separate DLQ collection)
- ✅ Public API shape: 3 builder methods, 2 item fields, 1 definition property
- ✅ Scope: max-count retries only; custom policies deferred
- ✅ Backward compatibility: default MaxRetries=null preserves Phase 1 behavior
- ✅ Test strategy: integration tests (retry exhaustion), builder tests (config), backward compat

## Context Links
- Issue: #71
- Branch: `squad/71-queue-dead-letter-handling-and-retry-policies`
- Decision artifact: `.squad/decisions/inbox/nate-issue-71-shape.md`
- Related: Phase 1 ADR (`.squad/decisions.md`), issue #10 implementation pattern
