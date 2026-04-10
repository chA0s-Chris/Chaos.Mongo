# Eliot — PR #73 Blocker Fixes

## Proposed decision

Keep passive lease recovery, but add two guardrails in `MongoQueueSubscription`:

1. **Idle wake-up:** when the queue is idle, re-run the availability query on a bounded timer (`min(lockLeaseTime, 1s)`) so expired locks are retried even without new inserts.
2. **Owned completion:** only close/unlock a queue item when the stored `LockedUtc` still matches the timestamp written by the current consumer's lock acquisition.

## Why

- The original passive filter alone does not wake the processor after a lease expires.
- A slow handler can otherwise close or unlock work that has already been reclaimed by another consumer.

## Scope

This is intentionally limited to PR #73 blocker fixes and matching integration coverage.
