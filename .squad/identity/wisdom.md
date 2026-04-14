---
last_updated: 2026-04-09T19:17:01.978Z
---

# Team Wisdom

Reusable patterns and heuristics learned through work. NOT transcripts — each entry is a distilled, actionable insight.

## Patterns

<!-- Append entries below. Format: **Pattern:** description. **Context:** when it applies. -->

**Pattern:** MongoDB `explain()` plan tests are brittle and high-maintenance. Prefer lightweight schema inspection (index existence, field order, partial filters) if index verification is needed.

**Context:** When tempted to add integration tests that verify query execution plans via explain(), consider: query plans vary by MongoDB version, collection size, and sharding strategy. Unless performance is already a proven problem (via production telemetry), the maintenance cost of explaining fragile plans exceeds the regression detection benefit. Functional tests catch query behavior bugs; code review catches most logic errors.

**Pattern:** Bulk-write optimization applies to multi-collection transactions, not single-collection operations.

**Context:** MongoDB 8's multi-collection bulk writes benefit operations that coordinate writes across multiple collections in one transaction (e.g., EventStore appending events to 3 collections atomically). Single-collection workloads like message queues (all operations target one collection) don't have multi-collection coordination problems and don't benefit from bulk-write optimization. Queue improvements should focus on single-collection patterns: indexing strategy, query optimization, processing loop efficiency.
