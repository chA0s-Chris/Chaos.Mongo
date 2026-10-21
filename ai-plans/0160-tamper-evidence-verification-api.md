# Tamper evidence: verification API, specification, reference verifier and public release

> Issue: [#160](https://github.com/chA0s-Chris/Chaos.Mongo/issues/160)

## Rationale

The hash chain (#156), anchoring (#158) and personal-data commitments with erasure (#159) produce tamper evidence, but nothing yet turns that evidence into answers: whether a stream is intact and anchored, whether derived data matches the events, whether every event is covered by an anchor, and whether all of this can be checked without Chaos.Mongo. This final sub-issue of [#154](https://github.com/chA0s-Chris/Chaos.Mongo/issues/154) (sections 5 and 6, "Completeness check") adds verification, freezes the format as version 1 with a byte-level specification and a non-.NET reference verifier, and makes the feature public in one step.

The work stays one issue because the format freeze, the verification semantics, the specification and the public surface must agree with each other. It is delivered as a native GitHub stack of six layers, so each concern can be reviewed on its own. #154 rejected stacked PRs for the sub-issue breakdown because the #157 benchmark could change later layers; that risk is resolved, and the rejection does not apply within this issue. Every layer below the top keeps all new API `internal`, so going public still happens in one step, in the top layer. Layers 3 to 5 depend in code only on layers 1 and 2. They still stay in one linear stack: all of them must land before the top layer makes the feature public in one step, and a linear order costs only rebases.

## Acceptance Criteria

### Layer 1: Format freeze

- [x] The genesis value is derived from the `AggregateType` element stored in the version-1 document, not from `typeof(TAggregate).Name`, on append, in retroactive sealing and in verification. A stream whose stored `AggregateType` differs from `typeof(TAggregate).Name` verifies.
- [x] A version-1 document without a string `AggregateType` element fails verification at version 1. The sealing sweep skips that stream and logs it, and an append that would seal it fails with `MongoEventStoreException` without persisting anything.
- [x] The chain hash binds the seal mode. Changing a stored `SealMode` from `Append` to `Retroactive` or back is reported as a hash mismatch.
- [x] Both append paths still persist byte-identical documents, and write-time hashes still equal the hashes recomputed from the stored bytes.
- [x] Automated tests cover the behavior above, and pinned hashes in existing tests are updated to the new hash input.

### Layer 2: Stream and anchor verification

- [x] Stream verification reports the first broken version as before. For every verified version, it also returns the hash, the previous hash, the seal mode, the sealing anchor (the first completed anchor whose leaf for the stream has a version at or above it, or none), and the erasure status of each recorded personal-data path: present, erased with an erasure record, or erased without one.
- [x] Without anchor collections configured, stream verification still verifies the chain, reports no sealing anchors, and states that anchor coverage was not checked.
- [x] Transitions from `Append` to `Retroactive` seal mode are reported as informational findings with their version. They are not failures and do not affect whether the chain is intact.
- [x] Stream verification compares the stream with its leaves in completed anchors. An anchored version that no longer exists is reported as broken (truncation), and so is an anchored version whose stored hash differs from the leaf hash (rewrite or rollback).
- [x] For any anchored event, an anchor verification checks the chain from the event to the leaf's version, recomputes the leaf and its Merkle inclusion proof up to the stored root, recomputes the anchor digest, and validates every stored timestamp token. It reports a result for each token. The event counts as anchored when at least one token is valid.
- [x] Stored tokens are validated with the same checks as on receipt, except the nonce and the comparison with the local clock: signature, message imprint equal to the anchor digest, ESSCertID, signer constraints, the token's time equal to the stored `TimestampUtc`, and the certificate chain built to a trusted root at the token's time.
- [x] Each token's revocation status is recomputed from its stored confirming CRLs, not read from the stored status. A CRL counts only when its signature verifies against its issuer's certificate in the stored chain, its issuer matches, and it was issued after the token's time. A token whose chain certificate such a CRL revokes at or before the token's time is invalid. Confirming the status needs CRLs issued at least the grace period after the token's time; a token without them for every distribution point is reported as unconfirmed but still counts as valid. A CRL whose issuer certificate is not in the stored chain, such as one from an indirect CRL issuer, cannot be verified and does not count.
- [x] Evidence that is missing from the evidence store, or does not match its hash, is reported as a failure of the token that references it, not thrown as an exception.
- [x] An anchor-chain verification checks that committed anchor sequence numbers are contiguous from 1, that each anchor's previous digest equals its predecessor's digest, and that each digest recomputes from its stored fields. It reports the first broken sequence number. It optionally takes a trusted head: the sequence number and digest of an anchor kept outside the database, for example by the completion hook. A missing or different anchor at that sequence is reported as broken.
- [x] Automated tests cover the behavior above, including a token with a wrong imprint, a token from an untrusted root, a confirming CRL with a broken signature, a CRL that lists a chain certificate as revoked, a stored status changed from `Revoked` to `Confirmed`, missing evidence, a broken inclusion proof, a deleted anchor, newest anchors deleted with a trusted head given, and a modified Merkle root.

### Layer 3: Checkpoints and read model

- [x] Checkpoints of protected stores record the chain hash of the event at their version. Checkpoints of unprotected stores are stored unchanged. An erasure rebuild keeps the recorded hash.
- [x] With protection enabled, a `CheckpointDocument<TAggregate>` class map that a consumer registered before the event store fails at registration with an error naming the reason, and the class-map registry stays unchanged. A class map the event store registered itself, for example in an earlier registration in the same process, is reused.
- [x] A derived-data check reports every checkpoint whose recorded hash differs from the event hash at its version. A checkpoint without a recorded hash is reported as unverifiable, not as a failure.
- [x] The derived-data check replays the stream from its start and reports a read model that differs from the replayed state. In the same pass, it reports every checkpoint whose state differs from the replayed state at its version.
- [x] The derived-data check requires an intact chain. Otherwise it reports the stream verification failure and compares nothing.
- [x] Automated tests cover the behavior above, including a manipulated read model, a manipulated checkpoint state, a checkpoint moved to another version, and a stream with erased personal data.

### Layer 4: Completeness check

- [x] A completeness check over all streams of an aggregate type reports every stream with events that no completed anchor covers. Each report gives the first unanchored version, the head version, and the age of the oldest unanchored event.
- [x] It reports a stream whose anchored head no longer exists, whether the whole stream was deleted or truncated below its anchored version.
- [x] It reports retroactively sealed events as "protected since anchor X" with their version range, and unsealed events as unsealed.
- [x] It runs the anchor-chain verification first, with the trusted head when one is given, and reports a broken anchor chain, because deleted-stream detection relies on it.
- [x] Automated tests cover the behavior above, including a deleted stream, a truncated stream, a never-anchored stream, a retroactively sealed stream and an anchor awaiting timestamps, whose leaves do not count as coverage.

### Layer 5: Specification and reference verifier

- [x] A versioned, language-neutral specification (format version 1) defines at byte level: the hash input, the chain rule and the genesis value, the personal-data commitments, the Merkle construction (leaf encoding, node hashing, ordering, inclusion proofs), the anchor format, including the digest the timestamp tokens cover, and the content-addressed evidence store of #171 with its chunking. It also defines the revocation confirmation of #181, and how tokens and CRLs are checked with OpenSSL.
- [x] The repository contains test vectors: raw event documents (appended, retroactively sealed, with commitments, with erased values), the expected genesis values and hashes, a Merkle tree with inclusion proofs for balanced and unbalanced sizes, anchor documents with digests, evidence documents, one chunked evidence item defined by a deterministic generator in the specification together with its expected item and chunk hashes rather than its bytes, and a sample timestamp token with its test CA, a stamping-time CRL and a confirming CRL.
- [x] A .NET test reproduces every deterministic test vector with the library and validates the sample token, so the library and the specification cannot drift apart.
- [x] A reference verifier written in Python, using only the standard library, verifies streams, inclusion proofs, the anchor chain and anchor digests from raw BSON. It resolves evidence by hash, reassembling chunked items, and rejects evidence that does not match its hash. It verifies tokens with `openssl ts -verify`, and confirming CRLs with OpenSSL.
- [x] An automated test runs the reference verifier against all test vectors. Another test runs it against a stream and an anchor produced by the library and stored in MongoDB; it passes for intact data and fails for a modified event.

### Layer 6: Public release

- [ ] The complete public surface of the feature is reviewed as a whole and made public. A public-surface test replaces the visibility test: it lists every public integrity type and member, so later changes to the surface are deliberate.
- [ ] Verification and erasure are exposed through new public interfaces. Per-aggregate verification and erasure are registered only for protected aggregate types, and anchor verification is registered by the anchoring or verification-only registration. `IEventStore<TAggregate>` gains no members.
- [ ] `PersonalDataErased<TAggregate>` is public, so readers of `GetEventStream` can recognize erasure events.
- [ ] The specification and the `_integrity` format are marked stable (format version 1), in the specification and in the code documentation.
- [ ] `docs/event-store.md` and the package README cover every documentation item listed in #160, plus the #171 and #181 items and the limitations listed in Technical Details.
- [ ] The existing test suite passes, and merged line coverage stays at or above 95 %.

## Technical Details

### Prerequisites

- #171 and #181 are merged, and this plan builds on the anchor format they left behind:
  - Timestamp entries on an anchor (`AnchorTimestampReference`) hold only SHA-256 references.
  - Tokens, certificates and CRLs live in the content-addressed `IntegrityAnchorEvidence` and `IntegrityAnchorEvidenceChunks` collections.
  - `AnchorEvidenceResolver` turns an anchor into a self-contained `CompletedAnchor`.
  - Each entry also carries its confirming CRLs and a stored revocation status (`Unconfirmed`, `Confirmed`, `Revoked`).
- #181 decodes CRL entries (`RevokedCertificate`) but deliberately leaves CRL signature verification to this issue.
- #188 made event class-map registration all-or-nothing. New registration checks in this plan follow that rule.

### Format freeze (Layer 1)

- **Genesis:** `SHA-256(tag ‖ UTF-8 AggregateType of version 1 ‖ 0x00 ‖ AggregateId in big-endian RFC 9562 order)`. The tag stays as it is. `AggregateType` is read from the stored bytes, so the library and an independent verifier always agree.
- Anchor leaves, the sealing-anchor lookup and the completeness check keep using the configured aggregate type name. Renaming an aggregate class therefore splits its anchor coverage between the old and the new name. This is documented as a limitation; it does not affect chain verification.
- **Seal-mode binding (exact):** `Hash = SHA-256(canonical ‖ binding? ‖ PreviousHash ‖ mode)`, where `mode` is one byte: `0x01` for `Append` and `0x02` for `Retroactive`. The canonical form is self-delimiting through its BSON length prefix, and the previous hash has a fixed length, so the input stays unambiguous. The stored `SealMode` element remains, and the verifier hashes with the stored value.
- The format version stays 1 and no data is migrated, because the draft format was never publicly reachable (#154 delivery rules).

### Verification (Layer 2)

- Extends `EventStreamVerifier<TAggregate>` and `StreamVerificationResult`. Per-version entries are returned as a list, so memory grows with stream length. This is acceptable for an explicit verification call, and the documentation says so.
- Sealing anchors are resolved with one read of the stream's leaves (the existing `(aggregateType, aggregateId, anchor sequence)` index) and of their anchors, instead of one `SealingAnchorLookup` call per version.
- Only completed anchors count, both as coverage and in the truncation and rewrite comparison. Leaves of anchors in other states are ignored.
- Event anchor verification starts with the first completed anchor that covers the event. When none of its tokens is valid, for example because one was revoked, it continues with the next covering anchor, so the evidence degrades to that anchor's time. Any other failure ends it.
- **Token validation:** the receipt-time checks in `Rfc3161TimestampAuthority` move into a shared validator, so receipt and verification cannot diverge. Verification builds the chain at the token's time, from the stored certificates, with revocation mode `NoCheck`.
- **Verification configuration** (new, module-level; its public name is settled in layer 6):
  - Trust settings per authority name: trusted roots and an optional required policy ID. RFC 3161 authorities registered in the same container contribute their `TrustedRoots` and `PolicyId`. Custom authorities and verification-only containers configure them explicitly. Trust settings with no roots use the system trust store, as on receipt. Only an authority name with no trust settings at all is reported as untrusted.
  - The revocation grace period: the anchoring grace period when anchoring is registered, otherwise 24 hours.
  - The anchor collection names come from the registered `IntegrityAnchoringOptions`. A new registration configures all of the above without starting the anchoring run, for verification-only containers.
- **Evidence:** verification resolves anchors through `AnchorEvidenceResolver`. It reports an `AnchorEvidenceException` as a failure of the affected token, so the other tokens of the anchor are still verified.
- **Revocation:** .NET offers no API to feed stored CRLs into `X509Chain`, so the chain is built with `NoCheck`. Revocation is then decided from the stored confirming CRLs:
  - The stored `RevocationStatus` is only reported for comparison. Anyone with write access to the anchors could change it.
  - Each CRL's `TBSCertList`, signature algorithm and signature are extracted with `System.Formats.Asn1`, beside `CertificateRevocationListHeader` and `RevokedCertificate`. The signature is verified against the issuer certificate's public key from the stored chain, with the BCL's `RSA`/`ECDsa` `VerifyData`. Nothing cryptographic is implemented in-house, as #154 requires.
  - Signature algorithms outside the receipt-time policy are rejected, including MD5, SHA-1 and keys below the minimum sizes.
  - A CRL counts only for the distribution point and issuer it covers, as at stamping time.
  - Revoked entries are matched with #181's rule: issuer and serial number, using the earlier of `revocationDate` and `invalidityDate`, with `certificateHold` treated as revoked.
  - The grace period comes from the verification configuration (see above). It only guards the conclusion that nothing is revoked, so a revoking CRL issued any time after the token's time decides.
  - Certificates of indirect CRL issuers are not captured (#158), so their CRLs are unverifiable, and tokens that depend on them stay unconfirmed. The documentation names this limitation.
- The independent procedure checks confirming CRL signatures with `openssl crl -CAfile` and applies the revocation rule to their entries, because OpenSSL rejects a CRL issued after `-attime`.
- Without a trusted head, deleting the newest anchors together with the streams they cover is undetectable. The documentation states this and recommends replicating anchors through the completion hook.
- Anchor-chain verification is module-level, like anchoring itself. Stream and event verification are per aggregate type.

### Checkpoints and read model (Layer 3)

- The chain hash is stored on `CheckpointDocument<TAggregate>` through a new non-public member, mapped with ignore-if-null, following the `Event<TAggregate>.Integrity` pattern. The public API of `CheckpointDocument<TAggregate>` does not change. The member is mapped whether or not protection is enabled, as `Event<TAggregate>.Integrity` is, so a class map the event store registered for an unprotected store can be reused by a protected one.
- Replay starts from the stream's start and never from a checkpoint, because checkpoints are the data under test. It uses the same replay rules as `PersonalDataEraser`'s rebuild. Replay starts with the stored read model's `CreatedUtc`, as the rebuild does, or with the earliest checkpoint's when the read model is missing, because replay does not produce it. The states are compared without `CreatedUtc`, and the result reports separately whether the read model and the checkpoints agree on it (`CreationTimeConsistent`).
- Comparison uses the BSON serialization of the replayed aggregate and the stored document, so it does not depend on equality members of consumer aggregates.
- `RegisterClassMaps` registers the checkpoint class map with `AutoMap`, which skips non-public members. The new member therefore needs an explicit mapping. With protection enabled, a `CheckpointDocument<TAggregate>` class map that the event store did not register itself is rejected, even if it maps the chain-hash member. This differs from the `Event<TAggregate>` base map in `EnsureEventBaseMapsIntegrity`, which is accepted when it maps the integrity member. Class maps are process-global, so the event store records the checkpoint class maps it registered, for example in a process-wide set, and reuses them in later registrations, such as a separate erasure container or the test suite. The check runs before anything is registered (#188).

### Completeness check (Layer 4)

- An explicit audit operation per aggregate type. It walks the streams in `AggregateId` order, reading each event's version, seal mode and `CreatedUtc` through a projection on the `(AggregateId, Version)` index. It merges this walk with the completed leaves of the type, so leaves without events reveal deleted streams. Its cost grows with events and leaves. Findings are streamed (`IAsyncEnumerable`), so memory grows only with the largest stream and with the number of completed anchors, held as one small entry each.
- The age of the oldest unanchored event is based on `CreatedUtc`, which is caller-supplied. The documentation says so, and points to the anchoring delay alert (#158) as the trustworthy operational signal.

### Specification and reference verifier (Layer 5)

- The specification is new at `docs/tamper-evidence-specification.md`. Test vectors are new under `spec/tamper-evidence/v1/`, and the reference verifier is new under `tools/tamper-evidence-verifier/`. None of them ship in the NuGet package.
- The reference verifier reads concatenated raw BSON documents, which is the format `mongodump` writes, for events, anchors, leaves, and the evidence and evidence-chunk collections. It includes its own minimal BSON element walker, because the hash input is the raw bytes and decoding and re-encoding them would change them.
- Evidence is chunked at 4 MiB (`AnchorEvidenceStore.ChunkSize`). The chunked item is therefore generated from a length and a byte pattern, by both the .NET test and the reference verifier, and is never committed.
- The token vector comes from the in-process test authority of #158, with its self-signed test CA and CRL committed alongside it.
- The tests that run the reference verifier start `python3` and `openssl` as processes and live in an `.Integration` namespace. On CI (`ubuntu-latest`, where both are present), a missing tool fails the test. Locally, it is reported as ignored.

### Public release (Layer 6)

- The following become public. The layer-6 review checks the surface as a whole; removing an item from it is a plan change, not a review outcome:
  - `WithIntegrityProtection`, the `WithEvent` overload with `EventRegistrationBuilder<TAggregate, TEvent>`, and `WithIntegrityAnchoring` with `IntegrityAnchoringBuilder`
  - `ITimestampAuthority` with `GetRevocationListsAsync` and `RevocationLists`, which custom authorities must implement (#181), `TimestampToken`, `Rfc3161TimestampAuthorityOptions` and `TimestampAuthorityException`
  - `CompletedAnchor` and `AnchorTimestamp` with their revocation fields, `TimestampRevocationStatus` and `TimestampConfirmationFailure`
  - the grace period and unconfirmed-delay threshold settings on the anchoring builder
  - the verification configuration and its verification-only registration
  - leaf access for completion hooks
  - the verification and erasure interfaces with their request and result types, the sealing status, and `PersonalDataErased<TAggregate>`
- Stays internal: the anchoring run, the stores (including the evidence store and `AnchorTimestampReference`), the candidate source and `WithAnchorCandidates`, the revocation confirmer, the sealing sweep, and `Event<TAggregate>.Integrity`.
- Erasure gets its own interface, separate from verification, so it can be resolved from a separate container with its own credentials, as the documentation recommends.
- Besides the items in #160, the documentation covers the following from #171 and #181:
  - the evidence collections, which need insert and find for the anchoring role
  - revocation confirmation, its grace period and the unconfirmed-delay alert
  - that a token marked revoked degrades its anchor's evidence to the next anchor's time
- Sealing settings (chunk size, sweep interval, retry delay) become public options of `WithIntegrityProtection`.

### Delivery

- Lower layers' PRs carry `skip-changelog`, and the top layer's PR carries `enhancement` and produces the release-notes entry for making the feature public.

### Stack Design

1. `0160-tamper-evidence-verification-api-format`: genesis from the stored version-1 `AggregateType` and seal-mode binding in the hash input. Depends on trunk.
2. `0160-tamper-evidence-verification-api-verification`: per-version stream verification results, seal-mode transitions, truncation and rewrite detection against anchored leaves, event anchor verification with stored-token validation, including CRL signature and revocation checks, and anchor-chain verification. Depends on the format layer.
3. `0160-tamper-evidence-verification-api-derived-data`: checkpoint chain hashes and the derived-data check with replay and compare. Depends on the verification layer.
4. `0160-tamper-evidence-verification-api-completeness`: the completeness check per aggregate type. Depends on the derived-data layer for stack order, and on the verification layer's anchor-chain verification for code.
5. `0160-tamper-evidence-verification-api-specification`: the specification, the test vectors and the Python reference verifier, with tests that run it. Depends on the completeness layer for stack order, and on the frozen format and verification semantics below it.
6. `0160-tamper-evidence-verification-api-public`: the public API review and visibility change, the stable format marking and the documentation. Depends on the specification layer.
