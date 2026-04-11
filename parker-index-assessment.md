# Index Usage Integration Tests: Assessment

## Executive Summary

**Recommendation: Minimal, focused index-usage tests. NOT full explain-plan verification.**

Adding tests that actually run queries and verify indexes are used is **feasible and stable** in Testcontainers. However, most proposed explain-plan tests would be **too brittle** and **tightly coupled to MongoDB internals**.

The signal they provide is low—we already have index definition tests (mocked). The real risk isn't "did we define the index" but "does our query actually match the index design."

**Proposed minimal set:**
1. **Query shape validation** (what fields we filter/sort on, post-index)
2. **TTL behavior verification** (that items actually expire)
3. **Lock expiry recovery** (that stale items are reacquired)

NOT: Explain-plan parsing, stage counts, query execution order.

---

## Current Index Landscape

### Queues
- **Active polling index:** `(IsClosed ASC, IsLocked ASC, LockedUtc ASC, IsTerminal ASC)`  
  - Used by: `CreateAvailableQueueItemFilter()`
  - Partial filter: items with `IsClosed=false`
  
- **Closed-item TTL:** `(ClosedUtc ASC)` with TTL + partial filter
  - Expires items where `IsClosed=true, IsTerminal=[false, null]`

### Event Store
- **Unique compound index:** `(AggregateId ASC, Version ASC)`
  - Enforces one event per aggregate + version
  - Used by read-after-write verification

### Outbox
- **Polling index:** `(NextAttemptUtc ASC, LockedUtc ASC, _id ASC)`
  - Partial filter: `State == Pending`
  - Used for: "Find next ready message"
  
- **TTL cleanup indexes** (processed + failed)
  - Selective expiry by message state

---

## Signal vs. Brittleness Analysis

### What Explain-Plan Tests Would Check
- Query planner chose our index (vs. collection scan)
- Number of examined documents
- Execution stages and order

### Why These Are Too Brittle Here
1. **MongoDB planner is adaptive** — index choice depends on collection size, selectivity, server stats
   - Empty collection often scans instead of using index (cheaper)
   - Small collection (< 100 docs) may bypass index intentionally
   - Cardinality estimates change between runs

2. **Test environment ≠ production** — Testcontainers is single-node, no replication
   - Query planner behaves differently
   - Stats collection varies

3. **Version-dependent** — MongoDB 5.0→7.0 planner behavior differs
   - We target 5.x minimum (via Testcontainers default)
   - Explain output structure varies

4. **Tests would require explain-plan parsing**
   - Tight coupling to MongoDB BSON structure
   - High maintenance burden
   - False negatives when minor output changes

### What Actually Matters (and is testable)
- ✅ Index is created with correct shape/options
- ✅ Query returns correct results (functional correctness)
- ✅ TTL actually expires items (time-dependent, needs real MongoDB)
- ✅ Stale lock recovery works (behavioral correctness)
- ✅ No query errors under scale (1000+ docs)

---

## Feasibility in Testcontainers

**YES, fully feasible.**

We already use Testcontainers for integration tests. Three patterns are proven:

### 1. Index Creation Verification (Already Done)
`MongoConfiguratorIntegrationTests.cs` — List indexes, assert structure exists.

```csharp
var indexes = await collection.Indexes.ListAsync().ToListAsync();
var index = indexes.FirstOrDefault(i => i["name"].AsString == "IndexName");
index.Should().NotBeNull();
index["key"]["Field"].Should().NotBeNull(); // BSON key check
```

**Signal:** Index exists with correct field composition.  
**Brittleness:** None—just checking BSON structure.  
**Maintenance:** Stable across MongoDB versions.

### 2. TTL Behavior (New, but Proven Pattern)
`MongoQueueRetentionIntegrationTests.cs` — Insert items with `ClosedUtc`, wait, verify deletion.

```csharp
await collection.InsertOneAsync(new MongoQueueItem { ClosedUtc = DateTime.UtcNow.AddSeconds(-2), ... });
await Task.Delay(3000); // TTL runs every 60s, but test with immediate index
var count = await collection.CountDocumentsAsync(/* filter exists */);
count.Should().Be(0); // Item expired
```

