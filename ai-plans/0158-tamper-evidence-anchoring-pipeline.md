# Anchoring pipeline with RFC 3161 timestamp authorities

> Issue: [#158](https://github.com/chA0s-Chris/Chaos.Mongo/issues/158)

## Rationale

The per-stream hash chain (#156) detects changes inside a stream, but only an anchor proves to a third party that a chain head existed at a given time and has not been rolled back since. This issue builds the anchoring pipeline of #154 (sections 3 and 3a): Merkle trees over the chain heads that the change-stream candidate source (#157) reports, timestamped by one or more RFC 3161 timestamp authorities, with chained anchors and crash-safe two-phase runs. Everything stays `internal` until #160.

The work is one story but four concerns with different reviewers in mind: the timestamp client and its cryptographic validation, the candidate source's read contract, the anchoring core with its crash-safety and storage invariants, and the operational surface around it. It is delivered as a native GitHub stack so each layer can be reviewed on its own, bottom to top. #154 rejected stacking across sub-issues because the #157 decision could change later layers; within this issue the layers depend on each other linearly.

## Acceptance Criteria

### Layer 1: Timestamp authorities

- [x] `ITimestampAuthority` returns a `TimestampToken` for a SHA-256 digest. The default `Rfc3161TimestampAuthority` POSTs an `application/timestamp-query` with an `HttpClient` obtained per authority from a `Func<IServiceProvider, HttpClient>`; `Chaos.Mongo.EventStore` gains no dependency on `Microsoft.Extensions.Http`.
- [x] Tokens are validated on receipt with `System.Security.Cryptography.Pkcs`: message imprint, nonce, signature, ESSCertID, and the certificate chain against the authority's trust configuration. Tokens with a wrong imprint, a wrong nonce, a broken signature or an untrusted chain are rejected.
- [x] A `TimestampToken` carries the raw token, the authority's certificate chain and the revocation data captured at stamping time.
- [x] Tests use a local, in-process RFC 3161 test authority with a self-signed test CA instead of an external service, validating with revocation mode `NoCheck`. The test CA's certificates name a CRL distribution point served by the test handler, and the issued token carries that CRL. The authority can also return invalid tokens, HTTP failures and an unavailable CRL, which fails the attempt.

### Layer 2: Candidate source

- [x] The candidate source supports consecutive reads before one acknowledgement, in the change stream and in the recovery scan, including the hand-over from scan to stream; acknowledging the last batch persists the position for all of them. Existing candidate-source tests still pass, except the one asserting that an unacknowledged batch is returned again, which is replaced; new tests cover consecutive reads, and a restart or `DiscardUnacknowledgedReads()` after unacknowledged reads, which delivers their candidates again.
- [x] Candidates carry their commit time: the change's cluster time, or the start of the scan for candidates found by a recovery scan.

### Layer 3: Anchoring core

- [x] An anchor covers every chain head changed since the previous anchor, including heads from transactions that committed late. Each stream contributes one leaf for its highest new version, and leaves are sorted deterministically.
- [x] An anchoring run is two-phase and crash-safe at every phase: tests simulate a crash after each phase and show that a new runner completes the anchor without losing or duplicating candidates. A run that reads candidates and fails before its commit point, followed by another run in the same process on the same source instance, anchors those candidates. Runs execute on exactly one instance under the Chaos.Mongo distributed lock.
- [x] Anchors are chained through the digest of their predecessor, and an interval without candidates creates no anchor and requests no timestamp.
- [x] Leaves and their inclusion proofs are stored outside the anchor document; an anchor whose tree data is well beyond 16 MB is created and completed.
- [x] Several authorities can stamp one anchor. An anchor completes once it holds a configurable minimum number of valid tokens (default 1). An unreachable or failing authority neither blocks appends nor loses anchors, and no new anchor is created while one awaits its timestamps.
- [x] Anchors store their tokens with certificate chains and revocation data, the hash algorithm and the anchored period, in a format that leaves room for RFC 4998 renewal.

### Layer 4: Operations

- [x] Anchoring is configured once at module level through an internal builder API with a default interval of 5 minutes. Registration fails with a clear error when no timestamp authority is configured; there is no default authority.
- [x] A hosted service runs the anchoring run at the configured interval after startup.
- [x] A hook notifies the application of every completed anchor, in anchor order and at least once.
- [x] When the oldest unanchored candidate is older than a configurable threshold (default 1 hour), this is reported through a log entry and a metric.
- [x] An internal lookup returns, for `(aggregateType, aggregateId, version)`, the first completed anchor whose leaf for that stream has a version greater than or equal to it, or none while the event is unanchored. Tests cover retroactively sealed, appended and unanchored events.
- [x] Automated tests cover the configuration, the hosted run, the hook and the delay alert.

## Technical Details

### Timestamp authorities

- `TimestampAsync(ReadOnlyMemory<Byte> digest, HashAlgorithmName algorithm, CancellationToken)` as sketched in #158. Only SHA-256 is accepted (#154, section 3a). Custom authorities for non-RFC 3161 APIs implement the interface and return an RFC 3161 token.
- `System.Security.Cryptography.Pkcs` becomes a dependency of `Chaos.Mongo.EventStore` through a new central version in `Directory.Packages.props`, aligned with the other 10.0.x packages; it has to support net8.0, net9.0 and net10.0.
- The request uses a fresh random nonce and asks for the signer certificate. Validation relies on the Pkcs types (`Rfc3161TimestampRequest`, `Rfc3161TimestampToken`); no CMS parsing or signature verification is written in-house. The chain is built with `X509Chain` against per-authority trust settings (trusted roots, revocation mode), because qualified authorities often chain to roots outside the OS store.
- The authority adds the RFC 3161 and RFC 5280 checks the Pkcs types leave open: the signing certificate's extended key usage contains only `id-kp-timeStamping`, its key usage permits signatures, and it carries no extension twice; a `tsa` name corresponds to one of its subject names; the signed content is exactly one TSTInfo, and the token carries no countersignature. An optional policy ID is requested and required, because the Pkcs types do not compare the token's policy with the requested one (RFC 3161, section 2.2). Tokens signed with an MD5 or SHA-1 digest, or with an RSA key below 2048 bits or an ECDSA key below 256 bits, are rejected without an opt-out (ETSI TS 119 312); the SHA-1 `ESSCertID` is still accepted because it only identifies the certificate. Signature algorithms of the chain's certificates are left to `X509Chain` and depend on the platform's policy. The authority's own decoding goes only as far as it decides an outcome; certificate validation is `X509Chain`'s job.
- Revocation data captured at stamping time consists of the CRLs downloaded from the CRL distribution points of the chain's non-root certificates, through the authority's `HttpClient`. A certificate without a distribution point contributes no CRL; a certificate that names distribution points must yield at least one downloaded CRL, and distribution points without an HTTP(S) location are skipped. A CRL that cannot be downloaded fails the attempt like an invalid token, so the next run retries that authority; a token is stored only with the complete revocation data its chain names. A downloaded CRL must be a single DER-encoded CRL (RFC 5280, section 4.2.1.13) issued by the certificate's issuer, unless the point names another CRL issuer, and must not expire before the token's time; otherwise it counts as unavailable at that location. Responses are bounded in size. Beyond these checks, captured CRLs are stored as evidence for #160 without being verified here; in particular their signatures are not checked. A CRL captured at stamping time is usually issued before the token's time, so it cannot show that a certificate was not revoked between its issue and the token's time; completing the evidence with CRLs issued after the token's time is deferred to #181. Revocation during validation is checked by `X509Chain` according to the authority's revocation mode, independently of the captured CRLs. Distribution points are read from the certificate extension with `System.Formats.Asn1`, which #154 permits; it forbids only in-house CMS parsing and signature verification. OCSP responses are not captured: .NET offers no OCSP request API, and requesting and parsing OCSP responses would be in-house protocol code. The stored format leaves room to add them.
- The test authority is an `HttpMessageHandler` in the test project that builds TSTInfo with `System.Formats.Asn1` and signs it with `SignedCms`, including the signing-certificate attribute. Producing tokens is test code; validation is not.

### Merkle tree and anchor digest

- The tree follows the Merkle Tree Hash of RFC 9162, section 2.1: leaf hashes are prefixed with `0x00`, node hashes with `0x01`, and an unbalanced tree splits at the largest power of two smaller than the leaf count, without duplicating nodes. Inclusion proofs follow the same section.
- A leaf encodes `(aggregateType, aggregateId, version, hash)` in a fixed byte layout: the UTF-8 type name with a 4-byte big-endian length prefix, the 16 UUID bytes in RFC 9562 order, the 8-byte big-endian version and the 32-byte chain hash. Leaves are sorted by these bytes. The byte layouts become part of the specification in #160; tests pin expected roots and proofs.
- Authorities stamp the anchor digest, a SHA-256 over the 4-byte big-endian anchor format version, the 8-byte big-endian anchor sequence number, the previous anchor's 32-byte digest and the 32-byte Merkle root. The first anchor uses 32 zero bytes as its previous digest. The chain of anchors is therefore covered by the timestamps, not only by stored fields.

### Candidate source

- The candidate source currently reopens at its persisted position when a batch is read before the previous one is acknowledged. It changes so that consecutive reads continue from the last read position and acknowledging the last batch persists the position for all of them, which is what allows one anchor, and one timestamp, per run regardless of event volume.
- Because successive runs share the source, each run starts by discarding unacknowledged reads through a new `DiscardUnacknowledgedReads()`, after which the next read reopens at the persisted position. A read after a failed read, after an acknowledgement whose transaction did not commit, or with a different batch size still reopens at the persisted position as today.
- Anchor candidates gain their commit time: the change's cluster time, or the start of the scan for candidates found by a recovery scan. Candidates found by a recovery scan carry its starting cluster time, so a head committed during the scan can get an earlier commit time than its actual one; the delay alert errs towards reporting earlier, and the anchored period may start slightly early.
- The benchmarks keep using `WithAnchorCandidates()` together with `AnchorCandidateConsumer`, which stays as their stand-in for the run, and the load test drains through the source directly; both follow the changed read contract, so their measurements remain comparable with #157.

### Anchoring run

- Phases, all under one global distributed lock whose lease is extended like the sealing sweep's:
  1. Complete or retry the anchor awaiting timestamps, if any. While it cannot complete, the run stops here.
  2. Read candidates until the source reports no more or the configurable maximum number of leaves per anchor (default 100,000) is reached, and reduce them to the highest version per stream. The maximum also bounds the number of batches read, the maximum divided by the batch size, because the change-stream source reports more as long as changes keep arriving, whether or not they yield candidates, and continuous changes must not keep a run from committing or from acknowledging its progress. The run reads with a constant batch size and checks both bounds between batches, so an anchor may exceed the maximum by less than one batch. Remaining candidates go into the next run's anchor; the run does not loop, so a backlog drains at one anchor per interval and the number of timestamps stays bound to the interval, as #154 requires. The delay alert surfaces a backlog that outgrows this rate.
  3. Insert the anchor as `Preparing` under a new anchor ID unique to this attempt, without a sequence number, and write the leaves with their inclusion proofs under that ID. The attempt fixes its sequence number, the highest committed plus one, before writing the leaves; the anchor document receives it at commit. Then, in one transaction, move the anchor from `Preparing` to `AwaitingTimestamp` with the next sequence number and acknowledge the candidates. That transaction is the commit point. Cleanup at the start of each run moves every `Preparing` anchor to `Discarded` and deletes the leaves of `Discarded` anchors; both transitions are filtered by the expected status, so either the marker or the attempt's own commit succeeds, never both. Leaves of `Discarded` anchors are deleted on every cleanup, because a superseded runner may still write leaves after the marker exists; attempts discarded more than 24 hours ago, far beyond any lock lease, are removed together with any remaining leaves, so discarded attempts do not accumulate.
  4. Request tokens from every authority that has not yet delivered one for the anchor, validate them and store them on the anchor. Mark the anchor `Completed` once it holds the configured minimum; otherwise it stays `AwaitingTimestamp` and the next run retries the missing authorities. The last failure and a failure count per authority are recorded on the anchor so gaps stay visible.
- With the change-stream source, the candidates are released by the acknowledgement in phase 3; the anchor's leaves hold them from then on.
- `Preparing` and `Discarded` anchors carry no sequence number, so a unique index on the sequence number, partial on its existence, and status transitions filtered by the expected status prevent a forked chain even if a lock lease expires during a run. `Preparing` and `Discarded` anchors are ignored by the chain and the lookup.
- Tests simulate crashes through an internal phase callback of the run that can abort it after any phase; a new runner then resumes from the stored state.
- Each run calls `DiscardUnacknowledgedReads()` on the candidate source before phase 2. Each anchor stores the oldest commit time among its candidates.
- The anchored period runs from the end of the previous anchor's period to the commit time of the newest candidate in the anchor; the first anchor's period starts at its oldest candidate's commit time.

### Storage

- Collections: anchors, and leaves referenced by anchor ID. Anchors hold the ID, status, sequence number, previous digest, anchor digest, Merkle root, leaf count, hash algorithm, period, the oldest candidate commit time, a format version, and a list of timestamp entries (authority name, raw token, certificate chain, CRLs, time obtained). RFC 4998 renewal can later append re-timestamping entries to that list. Chains and CRLs are embedded in every entry; deduplicating them is deferred to #171.
- Each leaf document holds its index, the stream identity, version, chain hash and its inclusion proof, so a single leaf suffices to verify one stream head against the anchor. An index on the anchor ID serves cleanup and per-anchor reads.
- The collection names (new: `IntegrityAnchors`, `IntegrityAnchorLeaves`) and the run settings join the existing `IntegrityAnchoringOptions`, which the operations layer's builder fills.
- Leaves also carry their anchor's sequence number. An index on `(aggregateType, aggregateId, anchor sequence)` over the leaves serves the sealing-anchor lookup, which only considers leaves whose anchor exists and is completed. The operations layer creates this index together with the lookup.

### Operations

- An internal module-level `MongoBuilder` extension configures anchoring: interval, delay threshold, minimum tokens per anchor, maximum leaves per anchor, the authorities, and the completion hook. It registers the change-stream candidate source through `WithAnchorCandidates()`, which remains the lower-level internal registration. The minimum must not exceed the number of authorities. Registration fails rather than leaving anchoring dormant, as #158 suggests, because calling the anchoring builder without an authority is a configuration error that would otherwise surface only as a growing delay alert.
- The hosted service follows `EventStreamSealingHostedService` and starts after startup, so configurators have created the collections and indexes.
- The hook is an async delegate `Func<IServiceProvider, CompletedAnchor, CancellationToken, Task>` registered through the anchoring builder. `CompletedAnchor` (new, internal) carries the anchor's stored fields and tokens but not its leaves; an internal leaf reader by anchor ID lets a hook stream them.
- Hook delivery is at-least-once: an anchor is marked notified only after the hook returns, and each run first notifies completed but unnotified anchors in sequence order. A failing hook is logged and retried on the next run, and later anchors are not notified before it succeeds; anchoring itself continues.
- The reported delay is the age of the oldest candidate commit time stored on the anchor awaiting timestamps, if there is one, and otherwise the age of the oldest candidate read in the run. After a failed or lock-lost run, the delay of the anchor awaiting timestamps, or else the last known oldest unanchored candidate, is reported, so the alert does not go silent while anchoring fails. Metrics use a `Meter`, following `MongoQueueDiagnostics`.
- The sealing anchor of an event is the first completed anchor whose leaf for the event's stream has a version greater than or equal to the event's version. It is looked up, not stored on the event as #154 and #156 anticipated: a stored reference would modify sealed documents after sealing, would need `update` on the event collections permanently instead of only during sealing, and would need a second rule for appended events. The same rule yields the anchor that proves an appended event existed no later than its timestamp. #160 exposes it through the verification API.
- All new types live in the `Integrity` namespace, which the existing visibility test checks, so nothing new is publicly reachable before #160.

### Stack Design

1. `0158-tamper-evidence-anchoring-pipeline-tsa` — timestamp authority abstraction, RFC 3161 client and token validation, with the in-process test authority.
2. `0158-tamper-evidence-anchoring-pipeline-candidates` — candidate-source read contract with consecutive reads and `DiscardUnacknowledgedReads()`, candidate commit times, and `AnchorCandidateConsumer` adapted to the contract; depends on the timestamp authorities layer only for stack order, not for code.
3. `0158-tamper-evidence-anchoring-pipeline-core` — Merkle tree, anchor and leaf storage, the crash-safe anchoring run with chaining and multiple authorities; depends on the candidate source layer. Tests drive runs directly, without a hosted service.
4. `0158-tamper-evidence-anchoring-pipeline-operations` — module-level configuration, hosted run, completion hook, delay alert and sealing-anchor lookup; depends on the anchoring core layer.
