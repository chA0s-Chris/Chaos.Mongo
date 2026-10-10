# Change-stream anchor candidates at production quality

> Issue: [#157](https://github.com/chA0s-Chris/Chaos.Mongo/issues/157)

## Rationale

`0157-0` implemented two anchor candidate sources and decided between them by a criterion fixed before measuring: change streams (D) won, because the pending-anchor collection (E) missed both thresholds on the default append path (#154, "Benchmark results and decision"). The issue's remaining criterion is that only the chosen source remains in the code base, at production quality.

This plan removes E and brings D to the quality that the anchoring pipeline of #158 can build on: bounded batches during recovery, resilience against interrupted reads, observable recoveries, and documented operating requirements. It also resolves the findings carried over from `0157-0`. Everything stays `internal` until #160.

## Acceptance Criteria

### Removing the pending-anchor collection

- [ ] Only the change-stream source remains: the pending-anchor source, its entries, its writes on both append paths and in retroactive sealing, and their tests are removed, and the append and sealing code paths behave as on `main` again.
- [ ] `EventStoreAppendBenchmarks` offers no protection, chain only and chain + D, and the load test chain only and chain + D; neither offers E.

### Change-stream source

- [ ] Candidate collection is enabled through an internal module-level `WithAnchorCandidates()`. Without it, appends, sealing and stored data are unchanged and no change stream is opened. `IAnchorCandidateSource` remains the contract that #158 consumes.
- [ ] Every batch reports whether more candidates are immediately available, so a consumer can drain a backlog or a recovery scan without relying on empty batches.
- [ ] A recovery scan examines at most `maxCandidates` streams per batch and persists its position with each acknowledgement, so a batch returns promptly even when most stream heads are not yet sealed.
- [ ] After a read fails, for example because of a non-resumable server error or a primary failover the driver does not resume, the next read reopens the stream at the persisted position and delivers the unacknowledged candidates again.
- [ ] The start of every recovery is logged with its cause (no persisted position, or history lost). When the start time of a just-completed recovery has already left the oplog, the source logs an error that names the duration of the scan, because the oplog window is too small for a full scan.
- [ ] The source's XML documentation states the operating requirements: a replica set or sharded cluster, an oplog window that covers the longest expected anchoring downtime plus a full recovery scan, the `changeStream` and `find` actions on the database, the restrictions of MongoDB-compatible services, and the extra server load on sharded clusters.
- [ ] The documentation scope of #160 lists the same operating requirements.
- [ ] Automated tests cover the behavior above; the correctness tests of `0157-0` run against the change-stream source alone.

## Technical Details

### Delivery

- Same branch, `0157-tamper-evidence-benchmark-anchor`. After this plan, the branch is merged as one PR that carries `skip-changelog` and closes #157 (delivery rules in #154).
- The decision record of `0157-0` stays unchanged. Its section "Carried into 0157-1" is resolved here.

### Removal

- Removed: `PendingAnchorCandidateSource`, `PendingAnchorEntry`, the pending-anchor write in `MongoEventStore.AppendOnceAsync` (both paths), `EventStreamSealingSweep.RecordPendingAnchorAsync`, `SealingChunkResult.LastSealedHash` (only E used it), `IntegrityAnchoringOptions.PendingAnchorsCollectionName` and `WritesPendingAnchors`, `MongoEventStoreOptions.Anchoring` (only E's write paths read it), `AnchorCandidateSourceKind`, and `PendingAnchorWriteTests`.
- The registration test for the shared anchoring options checks the module-level instance without the per-type property.
- `AnchorCandidateSourceTests` stops being parameterized by source; its tests run against D only.
- `ProtectionMode.ChainWithPendingCollection` and `LoadTestMode.ChainWithPendingCollection` are removed. The load test's decision table, which compares E with D, becomes a comparison of D with chain only.

### Registration

- `MongoBuilder.WithAnchorCandidates()` (internal, replaces `WithAnchorCandidateSource(kind)`) registers the change-stream source once. Event stores may be registered before or after it, as today. Module-level anchoring configuration in #158 may absorb it.
- The source receives an `ILogger<ChangeStreamAnchorCandidateSource>`, falling back to `NullLogger` like `EventStreamSealingSweep`.

### Batches

- `AnchorCandidateBatch` gains `HasMore`. During a scan it is `true` while unscanned streams remain; on the stream it is `true` when the server returned a full batch (`BatchSize` raw change events, before filtering).
- The change stream is opened with `BatchSize = maxCandidates` and reopened at the persisted position when a read asks for a different `maxCandidates`, so a batch never exceeds the requested size and `HasMore` compares against it.
- `ReadBatchAsync` still returns what is currently available without waiting; an empty batch with `HasMore` is legitimate during a scan.
- `AnchorCandidateConsumer.ConsumeBatchAsync` returns the reduced candidates together with `HasMore`, and `RunAsync` pauses only after a batch without candidates and without `HasMore`.
- The harness helpers that drain until empty batches (`LoadTestStore.DrainUntilIdleAsync`, the consumer start in the benchmark) drain until `HasMore` is `false` instead, so the initial recovery of a change stream completes outside the measurement.

### Recovery

- The scan examines at most `maxCandidates` streams per batch, counting streams whose head is unsealed or malformed.
- The recovery state additionally persists the wall-clock start of the scan. The state written when a scan completes keeps the scan's start and end, so the error for a lost start time can name the scan duration.
- A lost start time is recognized when opening the stream from a persisted operation time (the state right after a completed scan, before the first resume token) fails with `ChangeStreamHistoryLost`. The source then logs the error and recovers again; it does not consume the stream during the scan. Sizing the oplog is an operating requirement.

### Interrupted reads

- The driver resumes a change stream once on resumable errors. Any other failure propagates to the caller and leaves the batch outstanding, so the next read reopens the stream at the persisted position; this already follows from the at-least-once contract and is now tested.
- The stream is resumed with `StartAfter` instead of `ResumeAfter`, so a persisted token of an invalidate event (for example after the database was dropped) restarts the stream instead of failing on every read.
- An interrupted read is forced in tests with a `failCommand` fail point on `getMore` using a non-resumable error code, scoped by `appName` like the history-loss test.