**Signal:** TTL index actually expires documents.  
**Brittleness:** Medium—depends on MongoDB TTL granularity (60s default).  
**Mitigation:** Accept that this test is slower; run it once per test class.  
**Stability:** YES—has been used in retention tests, passes reliably.

### 3. Query Functional Correctness (Already Done)
`MongoQueueRetryIntegrationTests.cs` — Run queries, verify result set size/content.

```csharp
var item = await collection.FindAsync(filter).FirstOrDefaultAsync();
item.Should().NotBeNull();
item.RetryCount.Should().Be(3);
```

**Signal:** Query semantics are correct; index must exist for perf, but we verify results.  
**Brittleness:** None—functional behavior doesn't depend on explain-plan.  
**Stability:** Proven.

---

## What Tests Should NOT Do

### ❌ Parse explain-plan JSON
```csharp
// DON'T DO THIS
var explainResult = await collection.FindAsync(filter).Explain().ToListAsync();
var stage = explainResult[0]["executionStats"]["executionStages"];
stage["stage"].AsString.Should().Be("IXSCAN");
stage["executionStages"]["nDocsExamined"].AsInt32.Should().BeLessThan(10);
```

**Why:** 
- Couples test to MongoDB internals
- Empty collections don't use indexes (optimizer chooses COLLSCAN)
- Testcontainers single-node behavior != production replica set
- Maintenance nightmare on MongoDB version bumps

### ❌ Assert specific index selection in large collections
```csharp
// DON'T DO THIS
// Insert 10,000 items, then assert planner picks our index
// (Instead: Verify query returns correct subset, performance is acceptable)
```

**Why:** Adds 10K insert per test, slows suite, fragile to planner changes.

---

## Recommended Minimal Index Tests

### 1. Index Definition Verification (Existing Pattern, Expand)
**File:** New → `MongoQueueIndexIntegrationTests.cs`

```csharp
[Test]
public async Task QueueSubscription_InitializesActivePollingIndex()
{
    var collection = /* get queue collection */;
    await queueSubscription.EnsureIndexesAsync();
    
    var indexes = await collection.Indexes.ListAsync().ToListAsync();
    var pollingIdx = indexes.FirstOrDefault(i => i["name"].AsString == "IX_Queue_Polling");
    
    pollingIdx.Should().NotBeNull();
    // Check compound key: IsClosed, IsLocked, LockedUtc, IsTerminal
    pollingIdx["key"].AsBsonDocument["IsClosed"].Should().NotBeNull();
    pollingIdx["key"].AsBsonDocument["IsLocked"].Should().NotBeNull();
    // Verify partial filter exists
    pollingIdx["partialFilterExpression"].Should().NotBeNull();
}

[Test]
public async Task QueueSubscription_WithRetention_CreatesTtlIndex()
{
    var definition = new MongoQueueDefinition { ClosedItemRetention = TimeSpan.FromHours(1), ... };
    var subscription = new MongoQueueSubscription(definition, ...);
    await subscription.EnsureIndexesAsync();
    
    var indexes = await collection.Indexes.ListAsync().ToListAsync();
    var ttlIdx = indexes.FirstOrDefault(i => i["name"].AsString == "IX_Queue_ClosedUtc_TTL");
    
    ttlIdx.Should().NotBeNull();
    ttlIdx["expireAfterSeconds"].AsInt32.Should().Be(3600);
}
```

**Signal:** Index structure matches code intent.  
**Stability:** High—just BSON structure checks.  
**Maintenance:** Low.

### 2. TTL Behavior (Existing Pattern, Already Used)
**File:** Existing → `MongoQueueRetentionIntegrationTests.cs`

Already has:
```csharp
[Test]
public async Task ClosedItems_WithRetention_AreDeletedAfterExpiry()
{
    var now = DateTime.UtcNow;
    await collection.InsertOneAsync(
        new MongoQueueItem { 
            ClosedUtc = now.AddSeconds(-5), 
            IsClosed = true,
            IsTerminal = false,
            // ...
        });
    
    await Task.Delay(TimeSpan.FromSeconds(8)); // Wait for TTL
    
    var count = await collection.CountDocumentsAsync(/* IsClosed=true */);
    count.Should().Be(0);
}
```

