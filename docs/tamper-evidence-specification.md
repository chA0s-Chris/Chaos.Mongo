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
- Where a type is listed for an element, the element must have exactly that type; readers must not convert types, so every verifier reads the same data. Binary subtype `0x02`, the old binary subtype, is not subtype `0x00`: an element of subtype `0x02` where `0x00` is listed is malformed, even when its inner length matches.
- A value is *readable* when a string, JavaScript code or symbol is terminated and valid UTF-8, the strings of a regular expression or DBPointer are valid UTF-8, a boolean is `0x00` or `0x01`, a binary of subtype `0x03` or `0x04` is 16 bytes long and a binary of subtype `0x02` has an inner length that matches. A document is readable when every element name and value in it and in its embedded documents and arrays, at any depth, is readable and no element name repeats in it or in an embedded document. `_integrity` and the documents of the anchor and evidence collections must be readable as a whole; elsewhere only the values a verifier reads must be, as stated where they are read.
- Element names of arrays are ignored, as MongoDB and its drivers ignore them: they are neither decoded nor compared.
- *MongoDB order* is the order of an ascending MongoDB sort on an element: a missing element, `null` and an empty array first, then numbers by value (int32, int64, double and decimal128 alike, NaN first), then every other type; a non-empty array sorts by its smallest element. Other types are not ordered among themselves here, although MongoDB orders them by type and value: where documents with such values meet, only the broken version is defined, not the failure reported for it. Elements that sort alike keep their stored order. Within an array, a nested array counts as an array.
- *Selected by a query* follows MongoDB's matching: an element selects a document when it equals the queried value or is an array that holds it, and a range condition such as "at least 1" holds when the element or an item of its array is a number in the range. A document selected through an array then fails to read with the element's exact type. Element names are compared as bytes.

## Event Documents

Each event is one document of the events collection, `{prefix}_Events`. The elements this specification relies on:

| Element | Type | Meaning |
|---|---|---|
| `_id` | binary `0x04` | The event identifier. |
| `_t` | string, or array of strings | The discriminator; for a class hierarchy, the last element is the concrete one. |
| `AggregateId` | binary `0x04` | The stream. |
| `AggregateType` | string | The aggregate type name when the event was written. |
| `Version` | int64 | The position in the stream, contiguous from 1. Readers accept int32, int64 and integral doubles within the int64 range; other types, including decimal128 and strings, are no versions. |
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

Each `PersonalData` entry holds `Path` (string, a dotted element path), `Salt` (binary `0x00`, 32 bytes; absent once the value is erased) and `Commitment` (binary `0x00`, 32 bytes). Readers ignore unknown elements of `_integrity` and of its entries, as long as `_integrity` is readable.

