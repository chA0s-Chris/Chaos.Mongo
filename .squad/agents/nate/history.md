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