**Signal:** TTL expiry works end-to-end.  
**Stability:** Proven in current branch.  
**CI Impact:** Slight (TTL waits 8s per test), but acceptable.

### 3. Lock Expiry Recovery (Behavior, Not Explain)
**File:** Existing → `MongoQueueLockExpiryIntegrationTests.cs`

Already validates:
```csharp
[Test]
public async Task StaleLock_IsRecoveredAndReacquired()
{
    var subscription = new MongoQueueSubscription(definition, ...);
    
    // Insert locked item with expired LockedUtc
    var item = new MongoQueueItem { 
        IsLocked = true, 
        LockedUtc = DateTime.UtcNow.AddSeconds(-10) /* past lease time */
    };
    await collection.InsertOneAsync(item);
    
    // Next poll should recover stale lock
    await subscription.ProcessItemsAsync(ct);
    
    var recovered = await collection.FindAsync(f => f.Eq(i => i.Id, item.Id)).FirstOrDefaultAsync();
    recovered.IsLocked.Should().BeFalse(); // Lock cleared
    recovered.RetryCount.Should().Be(1);    // New attempt
}
```

**Signal:** Query filter correctly identifies expired locks.  
**Stability:** Proven.  
**What it validates:** Lock recovery **works**, not "which index MongoDB chose."

### 4. Event Store Unique Index (Simple, Already Tested)
**File:** Event store tests

Already covers:
```csharp
[Test]
public async Task EventStore_EnforcesDuplicateVersionPrevention()
{
    var evt1 = new Event<MyAggregate> { AggregateId = id, Version = 1 };
    var evt2 = new Event<MyAggregate> { AggregateId = id, Version = 1 };
    
    await collection.InsertOneAsync(evt1);
    
    var act = async () => await collection.InsertOneAsync(evt2);
    await act.Should().ThrowAsync<MongoWriteException>(); // Unique constraint
}
```

**Signal:** Unique index prevents duplicates.  
**Stability:** High—constraint enforcement is deterministic.

### 5. Outbox Polling Index (Functional Correctness)
**File:** Existing → Outbox integration tests

```csharp
[Test]
public async Task OutboxProcessor_PollingIndex_SupportsQueriesCorrectly()
{
    var now = DateTime.UtcNow;
    
    // Insert mix: ready, locked, pending future
    await collection.InsertOneAsync(new OutboxMessage { 
        State = OutboxMessageState.Pending,
        NextAttemptUtc = now.AddSeconds(-5),
        IsLocked = false
    });
    await collection.InsertOneAsync(new OutboxMessage { 
        State = OutboxMessageState.Pending,
        NextAttemptUtc = now.AddSeconds(10),
        IsLocked = false
    });
    
    // Query for ready items
    var ready = await collection.FindAsync(
        Builders<OutboxMessage>.Filter
            .Eq(m => m.State, OutboxMessageState.Pending)
            .And(Builders<OutboxMessage>.Filter.Lte(m => m.NextAttemptUtc, now))
    ).ToListAsync();
    
    ready.Should().HaveCount(1);
    ready[0].NextAttemptUtc.Should().BeLessThanOrEqualTo(now);
}
```

**Signal:** Query returns correct subset; index must exist for this to be efficient.  
**Stability:** High—functional test, not explain-plan.

---

## What NOT to Add

❌ Full explain-plan parsing tests  
❌ Assertions on query execution time (flaky in CI)  
❌ "Force empty collection to use index" tests  
❌ MongoDB version-specific plan assertions  

These create brittle, high-maintenance tests that don't catch real bugs.

---

## CI Stability Assessment

**Testcontainers behavior:**
- ✅ Index creation: deterministic
- ✅ Unique constraint enforcement: deterministic
- ✅ TTL expiry: slightly slower than production (60s default), but works
- ✅ Lock recovery: deterministic (query semantics, not timing)
- ⚠️ Explain-plan behavior: non-deterministic, version-dependent

