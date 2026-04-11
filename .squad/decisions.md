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

## Team Directives

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