Every listed element must have exactly the listed type: `FormatVersion` an int32, not an int64 or a double; binary elements subtype `0x00`; `SealMode` the string `"Append"` or `"Retroactive"` exactly, not a number. An erased `Salt` is absent; a `null` or otherwise typed `Salt` makes the integrity data malformed, and so does an `_integrity` document that is not readable (see [Notation](#notation)), for example because it or one of its entries repeats an element name or holds a string that is not valid UTF-8, also in an unknown element.

`_integrity` is the last element of an appended event. A retroactively sealed event gets it appended by `$set` on a document without it, which leaves every other byte unchanged.

## Canonical Form

The canonical form of an event is computed from its raw bytes:

1. Remove the first top-level element named `_integrity`: cut its bytes (type byte, name, value) and decrease the document's length prefix by their number.
2. When `PersonalData` is present, replace the value of every recorded path by binary subtype `0x00` holding the entry's 32-byte `Commitment`. The element keeps its name and its position; its type byte becomes `0x05`. The length prefixes of the enclosing documents are corrected. No other byte changes.

A path is resolved segment by segment: each segment addresses the first element of that name in the current document, elements whose names contain a dot are never addressed, and every segment but the last must address an embedded document (type `0x03`). Arrays are not traversed. A recorded path that does not resolve makes the event malformed, and so does an element name that is not valid UTF-8 in an embedded document a path descends into.

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

Read the stream's events, the documents an `AggregateId` query for the stream selects, in MongoDB order of `Version`, and keep the expected previous hash, starting at the genesis value. A document whose `Version` is missing or not a version therefore breaks the stream at version 1 when it sorts before the numbers, and after the last readable version otherwise. For each expected version, in this order, the first failing check breaks the stream at that version:

| Failure | Check |
|---|---|
| `MalformedIntegrity` | Every top-level element name of the next event is valid UTF-8. |
| `VersionGap` | The next event has the expected version. |
| `MissingAggregateType` | Version 1 stores `AggregateType` as a string of valid UTF-8. |
| `NotSealed` | The event has `_integrity`. |
| `MalformedIntegrity` | `_integrity` is a document with every required element of its exact type and a defined seal mode, and `PersonalData`, if present, is an array of entries of their exact types. |
| `UnsupportedFormat` | `FormatVersion` is 1 and `Algorithm` is `"SHA-256"`. |
| `PreviousHashMismatch` | `PreviousHash` equals the expected previous hash. |
| `MalformedIntegrity` | `PersonalData`, if present, is well formed: 32-byte commitments and salts, distinct non-empty paths none of which is a prefix of another followed by a dot, each resolving in the document, see [Canonical Form](#canonical-form). |
| `HashMismatch` | The recomputed chain hash equals `Hash`. |
| `PersonalDataMismatch` or `ErasedPersonalDataPresent` | Every value with a salt matches its commitment, and every value without a salt is `null`; the first entry in recorded order that fails decides which. |

The verified event's `Hash` becomes the next expected previous hash. A stream is intact when no check fails. A broken stream reports the events verified before the break, with their seal-mode transitions and personal-data statuses; unrecorded erasures are reported only for an intact stream.

**Erasure records.** The erasure event has the discriminator `Chaos.Mongo.EventStore.PersonalDataErased` and holds `Reference` (string) and `Entries`, an array of `{ Version, Paths }`. Its records are trusted once its own hash verified, and only for versions below its own. They are read as the library reads them: every item of `Entries` must be a readable value, an entry that is not a document is skipped, its `Version` is read first, and only an entry with a version below the erasure event's has its `Paths` read, whose items must be readable values too. A recorded value without a salt is *erased with a record* when a later erasure event lists its version and path, and *erased without a record* otherwise. Unrecorded erasures do not break the chain; they are reported.

**Seal-mode transitions.** A version whose seal mode is `Retroactive` while its predecessor's is `Append` is reported as an informational finding. It is either a legitimate gap in protection or a suffix re-sealed after tampering; only anchors tell them apart.

**Truncation and rewrites.** A consistent chain does not reveal deleted newest events or a deleted stream. The verified events are compared with the stream's leaves: the leaf documents that queries for the stream's `AggregateType` and `AggregateId` and for a `Version` of at least 1 select. A leaf that cannot be read, or that belongs to a committed anchor (one with `Sequence`) that cannot be read, breaks the stream at its `Version`, or at version 1 when that is not an integer (`AnchoredLeafInvalid`). Leaves of missing or uncommitted anchors are ignored: they belong to discarded attempts. The leaves of completed anchors with the leaf's sequence number are compared in anchor sequence order: a leaf whose inclusion proof does not lead to its anchor's root breaks the stream at the leaf's version (`AnchoredLeafInvalid`); an anchored version that no longer exists breaks it at the first missing version (`AnchoredVersionMissing`); an anchored version whose stored hash differs from the leaf's breaks it at that version (`AnchoredHashMismatch`). The lowest of these versions is reported, the first leaf's on a tie. When the chain itself breaks, only leaves below the break are compared, and an earlier finding replaces the chain's, so a re-sealed prefix shows at its anchored version. A deleted stream whose leaves were deleted with it has no leaves to compare with; the chain of anchors reveals it. Readable leaves are bound to the anchors only by recomputing the anchor roots, so stream verification is complete only together with verifying the chain of anchors. A value the verifier reads from a verified event that is not readable, such as the discriminator or, when it is an array, one of its items, an item of an erasure event's `Entries` or a path of an erasure record it uses, breaks the stream as `MalformedIntegrity` at that event, which is then not counted as verified.

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
| `_id` | binary `0x04`, 16 bytes | The anchor identifier. |
| `Status` | string | `"AwaitingTimestamp"` or `"Completed"` for a valid committed anchor; `"Preparing"` and `"Discarded"` are readable but invalid there. Only completed anchors are evidence. |
| `Sequence` | int64 (int32 accepted) | The position in the chain of anchors, contiguous from 1. |
| `FormatVersion` | int32 | `1`. |
| `HashAlgorithm` | string | `"SHA-256"`. |
| `PreviousDigest` | binary `0x00`, 32 bytes | The previous anchor's digest; 32 zero bytes for the first anchor. |
| `MerkleRoot` | binary `0x00`, 32 bytes | The root over the leaves. |
| `AnchorDigest` | binary `0x00`, 32 bytes | The digest the timestamp tokens cover. |
| `LeafCount` | int32 | The number of leaves. |
| `PeriodStartUtc`, `PeriodEndUtc`, `OldestCandidateUtc`, `CreatedUtc` | date | Operational times; they play no role in verification. |
| `CompletedUtc` | date or `null`, optional | When the anchor completed. |
| `Timestamps` | array of documents, optional | The timestamp entries, see [Timestamp Tokens](#timestamp-tokens); absent means none. |
| `AuthorityFailures` | array of documents, optional | Failed authority requests: `Authority` (string), `FailureCount` (int32), `LastFailure` (string), `LastFailureUtc` (date). |

Anchors without `Sequence` are attempts that never committed and are ignored.

**Anchor digest.**

```text
AnchorDigest = SHA-256(int32be(FormatVersion) ‖ int64be(Sequence) ‖ PreviousDigest ‖ MerkleRoot)
```

**Chain of anchors.** In MongoDB order of `Sequence`, the first failing check breaks the chain at that sequence number:

| Failure | Check |
|---|---|
| `MalformedAnchor` | The document is readable and has the elements above with their types, including those of its timestamp entries. |
| `SequenceGap` | The sequence numbers are contiguous from 1. |
| `MalformedAnchor` | The previous anchor is completed: no anchor is created while one awaits its timestamps, so only the newest may await them. The failure names the previous anchor. |
| `MalformedAnchor` | `Status` is `"AwaitingTimestamp"` or `"Completed"`, and `"Completed"` when a trusted head is given and the sequence number is at most the head's; `FormatVersion` is 1, `HashAlgorithm` is `"SHA-256"` and `AnchorDigest` has 32 bytes. |
| `PreviousDigestMismatch` | `PreviousDigest` equals the predecessor's `AnchorDigest`. |
| `AnchorDigestMismatch` | `AnchorDigest` recomputes. |
| `LeafSetMismatch` | The anchor's stored leaves recompute its `MerkleRoot`: exactly `LeafCount` (at least 1) readable leaf documents whose `AnchorId` is the anchor's `_id`, with the indexes 0 to `LeafCount − 1`, the anchor's sequence number and 32-byte hashes. |

A trusted head — the sequence number and digest of an anchor kept outside the database, for example in WORM storage fed by the completion hook — must then be present unchanged (`TrustedHeadMissing`, `TrustedHeadMismatch`). The completion hook only sees completed anchors, so every anchor up to the head must be completed; otherwise setting an anchor back to awaiting its timestamps would hide its leaves from stream verification. Without a trusted head, the newest anchor's status is not authenticated. Recomputing the roots binds the leaves that stream verification compares streams with to the anchor digests, so deleting or changing leaves together with the streams they cover is detected unless the digests are recomputed too. Digests and roots involve no secret: the chain of anchors proves consistency, not authenticity. Timestamp tokens bound when an anchor digest existed, not who built the anchor: anchors after the trusted head, or all anchors without one, may have been rebuilt or appended consistently by someone with write access, who can obtain genuine tokens for them. Detecting rebuilt anchors needs a trusted head or anchors replicated outside the database. Anchor coverage, as the library's completeness check reports it, counts stored completed anchors and has the same limit. The leaf documents of an anchor are those an `AnchorId` query for its `_id` selects.

**Leaf documents.** Each holds `AnchorId` and `AggregateId` (binary `0x04`, 16 bytes), `AnchorSequence` (int64, int32 accepted), `Index` (int32, the zero-based position among the sorted leaves), `AggregateType` (string), `Version` (int64, int32 accepted, at least 1), `Hash` (binary `0x00`) and `Proof` (an array of binary `0x00`, the inclusion proof's hashes); a leaf document that is not readable cannot be read. A leaf counts only when its anchor exists, is completed and has the same sequence number; how unreadable leaves count is defined for the leaf set above and for streams in [Verifying a Stream](#verifying-a-stream).

**Coverage.** An event of version `v` is anchored by the completed anchor with the lowest sequence number that has a leaf for its stream with a version at or above `v`; the chain from `v` to that version links the event to the leaf. For a retroactively sealed event, this anchor is the time since which it is protected, not its creation. When none of that anchor's timestamp tokens is valid, for example because one was revoked, the library's event anchor verification continues with the next completed anchor that has such a leaf, so the evidence degrades to that anchor's time; any other failure ends it.

## Evidence Store

Timestamp tokens, certificates and CRLs are stored once per distinct content in `IntegrityAnchorEvidence`, keyed by their SHA-256:

- An item of at most 4,194,304 bytes is stored inline: `{ _id: SHA-256(item), Length: int32, Data: binary 0x00 }`.
- A larger item is split into chunks of 4,194,304 bytes, the last one shorter: `{ _id: SHA-256(item), Length: int32, ChunkCount: int32 }`, with one document per chunk in `IntegrityAnchorEvidenceChunks`: `{ _id: { Hash: SHA-256(item), Index: int32 }, Data: binary 0x00 }`.

The `_id` of an item and the `Hash` of a chunk identifier are binary `0x00`; a chunk identifier holds exactly `Hash` and `Index`, in this order. `Length`, `ChunkCount` and `Index` are int32; an int64 or integral double in the int32 range is accepted, any other type or value makes the item malformed, as does `Data` of another type or subtype, old binary `0x02` included, and an item or chunk document that is not readable. A document without `Data` is chunked: it must be longer than one chunk and have exactly as many chunks as its length needs, `ChunkCount = ⌈Length / 4,194,304⌉`, so a tampered count cannot make reading unbounded. An item is the concatenation of its chunks in index order; its length and SHA-256 must match. Missing, malformed or mismatching evidence makes the token that references it invalid.

## Timestamp Tokens

Each entry of an anchor's `Timestamps` references one RFC 3161 token by hash:

| Element | Type | Meaning |
|---|---|---|
| `Authority` | string | The authority name. |
| `TokenHash` | binary `0x00` | The token: a DER `TimeStampToken`, CMS `SignedData` over a `TSTInfo`. |
| `CertificateChainHashes` | array of binary `0x00` | The DER certificates from the signing certificate to the root, in order. |
| `CertificateRevocationListHashes` | array of binary `0x00` | The CRLs captured at stamping time. They must resolve like every referenced item; their content plays no role in verification. |
| `TimestampUtc` | date | The token's `genTime`, truncated to milliseconds. |
| `ObtainedUtc` | date | When the token was obtained; it plays no role in verification. |
| `RevocationStatus` | string | `"Unconfirmed"`, `"Confirmed"` or `"Revoked"`, as last recorded. Verifiers recompute it and check it for consistency, see [Revocation](#revocation). |
| `ConfirmingRevocationListHashes` | array of binary `0x00`, optional | The CRLs that confirm the chain's revocation status, see [Revocation](#revocation); absent means none. |
| `RevocationStatusUtc`, `ConfirmationNotBeforeUtc` | date or `null`, optional | Operational times. |
| `ConfirmationFailureCount` | int32, optional | Failed confirmation attempts; when positive, `LastConfirmationFailure` (string) and `LastConfirmationFailureUtc` (date) are present. |

A token is valid evidence for an anchor when:

1. Its message imprint names `hashAlgorithm` id-sha256, with absent or `NULL` parameters, and a `hashedMessage` equal to the `AnchorDigest` itself (the digest is not hashed again), and its signature and signing-certificate attribute (`ESSCertID` or `ESSCertIDv2`) verify.
2. It is DER-encoded: tags and definite lengths in their shortest form, with tag numbers of 31 and more in the high-tag-number form, and no universal type but `SEQUENCE` and `SET` in constructed form, and no constructed value nested at a depth of 64 or more, counting the outermost value as depth 0; this holds for the token and, separately, for the `TSTInfo`.
3. Its signed content is exactly one `TSTInfo`, it has exactly one signer, and it carries no countersignature: no unsigned attribute of type countersignature (1.2.840.113549.1.9.6), whatever values it holds.
4. The signing certificate's extended key usage extension is critical and contains only `id-kp-timeStamping`, its key usage, if present, permits `digitalSignature` or `nonRepudiation` and nothing else, and it carries no extension twice.
5. A `tsa` name in the token corresponds to a subject name of the signing certificate.
6. MD2, MD4, MD5 and SHA-1 are rejected for its digest and signature algorithms, and an RSA signing key has at least 2048 bits and an ECDSA key at least 256; other key types are not size-checked.
7. When a policy is required for the authority, it is issued under that policy.
8. Its `genTime`, truncated to milliseconds, equals `TimestampUtc`.
9. The chain built from the stored certificates is a valid chain to a trusted root at `genTime` and equals the stored chain, which therefore starts with the signing certificate, names each next certificate's subject as its issuer and ends with a trusted root. Names are linked when they have the same relative distinguished names in order, each with the same attribute types in order, and equal values: a value of type `PrintableString`, `UTF8String`, `IA5String`, `TeletexString` (read as ISO 8859-1) or `BMPString` is compared as text, ignoring the case of ASCII letters, any other value by its encoding.
10. The chain's CRL distribution points are well formed, and a certificate that names distribution points names one with an HTTP or HTTPS location that covers all reasons, see [Revocation](#revocation).
11. Every referenced evidence item resolves: the token, the stored chain, the stamping-time CRLs and the confirming CRLs.
12. Its stored revocation evidence is consistent, see [Revocation](#revocation): anchoring records `"Confirmed"` and `"Revoked"` only from CRLs that justify them, so removing, substituting or partly removing them shows.
13. Its chain is not revoked at or before `genTime`, see below.

Trust settings are per authority name: the trusted roots and, optionally, the required policy. A token of an authority without trust settings is not valid.

An anchor is verified when its digest recomputes and at least one of its tokens is valid.

## Revocation

The CRLs captured at stamping time are usually issued before the token's time and show no revocation after their issue. The confirming CRLs close that gap: they are issued at least a grace period after the token's time (24 hours unless configured otherwise).

**Sources.** For each non-root certificate of the chain and each of its CRL distribution points with an HTTP or HTTPS location, there is one source: its locations, the issuer its CRL must come from — the certificate's issuer, or none when the point names a CRL issuer — and whether a CA certificate (basic constraints with `cA` set) or an end-entity certificate names it. Sources with the same locations (joined by spaces) and issuer are listed once, and such a source counts as named by a CA certificate when any of them is, and likewise by an end-entity certificate. A point covers all reasons when it has no `reasons` field or its `ReasonFlags` set bits 1 to 8 (bit 0 is unused). A certificate that names distribution points, but none with an HTTP location covering all reasons, or whose distribution points are malformed, makes the token invalid, as anchoring rejects such tokens.

**Locations** are compared in normalized form, computed on the text as written: drop the fragment and the user information, lower-case the scheme and the host (an IPv6 host keeps its brackets), drop the port when it is the scheme's default, make an empty path `/`, remove dot segments (RFC 3986, section 5.2.4), and keep the rest of the path and the query as written. A location counts only when its scheme is `http` or `https`, its host is one or more non-empty labels of ASCII letters, digits and hyphens joined by dots, or a bracketed IPv6 address written with hexadecimal digits, colons and dots, and its port, if any, is a decimal number of at most 65535.

The confirming CRLs are matched with the sources by content, not by position: they may be stored in any order, and one CRL may count for several sources.

**A confirming CRL counts** for its source when all of the following hold:

- The source names its issuer, and the CRL's `issuer` equals it. A CRL of an indirect CRL issuer cannot be verified, because the issuer's certificate is not stored.
- Its `thisUpdate` is at or after a time: `genTime` plus the grace period to confirm a source, and after `genTime` to show a revocation, see below.
- It is a complete CRL for the source: it carries no `deltaCRLIndicator`, no extension twice and no critical extension other than `issuingDistributionPoint` and `cRLNumber`; an `issuingDistributionPoint`, if present, sets none of `onlySomeReasons`, `onlyContainsAttributeCerts` and `indirectCRL`, sets `onlyContainsUserCerts` only for a source no CA certificate names and `onlyContainsCACerts` only for a source no end-entity certificate names, and its `distributionPoint`, if present, is a full name that names one of the source's locations.
- Its revoked-certificate entries can be decoded: each is a DER `SEQUENCE` of a serial number in minimal `INTEGER` encoding, a revocation time and optional entry extensions. A CRL with an entry that cannot be decoded counts for no source.
- Its signature verifies against the certificate of its issuer in the stored chain, whose key usage, if present, permits `cRLSign`, under the same policy as tokens: the signature algorithms in the signed part and outside it are equal and are RSA with PKCS #1 v1.5 padding (parameters `NULL` or absent) or ECDSA (no parameters), each with SHA-256, SHA-384 or SHA-512, or RSASSA-PSS with an explicit SHA-2 hash, MGF1 with the same hash, a salt as long as the hash and the default trailer, in this order; the signature value has no unused bits; the issuer's key is an RSA key of at least 2048 or an ECDSA key of at least 256 bits.

**Revocation status.**

- *Revoked* when a CRL that counts for a source, issued after `genTime` (the grace period only guards the conclusion that nothing is revoked), has an entry revoking a non-root certificate of the chain at or before `genTime`: same serial number, an issuer the entry names (the CRL issuer unless a `certificateIssuer` extension names others, for this and every following entry; a `certificateIssuer` without a `directoryName` leaves the issuer unknown, so the entry matches by serial number alone), and the earlier of `revocationDate` and `invalidityDate` at or before `genTime`; `certificateHold` counts as revoked, `removeFromCRL` lifts it. Unknown entry extensions are ignored; of a repeated known one, the last applies. A revoked token is invalid; its anchor's evidence degrades to the next anchor's time.
- *Confirmed* otherwise when a CRL issued at least the grace period after `genTime` counts for every source, or the chain has no sources and no confirming CRL is stored, readable or not.
- *Unconfirmed* otherwise. An unconfirmed token is still valid.

**Consistency.** For a token that is not *Revoked*, a stored status of `"Revoked"` is inconsistent, because no stored CRL shows the revocation, and a stored status of `"Confirmed"` is consistent only when every source that names its issuer has a confirming CRL that counts for it and was issued after `genTime`; the grace period plays no role, so a verification grace period longer than the anchoring one causes no false report. An inconsistent token is invalid.

## Verifying Independently

Export the collections with `mongodump`, which writes concatenated raw BSON documents:

```bash
mongodump --db=app --collection=Orders_Events --out=evidence
```

and likewise `IntegrityAnchors`, `IntegrityAnchorLeaves`, `IntegrityAnchorEvidence` and `IntegrityAnchorEvidenceChunks`. Then run the reference verifier with the trusted roots of the timestamp authorities:

```bash
python3 tools/tamper-evidence-verifier/verify.py check --events evidence/app/Orders_Events.bson --aggregate-type Order --aggregate-id 0f5a9c2e-6b1d-4c3a-9e2f-1a2b3c4d5e01 --anchors evidence/app/IntegrityAnchors.bson --leaves evidence/app/IntegrityAnchorLeaves.bson --evidence evidence/app/IntegrityAnchorEvidence.bson --evidence-chunks evidence/app/IntegrityAnchorEvidenceChunks.bson --trusted-root qualified-tsa=tsa-root.pem --policy qualified-tsa=0.4.0.2023.1.1 --trusted-head 1042:<AnchorDigest in hex>
```

`--trusted-root NAME=PEM` trusts a root for the tokens of one authority, a plain `--trusted-root PEM` for every authority; `--policy NAME=OID` requires a policy for an authority's tokens, `--trusted-head SEQUENCE:DIGEST` checks the chain of anchors against a head kept outside the database, and `--grace-hours` sets the grace period of confirming CRLs. These options, `--leaves` and the evidence files need `--anchors`; without it only the stream is verified. Unreadable input files and invalid options are reported with exit code 2; event and evidence documents that cannot be read are listed under `malformed` and treated as missing. Leaf documents that cannot be read are listed too, but break the stream and the leaf set as defined above, and an anchor document that cannot be read breaks the chain of anchors without being listed.

It verifies the stream, its anchored leaves and inclusion proofs, the chain of anchors with the leaf sets and the anchor digests from the raw bytes. It checks the token's encoding, structure, imprint algorithm, policy, algorithms, key sizes, signing-certificate usages and the stored chain's structure itself, and verifies each token with OpenSSL, writing the token, the stored chain as PEM and the trusted roots to files:

```bash
openssl ts -verify -digest <AnchorDigest in hex> -in token.der -token_in -CAfile roots.pem -untrusted chain.pem -attime <genTime in Unix seconds>
openssl crl -inform DER -in confirming.der -CAfile issuer.pem -noout
```

The first command checks the imprint bytes, the signature, the signing-certificate attribute and the chain at the token's time; the second the signature of a confirming CRL. With `-digest`, OpenSSL compares only the imprint bytes, not their hash algorithm, and it ignores unknown extended key usages, so the verifier checks both itself. OpenSSL rejects a CRL issued after `-attime`, so confirming CRLs are not passed to `openssl ts -verify`: the verifier checks their signatures separately and applies the scope and revocation rules above to them. Two checks are left to the library: whether a `tsa` name in the token corresponds to the signing certificate, and whether the chain OpenSSL builds equals the stored chain; the verifier checks the stored chain's structure instead: it starts with the token's signer, links each certificate to its issuer as item 9 of the token checks defines and ends with a self-issued trusted root.

**Test vectors.** [`spec/tamper-evidence/v1`](../spec/tamper-evidence/v1) holds:

- `streams.bson` with `streams.json`: raw events of appended, personal-data (with recorded and unrecorded erasures), retroactively sealed, tampered and renamed streams, with their genesis values and expected verification results.
- `merkle.json`: trees of one to seven leaves with their encoded leaves, roots and inclusion proofs.
- `anchors.bson`, `leaves.bson`, `evidence.bson`, `tsa-root.pem` with `anchors.json`: two chained anchors with their digests and sample tokens of a test authority, one confirmed by a confirming CRL and one unconfirmed.
- `evidence-chunked.json`: an item longer than two chunks, defined by its length, byte `i` being `(i × 31 + 7) mod 251`, with its hash and chunk hashes; 251 is prime, so every chunk differs and their order is pinned. The item is generated, not committed.

```bash
python3 tools/tamper-evidence-verifier/verify.py vectors spec/tamper-evidence/v1
```

## Limitations

- **Evidence is relative to anchors.** A chain only proves consistency up to its last event, and the chain of anchors up to its last anchor; timestamp tokens bound when anchors existed, but anchors rebuilt with fresh tokens are revealed only by a trusted head or anchors replicated outside the database. Deleting the newest events of a stream is detected against completed anchors; deleting the newest anchors together with the streams they cover is detected only against a trusted head kept outside the database.
- **Retroactive sealing** proves "unchanged since anchor X", never "since creation".
- **`CreatedUtc` is asserted by the caller.** The trustworthy time bound is the token time of the anchor that covers an event.
- **Renaming an aggregate class** keeps chains verifiable, because the genesis value uses the stored type name, but leaves are recorded under the type name configured at anchoring, so coverage splits between the old and the new name.
- **Indirect CRL issuers** cannot be verified, so tokens whose chains depend on them stay unconfirmed.
- **Reason-limited distribution points** are sources too, but a CRL of such a point is never complete, so chains whose certificates name them stay unconfirmed.
- **CRLs issued after a certificate expired** may no longer list it, as RFC 5280 permits, so a confirming CRL issued long after the token's time can miss a revocation.
- **Revocation evidence can be deleted with its record.** Removing, substituting or partly removing the confirming CRLs of a confirmed or revoked token is detected, but removing them together with resetting the stored status to `"Unconfirmed"` is not. Replicate anchors and their evidence to WORM storage when the revocation status matters.