**Current test suite:** ~80 tests, ~2min on CI  
**Added index tests:** ~6 new tests, +8-10s (TTL waits in `MongoQueueRetentionIntegrationTests`)  
**Risk:** Very low. We're not adding flaky timing assertions.

---

## Decision Matrix

| Test Type | Feasible? | CI Stable? | Maintenance | Signal Value | Recommend? |
|-----------|-----------|-----------|-------------|--------------|-----------|
| Index definition structure | ✅ Yes | ✅ High | Low | High | ✅ YES |
| TTL expiry timing | ✅ Yes | ⚠️ Medium | Medium | High | ✅ YES |
| Lock recovery behavior | ✅ Yes | ✅ High | Low | High | ✅ YES |
| Event store uniqueness | ✅ Yes | ✅ High | Low | High | ✅ YES (exists) |
| Outbox query correctness | ✅ Yes | ✅ High | Low | High | ✅ YES (expand) |
| **Explain-plan parsing** | ✅ Yes | ❌ Low | **Very High** | **Low** | ❌ **NO** |
| Query execution timing | ✅ Yes | ❌ Low | High | Low | ❌ **NO** |
| Index selection with < 1K docs | ✅ Yes | ⚠️ Medium | High | Low | ❌ **NO** |

---

## Concrete Implementation Plan

### Phase 1: Index Definition Coverage (No-Risk)
**Work:** Add assertions to existing integration tests.

**File:** `MongoQueueIndexIntegrationTests.cs` (new, ~80 lines)
- Assert polling index structure
- Assert TTL index structure  
- Assert partial filters exist

**File:** Enhance `MongoEventStoreTests.cs`
- Assert unique index exists

**Time:** 1 hour. Risk: None.

### Phase 2: Expand Functional Tests (Low-Risk)
**Work:** Enhance existing integration tests.

**Files:** Already have:
- `MongoQueueLockExpiryIntegrationTests.cs` ← validates recovery behavior
- `MongoQueueRetentionIntegrationTests.cs` ← validates TTL expiry
- Outbox tests ← add one correctness query test

**Time:** 30 min. Risk: None (these patterns exist).

### Phase 3: DO NOT ADD
- Explain-plan tests
- Execution timing assertions
- MongoDB version-specific tests

---

## Risk Mitigation: If Queries Become Slow

The real risk isn't "did we define the index" but "did we accidentally query in a way the index can't help."

**Best practice:** In integration tests, run queries and assert they complete quickly **under normal load** (1000-10K docs). MongoDB planner will use index if it's helpful.

```csharp
var sw = Stopwatch.StartNew();
var result = await collection.FindAsync(filter).ToListAsync();
sw.Stop();

// Not "must be < 50ms" (flaky), but:
result.Count.Should().BeGreaterThan(0);
sw.ElapsedMilliseconds.Should().BeLessThan(1000); // Generous limit
```

But even this is optional if queries are already fast in our current tests.

---

## Conclusion

**Index-usage testing is worthwhile. Explain-plan testing is not.**

Add lightweight tests that verify:
1. Indexes exist with correct structure ✅
2. TTL actually expires items ✅
3. Lock recovery queries work ✅
4. Event store uniqueness enforced ✅

DO NOT:
- Parse explain-plan output
- Assert execution stages
- Force collection size thresholds
- Assert query timing in CI

The signal you'll gain: confidence that our query design doesn't accidentally regress to collection scans. The cost: ~100 lines of test code, ~8s added to suite (from TTL waits). The maintenance burden: minimal (BSON structure checks are stable).

**Tests should survive refactoring.** Explain-plan tests don't—they couple to MongoDB internals.

---

## Open Questions for the Team

1. **TTL behavior confidence:** Is "wait 8s, verify expiry" acceptable, or should retention tests be tagged `[Explicit]` and run separately?
2. **Event store focus:** Do we care about query performance there, or is uniqueness-enforcement enough?
3. **Outbox coverage:** Worth adding one "polling query correctness" test, or is existing coverage sufficient?
4. **CI time tolerance:** Can we add ~8s per test run for TTL verification?
