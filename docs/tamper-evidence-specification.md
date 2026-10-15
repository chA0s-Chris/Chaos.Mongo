# Tamper Evidence Specification

**Format version 1.** Status: draft until the tamper-evidence feature is released; then this version is stable and changes only through a new format version.

This document defines, at byte level, the evidence that `Chaos.Mongo.EventStore` stores for tamper-evident event streams, so it can be verified without Chaos.Mongo and without .NET: with any BSON library, or the raw bytes, plus OpenSSL. The [reference verifier](../tools/tamper-evidence-verifier/verify.py) implements it in Python with the standard library only, and the [test vectors](../spec/tamper-evidence/v1) pin its results.

## Table of Contents

- [Notation](#notation)
- [Event Documents](#event-documents)
- [Canonical Form](#canonical-form)
- [Personal Data Commitments](#personal-data-commitments)
- [Chain Rule](#chain-rule)
- [Verifying a Stream](#verifying-a-stream)
- [Anchors](#anchors)
- [Evidence Store](#evidence-store)
- [Timestamp Tokens](#timestamp-tokens)
- [Revocation](#revocation)
- [Verifying Independently](#verifying-independently)
- [Limitations](#limitations)

## Notation

- `‖` concatenates byte strings. `SHA-256(x)` is the 32-byte SHA-256 digest of `x`.
- `int32be(n)` and `int64be(n)` are big-endian two's complement integers of 4 and 8 bytes.
- `utf8(s)` is the UTF-8 encoding of a string without terminator.
- `uuid(id)` is the 16 bytes of a UUID in RFC 9562 order: the bytes of BSON binary subtype `0x04`.
- BSON follows the [BSON specification 1.1](https://bsonspec.org/spec.html). "Raw bytes" are the bytes as stored, never decoded and re-encoded: re-encoding can change types, for example `int64` to `int32`.
- Element names are the names written by Chaos.Mongo. Element names of consumer events follow the consumer's class maps and play no role here except for personal-data paths.

## Event Documents

Each event is one document of the events collection, `{prefix}_Events`. The elements this specification relies on:

| Element | Type | Meaning |
|---|---|---|
| `_id` | binary `0x04` | The event identifier. |
| `_t` | string, or array of strings | The discriminator; for a class hierarchy, the last element is the concrete one. |
| `AggregateId` | binary `0x04` | The stream. |
| `AggregateType` | string | The aggregate type name when the event was written. |
| `Version` | int64 | The position in the stream, contiguous from 1. Readers accept int32 and integral doubles. |
| `CreatedUtc` | date | A time the caller asserts. It is not trustworthy evidence; anchors are. |
| `_integrity` | document | The integrity data of a sealed event. |

`_integrity` holds the following elements; their order does not matter, because the hash input excludes the element:

| Element | Type | Meaning |
|---|---|---|
| `FormatVersion` | int32 | `1`. |
| `Algorithm` | string | `"SHA-256"`. |
| `PreviousHash` | binary `0x00`, 32 bytes | The predecessor's hash, or the genesis value for version 1. |
| `Hash` | binary `0x00`, 32 bytes | The event's chain hash. |
| `SealMode` | string | `"Append"` when the event was sealed while it was appended, `"Retroactive"` when it was stored without integrity data first and sealed later. |
| `PersonalData` | array of documents, optional | The commitments to personal data values, in recorded order; absent when none is recorded. |

Each `PersonalData` entry holds `Path` (string, a dotted element path), `Salt` (binary `0x00`, 32 bytes; absent once the value is erased) and `Commitment` (binary `0x00`, 32 bytes). Readers ignore unknown elements of `_integrity` and of its entries.

`_integrity` is the last element of an appended event. A retroactively sealed event gets it appended by `$set` on a document without it, which leaves every other byte unchanged.

## Canonical Form

The canonical form of an event is computed from its raw bytes:

1. Remove the first top-level element named `_integrity`: cut its bytes (type byte, name, value) and decrease the document's length prefix by their number.
2. When `PersonalData` is present, replace the value of every recorded path by binary subtype `0x00` holding the entry's 32-byte `Commitment`. The element keeps its name and its position; its type byte becomes `0x05`. The length prefixes of the enclosing documents are corrected. No other byte changes.

A path is resolved segment by segment: each segment addresses the first element of that name in the current document, elements whose names contain a dot are never addressed, and every segment but the last must address an embedded document (type `0x03`). Arrays are not traversed. A recorded path that does not resolve makes the event malformed.

## Personal Data Commitments

For a recorded path whose element has BSON type `t` and value bytes `v` (the bytes after the element name, for example the length prefix, characters and terminator of a string, or an embedded document as a whole):

```text
Commitment = SHA-256(Salt ‖ t ‖ v)
```

`t` is one byte, so a value whose type changes while its bytes stay the same no longer matches. A `null` value has no value bytes. The recorded paths are bound into the chain hash:

```text
Binding = utf8("Chaos.Mongo.EventStore/integrity/personal-data/v1") ‖ utf8(path₁) ‖ 0x00 ‖ … ‖ utf8(pathₙ) ‖ 0x00
```

in recorded order. Without the binding, the canonical form of an event would equal a document that genuinely stores a commitment's bytes in place of a value.

Erasure sets a recorded value to BSON `null` and removes its salt; the commitment stays, so the canonical form and the chain hash are unchanged.

## Chain Rule

```text
Genesis = SHA-256(utf8("Chaos.Mongo.EventStore/integrity/genesis/v1") ‖ utf8(AggregateType of version 1) ‖ 0x00 ‖ uuid(AggregateId))
Hash    = SHA-256(Canonical ‖ Binding ‖ PreviousHash ‖ Mode)
```

- `AggregateType` is read from the stored version-1 document, so renaming an aggregate class does not change the genesis value.
- `Binding` is present only when `PersonalData` is present.
- `Mode` is one byte: `0x01` for `Append`, `0x02` for `Retroactive`. Changing a stored `SealMode` therefore breaks the hash, and anchored heads authenticate the mode of every earlier event.
- The input is unambiguous: the canonical form is self-delimiting through its length prefix, `Binding` starts with its tag, and `PreviousHash` and `Mode` have fixed lengths.

## Verifying a Stream

Read the stream's events in version order and keep the expected previous hash, starting at the genesis value. For each expected version, in this order, the first failing check breaks the stream at that version:

| Failure | Check |
|---|---|
| `VersionGap` | The next event has the expected version. |
| `MissingAggregateType` | Version 1 stores `AggregateType` as a string. |
| `NotSealed` | The event has `_integrity`. |
| `MalformedIntegrity` | `_integrity` has every required element with its type, a defined seal mode, and, if present, well-formed `PersonalData`: 32-byte commitments and salts, distinct non-empty paths none of which is a prefix of another followed by a dot, each resolving in the document. |
| `UnsupportedFormat` | `FormatVersion` is 1 and `Algorithm` is `"SHA-256"`. |
| `PreviousHashMismatch` | `PreviousHash` equals the expected previous hash. |
| `HashMismatch` | The recomputed chain hash equals `Hash`. |
| `PersonalDataMismatch` | Every value with a salt matches its commitment. |
| `ErasedPersonalDataPresent` | Every value without a salt is `null`. |

The verified event's `Hash` becomes the next expected previous hash. A stream is intact when no check fails.

**Erasure records.** The erasure event has the discriminator `Chaos.Mongo.EventStore.PersonalDataErased` and holds `Reference` (string) and `Entries`, an array of `{ Version, Paths }`. Its records are trusted once its own hash verified, and only for versions below its own. A recorded value without a salt is *erased with a record* when a later erasure event lists its version and path, and *erased without a record* otherwise. Unrecorded erasures do not break the chain; they are reported.

**Seal-mode transitions.** A version whose seal mode is `Retroactive` while its predecessor's is `Append` is reported as an informational finding. It is either a legitimate gap in protection or a suffix re-sealed after tampering; only anchors tell them apart.

**Truncation and rewrites.** A consistent chain does not reveal deleted newest events or a deleted stream. Against the stream's leaves in completed anchors, an intact chain breaks at the first missing version when an anchored version no longer exists (`AnchoredVersionMissing`), and at the anchored version when its stored hash differs from the leaf's (`AnchoredHashMismatch`).

## Anchors

An anchor timestamps a Merkle tree over the heads of the streams that changed since the previous anchor. It is stored in the `IntegrityAnchors` collection, its leaves in `IntegrityAnchorLeaves`.

**Leaves.** A leaf is a stream head:

```text
Leaf = int32be(len(utf8(AggregateType))) ‖ utf8(AggregateType) ‖ uuid(AggregateId) ‖ int64be(Version) ‖ Hash
```

`AggregateType` is the aggregate type name configured when the head was anchored, `Version` the head version and `Hash` its chain hash. Each stream contributes one leaf per anchor, for its highest new version. Leaves are sorted by their bytes in ascending order.

**Merkle tree.** The Merkle Tree Hash of [RFC 9162, section 2.1](https://www.rfc-editor.org/rfc/rfc9162#section-2.1): leaf hashes are `SHA-256(0x00 ‖ Leaf)`, node hashes `SHA-256(0x01 ‖ Left ‖ Right)`, and a tree of `n` leaves splits at the largest power of two smaller than `n`. Inclusion proofs and their verification follow RFC 9162, sections 2.1.3.1 and 2.1.3.2.

**Anchor documents.** A committed anchor holds:

| Element | Type | Meaning |
|---|---|---|
| `_id` | binary `0x04` | The anchor identifier. |
| `Status` | string | `"AwaitingTimestamp"` or `"Completed"`. Only completed anchors are evidence. |
| `Sequence` | int64 | The position in the chain of anchors, contiguous from 1. |
| `FormatVersion` | int32 | `1`. |
| `HashAlgorithm` | string | `"SHA-256"`. |
| `PreviousDigest` | binary `0x00`, 32 bytes | The previous anchor's digest; 32 zero bytes for the first anchor. |
| `MerkleRoot` | binary `0x00`, 32 bytes | The root over the leaves. |
| `AnchorDigest` | binary `0x00`, 32 bytes | The digest the timestamp tokens cover. |
| `LeafCount` | int32 | The number of leaves. |
| `Timestamps` | array of documents | The timestamp entries, see [Timestamp Tokens](#timestamp-tokens). |

Anchors without `Sequence` are attempts that never committed and are ignored.

**Anchor digest.**

```text
AnchorDigest = SHA-256(int32be(FormatVersion) ‖ int64be(Sequence) ‖ PreviousDigest ‖ MerkleRoot)
```

**Chain of anchors.** Sorted by `Sequence`, the sequence numbers must be contiguous from 1, each `PreviousDigest` must equal the predecessor's `AnchorDigest`, and each `AnchorDigest` must recompute. A trusted head — the sequence number and digest of an anchor kept outside the database, for example in WORM storage fed by the completion hook — must be present unchanged.

**Leaf documents.** Each holds `AnchorId`, `AnchorSequence`, `Index` (the zero-based position among the sorted leaves), `AggregateType`, `AggregateId`, `Version`, `Hash` and `Proof`, an array of the inclusion proof's 32-byte hashes. A leaf counts only when its anchor exists, is completed and has the same sequence number.

**Coverage.** An event of version `v` is anchored by the completed anchor with the lowest sequence number that has a leaf for its stream with a version at or above `v`; the chain from `v` to that version links the event to the leaf. For a retroactively sealed event, this anchor is the time since which it is protected, not its creation.

## Evidence Store

Timestamp tokens, certificates and CRLs are stored once per distinct content in `IntegrityAnchorEvidence`, keyed by their SHA-256:

- An item of at most 4,194,304 bytes is stored inline: `{ _id: SHA-256(item), Length: int32, Data: binary }`.
- A larger item is split into chunks of 4,194,304 bytes, the last one shorter: `{ _id: SHA-256(item), Length: int32, ChunkCount: int32 }`, with one document per chunk in `IntegrityAnchorEvidenceChunks`: `{ _id: { Hash: SHA-256(item), Index: int32 }, Data: binary }`.

An item is the concatenation of its chunks in index order; its length and SHA-256 must match. Missing or mismatching evidence makes the token that references it invalid.

## Timestamp Tokens

Each entry of an anchor's `Timestamps` references one RFC 3161 token by hash:

| Element | Type | Meaning |
|---|---|---|
| `Authority` | string | The authority name. |
| `TokenHash` | binary | The token: a DER `TimeStampToken`, CMS `SignedData` over a `TSTInfo`. |
| `CertificateChainHashes` | array of binary | The DER certificates from the signing certificate to the root, in order. |
| `CertificateRevocationListHashes` | array of binary | The CRLs captured at stamping time. |
| `TimestampUtc` | date | The token's `genTime`, truncated to milliseconds. |
| `RevocationStatus` | string | `"Unconfirmed"`, `"Confirmed"` or `"Revoked"`, as last recorded. Verifiers recompute it and only report the stored one. |
| `ConfirmingRevocationListHashes` | array of binary | The CRLs that confirm the chain's revocation status, see [Revocation](#revocation). |

A token is valid evidence for an anchor when:

1. Its message imprint is SHA-256 over the `AnchorDigest`, and its signature and signing-certificate attribute (`ESSCertID` or `ESSCertIDv2`) verify.
2. Its signed content is exactly one `TSTInfo`, and it carries no countersignature.
3. The signing certificate's extended key usage contains only `id-kp-timeStamping`, its key usage, if present, permits signatures, and it carries no extension twice.
4. A `tsa` name in the token corresponds to a subject name of the signing certificate.
5. It is signed with SHA-256 or stronger and an RSA key of at least 2048 or an ECDSA key of at least 256 bits.
6. When a policy is required for the authority, it is issued under that policy.
7. Its `genTime`, truncated to milliseconds, equals `TimestampUtc`.
8. The stored chain is a valid chain to a trusted root at `genTime`.
9. Its chain is not revoked at or before `genTime`, see below.

An anchor is verified when its digest recomputes and at least one of its tokens is valid.

## Revocation

The CRLs captured at stamping time are usually issued before the token's time and show no revocation after their issue. The confirming CRLs close that gap: they are issued at least a grace period after the token's time (24 hours unless configured otherwise).

**Ordering.** For each non-root certificate of the chain, in chain order, and each of its CRL distribution points with an HTTP or HTTPS location, in extension order, there is one source: its locations and the issuer its CRL must come from — the certificate's issuer, or none when the point names a CRL issuer. Sources with the same locations (joined by spaces) and issuer are listed once. A certificate that names distribution points, but none with an HTTP location covering all reasons, has no valid sources. `ConfirmingRevocationListHashes` holds one CRL per source, in this order.

**A confirming CRL counts** for its source when the source names its issuer, the CRL's `issuer` equals it, its `thisUpdate` is at or after `genTime` plus the grace period, and its signature verifies against the certificate of that issuer in the stored chain. A CRL of an indirect CRL issuer cannot be verified, because the issuer's certificate is not stored.

**Revocation status.**

- *Revoked* when a CRL that counts has an entry revoking a non-root certificate of the chain at or before `genTime`: same serial number, an issuer the entry names (the CRL issuer unless a `certificateIssuer` extension names others), and the earlier of `revocationDate` and `invalidityDate` at or before `genTime`; `certificateHold` counts as revoked, `removeFromCRL` lifts it. A revoked token is invalid; its anchor's evidence degrades to the next anchor's time.
- *Confirmed* when a CRL counts for every source, or the chain names no distribution points.
- *Unconfirmed* otherwise. An unconfirmed token is still valid.

## Verifying Independently

Export the collections with `mongodump`, which writes concatenated raw BSON documents:

```bash
mongodump --db=app --collection=Orders_Events --out=evidence
```

and likewise `IntegrityAnchors`, `IntegrityAnchorLeaves`, `IntegrityAnchorEvidence` and `IntegrityAnchorEvidenceChunks`. Then run the reference verifier with the trusted roots of the timestamp authorities:

```bash
python3 tools/tamper-evidence-verifier/verify.py check --events evidence/app/Orders_Events.bson --aggregate-type Order --aggregate-id 0f5a9c2e-6b1d-4c3a-9e2f-1a2b3c4d5e01 --anchors evidence/app/IntegrityAnchors.bson --leaves evidence/app/IntegrityAnchorLeaves.bson --evidence evidence/app/IntegrityAnchorEvidence.bson --evidence-chunks evidence/app/IntegrityAnchorEvidenceChunks.bson --trusted-root tsa-root.pem
```

It verifies the stream, its anchored leaves and inclusion proofs, the chain of anchors and the anchor digests from the raw bytes. It verifies each token with OpenSSL, writing the token, the stored chain as PEM and the trusted roots to files:

```bash
openssl ts -verify -digest <AnchorDigest in hex> -in token.der -token_in -CAfile roots.pem -untrusted chain.pem -attime <genTime in Unix seconds>
openssl crl -inform DER -in confirming.der -CAfile issuer.pem -noout
```

The first command checks the imprint, the signature, the signing-certificate attribute, the timestamping usage and the chain at the token's time; the second the signature of a confirming CRL. The verifier then applies the revocation rule above to the CRL entries.

**Test vectors.** [`spec/tamper-evidence/v1`](../spec/tamper-evidence/v1) holds:

- `streams.bson` with `streams.json`: raw events of appended, personal-data (with recorded and unrecorded erasures), retroactively sealed, tampered and renamed streams, with their genesis values and expected verification results.
- `merkle.json`: trees of one to seven leaves with their encoded leaves, roots and inclusion proofs.
- `anchors.bson`, `leaves.bson`, `evidence.bson`, `tsa-root.pem` with `anchors.json`: two chained anchors with their digests and sample tokens of a test authority, one confirmed by a confirming CRL and one unconfirmed.
- `evidence-chunked.json`: an item longer than two chunks, defined by its length and byte `i = (i × 31 + 7) mod 256`, with its hash and chunk hashes; the item is generated, not committed.

```bash
python3 tools/tamper-evidence-verifier/verify.py vectors spec/tamper-evidence/v1
```

## Limitations

- **Evidence is relative to anchors.** A chain only proves consistency up to its last event, and the chain of anchors up to its last anchor. Deleting the newest events of a stream is detected against completed anchors; deleting the newest anchors together with the streams they cover is detected only against a trusted head kept outside the database.
- **Retroactive sealing** proves "unchanged since anchor X", never "since creation".
- **`CreatedUtc` is asserted by the caller.** The trustworthy time bound is the token time of the anchor that covers an event.
- **Renaming an aggregate class** keeps chains verifiable, because the genesis value uses the stored type name, but leaves are recorded under the type name configured at anchoring, so coverage splits between the old and the new name.
- **Indirect CRL issuers** cannot be verified, so tokens whose chains depend on them stay unconfirmed.
