# Eliot — History

## Core Context

- **Project:** A .NET MongoDB library providing migrations, event store, transactional outbox, queues, and helpers published as multiple NuGet packages
- **Role:** Library Dev
- **Joined:** 2026-04-09T19:19:45.616Z

**Queue Architecture Summary:**
- `MongoQueueItem`: State machine with `IsClosed`, `IsLocked`, `LockedUtc`, `ClosedUtc` fields
- Processing: Dual-mechanism (change stream + polling loop with backoff)
- Original issues: Stuck locks on handler failure (Issue #9), unbounded closed-item accumulation (Issue #10)
- Index strategy: Compound index on `(IsClosed, IsLocked, LockedUtc)` for active items; TTL index on `ClosedUtc` for cleanup

**Key Queue Implementation Files:**
- `MongoQueueSubscription.cs`: Core processing engine (ProcessQueueItemsAsync, ProcessQueueItemAsync, lock recovery)
- `MongoQueueItem.cs`: Schema definition with state fields
- `MongoQueueDefinition.cs`: Configuration holder (passed to builder)
- `MongoQueueBuilder.cs`: DI registration and fluent configuration
- Tests: `MongoQueueTests.cs` (unit), `MongoQueueLockExpiryIntegrationTests.cs`, `MongoQueueRetentionIntegrationTests.cs` (integration)

**Configuration Defaults (MongoDefaults.cs):**
- `QueueLockLeaseTime` = 5 minutes (for lock recovery)
- `QueueClosedItemRetention` = 1 hour (for TTL cleanup)
- `AutoStartSubscription` = false (opt-in)
- `QueryLimit` = 1 (single-item-at-a-time processing)

**API Pattern:**
- Fluent builder: `MongoBuilder.WithQueue<T>()` returns `MongoQueueBuilder<T>` with chainable `.With*()` methods
- New features follow builder pattern (e.g., `.WithLockLeaseTime()`, `.WithClosedItemRetention()`, `.WithImmediateDelete()`)
- All new fields optional with sensible defaults; backward compatible

## Recent Work

### 2026-04-10: Team Orchestration & Implementation Planning

**Session:** Multi-agent coordination (Nate architect, Eliot dev, Parker tester)

**Coordinated Outcomes:**
- Nate's passive lease expiry decision validated by Parker's test coverage gap analysis
- Eliot's lock reset logic in catch block confirmed by Parker's race condition tests
- TTL index strategy aligned across all three agents
- Comprehensive test plan (Tiers 1–4) supports TDD implementation

**Team Consensus:**
- Lock recovery: Passive query-time filter (simplest, no background job coordination)
- Retention cleanup: MongoDB TTL native (reliable, configurable)
- Configuration: All new fields optional, backward compatible
- Retry counting: Deferred Phase 2+ (Parker flagged as out-of-scope for now)

**Implementation Road Map:**
1. **PR #9:** Lock lease expiry (Eliot lead, Parker writes lock expiry tests)
2. **PR #10:** TTL retention (Eliot lead, Parker writes retention tests)
3. **Test Infrastructure:** Parker to create three new test files before coding starts (TDD approach)

**Cross-File Impact:**
- Compound index replaces partial index (Nate, Eliot aligned)
- TTL index with partial filter on IsClosed=true (Parker validates in tests)
- Builder API extended with fluent methods (Eliot implements, Parker tests config flow)

**Status:** Development ready. Clear ownership, locked API contract, test fixtures prepared.

### 2026-04-11: Issue #9 Queue Lock Lease Recovery

**Queue lease recovery pattern:**
- `MongoQueueDefinition.LockLeaseTime` carries per-queue lease configuration, defaulted in `MongoQueueBuilder<T>` from `MongoDefaults.QueueLockLeaseTime`.
- `MongoQueueSubscription` now treats open items as processable when they are unlocked or when `IsLocked=true` and `LockedUtc` is missing or older than `now - LockLeaseTime`.
- Lease recovery stays passive: no unlock write on failure, no scavenger job, and the polling loop reclaims expired items by reacquiring the lock atomically.

**Indexing for queue recovery:**
- Queue subscriptions now create a compound index on `(IsClosed, IsLocked, LockedUtc)` instead of the old partial unlocked-items index.
- Recovery tests live in `tests/Chaos.Mongo.Tests/Integration/Queues/MongoQueueLockExpiryIntegrationTests.cs`.

**PR #73 Blocker Fixes:**
- Queue wake-up behavior: Passive lease recovery still needs a periodic wake-up after the queue goes idle, otherwise expired locks are only retried when a new insert or self-signal arrives. `MongoQueueSubscription` now re-checks the queue at least once per second while idle, capped by the configured lease time, so expired locks wake processing without a new publish.
- Lock ownership guard: Closing a processed item must be conditional on the same `LockedUtc` value that was written when this consumer acquired the lock. Guarding the final close/unlock update with `(Id, IsClosed=false, IsLocked=true, LockedUtc=acquiredLockUtc)` prevents a slow consumer from clearing a replacement lock after lease expiry.
- Regression coverage: Lease-expiry recovery test now proves the retry happens after the lease window without another insert. Added a two-consumer integration test that verifies the original handler cannot clear the replacement consumer's renewed lock.

**Status:** PR #73 implementation complete and submitted for user review. Awaiting approval before merge to main.

### 2026-04-11: Issue #10 Queue Closed-Item Retention (TTL-Based)

**Session:** Issue #10 implementation and validation  
**Branch:** `squad/10-remove-old-queue-items`  
**Status:** Completed (awaiting user review for merge)

**Implementation Pattern:**
- Single managed TTL index per queue collection (`IX_Queue_ClosedUtc_TTL`) for deterministic retention
- Index reconciliation on every subscription start enables policy changes without manual cleanup
- When `ClosedItemRetention` is null, items deleted immediately; TTL index dropped
- Default retention: 1 hour (configurable)

**API Changes:**
- `MongoQueueBuilder<T>.WithClosedItemRetention(TimeSpan)` — set custom retention period
- `MongoQueueBuilder<T>.WithImmediateDelete()` — enable immediate deletion
- `MongoQueueDefinition.ClosedItemRetention` property (nullable TimeSpan)
- `MongoDefaults.QueueClosedItemRetention` = 1 hour

**Code Changes:**
- Extended `MongoQueueDefinition` with retention configuration
- Updated `MongoQueueBuilder<T>` with fluent methods
- Modified `MongoQueueSubscription.EnsureIndexesAsync()` for TTL index lifecycle
- Updated queue processing for immediate delete when retention null
- Updated README with retention documentation
- Extended builder and integration test coverage

**Test Coverage:**
- Builder configuration validation
- TTL index creation/management (default and custom retention)
- Immediate delete behavior (null retention)
- Same-collection policy reconciliation (multiple subscriptions, different policies)
- No regression in existing queue processing

**Status:** Implementation complete, all tests passing, ready for user/team review and merge.

### 2026-04-11: PR #74 Follow-up — Direct-Construction Default & Cancellation Hardening

**Session:** PR #74 follow-up validation and hardening  
**Branch:** `squad/10-remove-old-queue-items`  
**Status:** Complete (ready for user review and merge)  
**Commit:** `fa183f1`

**Work Completed:**

1. **Fixed Direct-Construction Default for `MongoQueueDefinition.ClosedItemRetention`**
   - Issue: Direct construction bypassed builder default, leaving retention unset
   - Change: Added default to record property initializer (`= MongoDefaults.QueueClosedItemRetention`)
   - Outcome: Direct construction now matches documented 1-hour retention without forcing every caller to set it

2. **Hardened `MongoQueueRetentionIntegrationTests` Cancellation Token Flow**
   - Issue: Retention polling helpers (`ListAsync`, `ToListAsync`, `FirstOrDefaultAsync`) not respecting timeout cancellation tokens
   - Change: Threaded cancellation token through all retention polling helper calls
   - Outcome: Tests now honor timeout semantics, preventing optional hang scenarios in production

**Validation:**
- ✅ Direct-construction test validates default applies correctly
- ✅ Focused queue tests passed
- ✅ Full solution `dotnet test Chaos.Mongo.slnx --no-restore` passed
- ✅ Existing test `MongoQueueBuilderTests.MongoQueueDefinition_WithoutExplicitClosedItemRetention_UsesDefaultRetention` validates default contract
- ✅ No regression in existing queue behavior

**Outcome:** PR #74 follow-up complete and ready for user review/merge. All changes committed to `squad/10-remove-old-queue-items` branch.

### 2026-04-11: PR #77 Final Fixes — EventStore, Outbox, Queue Tests

**Session:** Address three valid review notes from 2026-04-11 Copilot review pass  
**Status:** Complete (build passing: 427 tests, 0 failures)

**Work Completed:**

1. **EventStore query-contract serialization bootstrap** (`MongoEventStoreQueryContractTests.cs`)
   - Added explicit call to `MongoEventStoreSerializationSetup.EnsureGuidSerializer()` and `RegisterClassMaps(options)` before BSON rendering
   - Merge-blocking issue: isolated test runs now use production BSON configuration instead of failing on Guid serialization

2. **Outbox processor integration test cleanup guarantee** (`OutboxIndexContractIntegrationTests.cs`)
   - Wrapped processor `StopAsync()` in `finally` block to ensure cleanup even on wait timeout
   - Prevents background polling loop from leaking into subsequent tests

3. **Queue lease-recovery contract assertion coupling** (`MongoQueueSubscriptionTests.cs`)
   - Tightened assertion to verify `LockedUtc < now - leaseTime` is coupled with `IsLocked == true` in the same filter branch
   - Strengthens contract coverage to prevent false positives from loose tree-wide checks

**Verification:** ✅ Build clean. All three review notes addressed. Test suite passing with no regressions.

## Learnings

- Queue availability filtering in `src/Chaos.Mongo/Queues/MongoQueueSubscription.cs` should treat missing `IsTerminal` as non-terminal for backward compatibility with legacy queue documents, using `IsTerminal != true` semantics for active-work queries.
- TTL retention on queue items must stay backward compatible too, but MongoDB partial index filters reject `$ne` and `$exists: false`; the working representation is `IsTerminal: { $in: [false, null] }`, which still includes legacy documents with no `IsTerminal` field.
- Review coverage for these behaviors lives in `tests/Chaos.Mongo.Tests/Queues/MongoQueueSubscriptionTests.cs` and `tests/Chaos.Mongo.Tests/Integration/Queues/MongoQueueRetentionIntegrationTests.cs`, and the assertions should validate compatible behavior instead of one literal BSON shape.
- EventStore query-contract tests that render BSON must explicitly run `MongoEventStoreSerializationSetup.EnsureGuidSerializer()` plus `RegisterClassMaps(options)` in the test path; otherwise isolated runs can fail on Guid serialization or depend on prior test order. The affected contract coverage lives in `tests/Chaos.Mongo.EventStore.Tests/MongoEventStoreQueryContractTests.cs`.
- Integration tests that start `OutboxProcessor` should always stop it from a `finally` block so timed-out waits do not leak the background polling loop into later tests. The current regression point is `tests/Chaos.Mongo.Outbox.Tests/Integration/OutboxIndexContractIntegrationTests.cs`.

## Recent Work (Historical Duplicates Below — See Above for Latest Work)
