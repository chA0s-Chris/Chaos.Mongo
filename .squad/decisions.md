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

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
