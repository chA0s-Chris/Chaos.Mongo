# Eliot — History

## Core Context

- **Project:** A .NET MongoDB library providing migrations, event store, transactional outbox, queues, and helpers published as multiple NuGet packages
- **Role:** Library Dev
- **Joined:** 2026-04-09T19:19:45.616Z

## Learnings

### Queue Implementation Architecture (2025-01-15)

**MongoQueueItem state machine:**
- Three key fields: `IsClosed` (processed?), `IsLocked` (processing?), with timestamps `LockedUtc`, `ClosedUtc`
- Processing filter: `IsClosed=false AND IsLocked=false` (finds unprocessed, unlocked items)
- Lock acquired: `FindOneAndUpdateAsync()` sets `IsLocked=true, LockedUtc=now` before handler runs
- Unlock on success: `IsClosed=true, ClosedUtc=now, IsLocked=false, Unset(LockedUtc)`
- **Bug on failure:** Exception caught silently, lock never released → item stuck forever

**Queue subscription pattern:**
- Dual-mechanism: MongoDB change stream watches inserts in real-time; polling loop processes + handles misses
- Change stream signals via `_signalSemaphore.Release()` when inserts detected
- Polling loop queries `IsClosed=false AND IsLocked=false`, processes up to `QueryLimit` items (default 1)
- After batch, checks if more unprocessed items exist, self-signals if needed
- Default `QueryLimit=1` means single-item-at-a-time processing (very conservative)

**Key files for queue work:**
- `MongoQueueSubscription.cs`: Core processing engine (ProcessQueueItemsAsync, ProcessQueueItemAsync)
- `MongoQueueItem.cs`: Schema definition
- `MongoQueueDefinition.cs`: Configuration holder (passed to builder)
- `MongoQueueBuilder.cs`: DI registration and fluent configuration
- Tests: `MongoQueueTests.cs` (unit), `MongoQueueIntegrationTests.cs` (integration with Testcontainers)

**No cleanup or timeout handling exists.** Items processed successfully stay forever (`IsClosed=true` with `ClosedUtc` set but unused). Stuck locks from handler failures never expire.

**Default configuration in MongoDefaults.cs:**
- `AutoStartSubscription = false` (opt-in)
- `QueryLimit = 1` (process 1 item per loop iteration)
- `LockLeaseTime = 5 min` (for distributed locks, NOT queue item locks — unrelated)

**Index strategy:**
- `EnsureIndexesAsync()` creates partial index: `{IsClosed: asc, IsLocked: asc}` filtered to `IsClosed=false AND IsLocked=false`
- Only indexes unprocessed items for efficient polling
- TTL indexes can be created in same method without breaking existing setup

### Issues #9 and #10 — Root Cause (2025-01-15)

**Issue #9 (stuck locks):**
- Handler exception on line 177 (MongoQueueSubscription.cs) caught silently (line 203)
- Lock state never reset → item with `IsLocked=true` ignored forever
- Code path: `ProcessQueueItemAsync()` → exception in `HandlePayloadAsync()` → catch block → no update
- Solution: Reset lock on failure (set `IsLocked=false, Unset(LockedUtc)`) to allow retry on next scan

**Issue #10 (old items):**
- No deletion logic anywhere in queue system
- Processed items (IsClosed=true) stay in DB indefinitely
- `ClosedUtc` timestamp exists but never used
- Solution: Create TTL index on `ClosedUtc` with configurable retention (e.g., 7 days default)

**Both issues should be addressed in a single PR** because they share configuration pattern, file changes, and test scenarios.

### API Gaps Identified (2025-01-15)

**New configuration needed:**
- `LockTimeoutSeconds` (when to consider lock "stuck" and safe to reset)
- `ProcessedItemRetentionSeconds` (how long to keep closed items before TTL cleanup)
- Fluent builder methods: `WithLockTimeout()`, `WithProcessedItemRetention()`
- Defaults: 5 min lock timeout, 7 days retention (conservative, configurable)

**Backward compatibility:**
- All new fields optional (nullable)
- No breaking changes to `IMongoQueue<T>` or handler contract
- Existing code works unchanged (just keeps old behavior: stuck locks, item accumulation)

### Secondary Issues Found (2025-01-15)

1. **Race condition in `ProcessQueueItemsAsync` (line 263-264):** Count check between batch processing and self-signal could miss items during high throughput. Low impact, out of scope for #9/#10.

2. **Lock expiry without retry limits:** Stuck items unlock but no backoff or "dead letter" collection. Acceptable for now; handlers can implement retry semantics themselves.

3. **Collection naming weak:** Uses `PayloadType.Name` (not FullName), collision risk if two namespaced types have same name. Out of scope, minor.

4. **No durable lock detection for long-running handlers:** If handler takes >5 min and we unlock it, two processors could run simultaneously. Acceptable per "at-least-once" semantics.

### Testing Patterns (2025-01-15)

**Integration tests use Testcontainers.MongoDb:**
- Each test gets unique DB name: `$"QueueTest_{Guid.NewGuid():N}"`
- Handler helpers (TestPayloadHandler, AnotherPayloadHandler) track processed payloads
- `handler.WaitForMessages(count, timeout)` polls for completion
- Tests verify both auto-start and manual start modes
- Cleanup: Start/stop hosted services properly, dispose queue

### Testing Patterns (2025-01-15)

**Integration tests use Testcontainers.MongoDb:**
- Each test gets unique DB name: `$"QueueTest_{Guid.NewGuid():N}"`
- Handler helpers (TestPayloadHandler, AnotherPayloadHandler) track processed payloads
- `handler.WaitForMessages(count, timeout)` polls for completion
- Tests verify both auto-start and manual start modes
- Cleanup: Start/stop hosted services properly, dispose queue

**Test structure:**
- Arrange: Build services, publish messages (often BEFORE starting subscription)
- Act: Start subscription, wait for processing
- Assert: Verify processed items, queue state
- Cleanup: Stop services gracefully

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

### 2026-04-11: Issue #9 Queue Lock Lease Recovery

**Queue lease recovery pattern:**
- `MongoQueueDefinition.LockLeaseTime` carries per-queue lease configuration, defaulted in `MongoQueueBuilder<T>` from `MongoDefaults.QueueLockLeaseTime`.
- `MongoQueueSubscription` now treats open items as processable when they are unlocked or when `IsLocked=true` and `LockedUtc` is missing or older than `now - LockLeaseTime`.
- Lease recovery stays passive: no unlock write on failure, no scavenger job, and the polling loop reclaims expired items by reacquiring the lock atomically.

**Indexing for queue recovery:**
- Queue subscriptions now create a compound index on `(IsClosed, IsLocked, LockedUtc)` instead of the old partial unlocked-items index.
- Recovery tests live in `tests/Chaos.Mongo.Tests/Integration/Queues/MongoQueueLockExpiryIntegrationTests.cs`.

**User preference:**
- For issue work, use the dedicated issue branch and leave changes uncommitted for review.

### 2026-04-11: Post-Session Orchestration (Scribe Consolidation)

**Session Work:**
- Eliot completed Issue #9 implementation on `squad/9-queue-resilience` branch (uncommitted for review)
- Decisions inbox merged: captured Eliot's implementation note and user directives on documentation and branch discipline
- Orchestration log created at 2026-04-10T22:01:46Z
- Session log created with Issue #9 summary and next steps

**Directives Added to decisions.md:**
- Keep feature documentation current with code changes
- Use dedicated issue branches with uncommitted changes for review

**Status:** Awaiting user review before merge to main. Phase 2 (Issue #10 TTL retention) ready to begin after approval.
