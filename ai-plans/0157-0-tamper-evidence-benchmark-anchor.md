# Benchmark anchor candidate sources: change streams vs. pending collection

> Issue: [#157](https://github.com/chA0s-Chris/Chaos.Mongo/issues/157)

## Rationale

Anchoring (#158) must find the chain heads that changed since the last anchor, without gaps when transactions commit late or out of order, and efficiently with millions of streams. #154 narrowed this down to two candidate sources: change streams on the event collections (D), which keep the append path untouched but depend on the oplog window and need a recovery path, and a pending-anchor collection written in the append transaction (E), which is robust but adds a write to every append.

This plan implements both sources behind one internal abstraction, verifies both against the same correctness tests, measures them as described in the benchmark plan of #154, and decides by a criterion that is fixed before any result exists. It is the first of two plans for #157 on one branch: `0157-1` is written after the decision and removes the losing source and brings the winner to production quality. The branch is merged once, as one PR that contains only the winner.

## Acceptance Criteria

### Decision criterion

- [x] Before any benchmark result of this plan exists, #154 records the decision criterion: E is chosen if chain + E with its consumer running, compared with chain + active D consumer, has a p99 latency at most 10 % higher and a throughput at most 10 % lower, each taken as the median of three load-test runs on the three-node replica set, at 16 and at 64 appenders, on both append paths. Otherwise D is chosen with a full-scan recovery path.

### Candidate sources

- [x] An internal candidate-source abstraction returns batches of `(AggregateType, AggregateId, Version, Hash)` candidates across all protected aggregate types and acknowledges a batch inside a caller-supplied transaction. At most one batch is outstanding at a time: reading again before acknowledging returns the unacknowledged candidates again, and acknowledging a batch covers everything read up to it. Acknowledged candidates are never returned again (at-least-once).
- [x] Option E: every append to a protected stream inserts one pending-anchor entry for its last event within the append transaction, on both append paths. Every chunk of retroactive sealing inserts one entry for its last sealed event within the chunk transaction. Acknowledging deletes the entries.
- [x] Option D: a change-stream source over the protected events collections returns candidates for appended and retroactively sealed events and persists its resume token in the acknowledging transaction.
- [x] D recovers without gaps when its resume point is no longer in the oplog or no token exists yet: it scans all stream heads and resumes the change stream from a cluster time taken before the scan.
- [x] Both sources pass the same correctness tests: a transaction that commits after a later one has been consumed and acknowledged is still delivered; reading again without acknowledging returns the same candidates; after a consumer restart, acknowledged candidates are not delivered again and unacknowledged ones are; aborted transactions never produce candidates.
- [x] A minimal consumer reads candidates, reduces them to the highest version per stream, and acknowledges them, without requesting timestamps.
- [x] Without integrity protection or without a configured candidate source, append behavior and stored data are unchanged: no pending-anchor writes and no change stream.

### Benchmarks and decision

- [x] `EventStoreAppendBenchmarks` compares no protection, chain only, chain + E, and chain + active D consumer, each on both append paths, for new and existing streams.
- [x] A load test reports throughput, p50 and p99 latency for 16 and 64 concurrent appenders on distinct existing streams, for chain only (as a reference) and per candidate source, on each append path, against the single-node Testcontainers replica set and against a three-node replica set with `w: majority` and a 2 ms injected round-trip latency between members.
- [x] Oplog growth per append and candidate-consumption throughput (candidates per second, including catching up a backlog of appends and a backlog of retroactively sealed events) are measured for both sources.
- [x] The results and the decision, applying the recorded criterion, are documented in #154.
- [x] Automated tests cover the behavior above.

## Technical Details

### Delivery

- One branch, `0157-tamper-evidence-benchmark-anchor`, for both plans. This plan ends with the decision; `0157-1` covers removing the losing source, production quality, and the remaining issue criterion ("only the chosen source remains"). The branch is not merged before `0157-1` is complete.
- Everything stays `internal`, as required by the delivery rules in #154.

### Baseline

- #164 changed the append path (stream-head read, `Revision`, store-assigned position retries), and the benchmark now seeds fresh streams per iteration. The chain-only numbers in the body of #157 are therefore not comparable; chain only is measured again in the same runs as the candidate sources.

### Abstraction and configuration

Illustrative shape:

```csharp
internal sealed record AnchorCandidate(String AggregateType, Guid AggregateId, Int64 Version, Byte[] Hash);

internal interface IAnchorCandidateSource
{
    // The batch carries an opaque position: the entry IDs for E, the resume token for D.
    Task<AnchorCandidateBatch> ReadBatchAsync(Int32 maxCandidates, CancellationToken cancellationToken);

    Task AcknowledgeAsync(IClientSessionHandle session, AnchorCandidateBatch batch, CancellationToken cancellationToken);
}
```

- Acknowledging takes the caller's session so that #158 can acknowledge atomically with persisting an anchor.
- `ReadBatchAsync` returns what is currently available, possibly an empty batch, without waiting for new events.
- Anchoring is global across aggregate types (#154, section 3), so the sources need all protected events collections. A new internal, non-generic registry collects them from each `WithIntegrityProtection()` registration. Per type, it provides the events collection name, the element names from `EventDocumentFields<TAggregate>` and the head queries from `SealingSweepQueries<TAggregate>`, because the change stream and the recovery scan span all types.
- A new internal module-level setting selects the source (none, E, or D). Module-level anchoring configuration arrives in #158 and may absorb it.

### Option E: pending-anchor collection

- One global collection (new) with `{ _id, AggregateType, AggregateId, Version, Hash }`. Only the last event of an append is recorded, because the head covers its predecessors through the chain.
- Default path: one more insert in the append transaction (one more round trip). Bulk-write path: one more `BulkWriteInsertOneModel` in the same client bulk write.
- Retroactive sealing writes its entry in the chunk transaction of `EventStreamSealingSweep`. The append safety net needs no entry of its own, because the append's entry covers the sealed predecessors.
- Uncommitted entries are invisible, and acknowledgement deletes exactly the read `_id`s, so late commits are picked up by a later read.
- Open for `0157-1`: whether entries are written when no anchoring is configured. Without a consumer they accumulate indefinitely.

### Option D: change streams

- One database-wide change stream filtered to the protected events namespaces, for inserts and for updates that set `_integrity`. `FullDocument = UpdateLookup` gives retroactive seals their `AggregateId` and `Version`; a `$project` keeps only the candidate fields, resolved through `EventDocumentFields`.
- A state document (new collection) holds the resume token; it is written in the acknowledging transaction.
- Every acknowledgement, including that of an empty batch, persists the change stream's post-batch resume token, so idle periods do not let the token fall out of the oplog window.
- A read without a preceding acknowledgement reopens the change stream at the last persisted resume token, so an unacknowledged batch is delivered again.
- `UpdateLookup` costs one server-side read per retroactively sealed event, up to one sealing chunk per transaction, which the retroactive-sealing backlog measurement captures.
- Recovery is triggered by `ChangeStreamHistoryLost` (error code 286) or a missing token. It takes the cluster time before scanning, scans all stream heads with the existing `SealingSweepQueries` (two index point reads per stream), and then starts the change stream at that cluster time. Candidates delivered by both the scan and the stream are harmless, because consumers reduce to the highest version per stream.
- During recovery, acknowledging persists the scan position (the last scanned `AggregateId` per events collection, as in the sealing sweep) together with the cluster time taken before the scan. A restart continues the scan instead of repeating it, and the change stream starts at the persisted cluster time once the scan is complete.
- The recovery test forces history loss, for example by starting from an operation time before the oldest oplog entry.
- `MongoQueueSubscription` is the existing change-stream consumer in the repository; it does not persist resume tokens yet.

### Correctness tests

- One shared test fixture runs every correctness test against both sources.
- Late and out-of-order commits are produced deterministically by holding an append transaction open in `AppendEventsAsync`'s `onBeforeCommit` callback while a later append commits and is consumed and acknowledged.
- A consumer restart is simulated by disposing the consumer and its service provider between read and acknowledgement.

### Benchmarks

- `EventStoreAppendBenchmarks` gets a candidate-source parameter. Valid combinations are modeled through `ParamsSource` (no protection only without a source).
- Both candidate sources run their minimal consumer continuously during the measurement, so the server-side cost of each source (change-stream reads for D, deletes for E) appears on the append side. Consumer start-up stays outside the measured operation.
- Oplog growth is measured as the summed `$bsonSize` of the oplog entries written during the measured `ts` range, divided by the number of appends; the size of the capped `local.oplog.rs` collection stops changing once it is full. E is expected to add two oplog entries per append (insert and delete).

### Load test

- A new console project under `benchmarks/`, because a sustained concurrent run with latency percentiles does not fit BenchmarkDotNet. It prints its results as a Markdown table, including the environment and the injected latency.
- Parameters: candidate source, append path, concurrency (16 and 64), warm-up and measurement duration. Appenders write to distinct pre-seeded streams while the source's consumer runs.
- Three-node replica set: three `mongo:8` containers in one Docker network with `w: majority`. Latency is injected with `tc netem` (1 ms delay per direction, 2 ms round trip), which needs `NET_ADMIN` and a derived image with `iproute2`.
- The load test connects from the host with `directConnection=true` to the primary, so the members' advertised host names do not matter; `w: majority` still waits for the secondaries. With netem on each container's egress, member-to-member round trips are 2 ms and client-to-primary round trips 1 ms; the results state both.
