# Tamper evidence: deduplicate certificate chains and CRLs stored on anchors

> Issue: [#171](https://github.com/chA0s-Chris/Chaos.Mongo/issues/171)

## Rationale

Every timestamp entry on an anchor embeds the raw token, the authority's certificate chain and the CRLs captured at stamping time (#158). With the default 5 minute interval that evidence is copied once per authority and anchor, although chains and CRLs change only when they are renewed, and public CA CRLs can be hundreds of KB. Worse, the entries are pushed into the anchor document: evidence that together exceeds the 16 MiB document limit makes `AnchorStore.AddTimestampAsync` fail on every run, so the anchor stays `AwaitingTimestamp` and no later anchor is created.

This change moves tokens, certificates and CRLs into a content-addressed evidence store, referenced from the anchor's timestamp entries by SHA-256, so an anchor document's size no longer depends on the size of the evidence, only on the number of configured authorities, each adding a small entry of references. Anchors handed out remain self-contained. The anchor format is a draft until #160 freezes it, so this lands first.

## Acceptance Criteria

- [x] Tokens, certificates and CRLs are stored once per distinct content in a new evidence store keyed by their SHA-256. An anchor's timestamp entries hold only references to them besides the authority name and times, preserving the order and any duplicates within a chain or CRL list.
- [x] An anchor whose authorities' evidence together is well beyond 16 MiB is created and completed, and its anchor document holds no token, certificate or CRL bytes.
- [x] Evidence shared across anchors and authorities is stored once: consecutive anchors stamped with unchanged chains and CRLs add no certificate or CRL documents, and two authorities that share a certificate or CRL store it once.
- [x] An evidence item larger than the MongoDB document limit is stored and resolved byte-identical.
- [x] Storing evidence is idempotent and crash-safe: an attempt that stores evidence partially, including part of a chunked item, and fails before referencing it leaves the anchor retryable, and a later run completes it.
- [x] The completion hook's `CompletedAnchor`, and an anchor resolved through a new internal evidence resolver, are self-contained: their tokens, chains and CRLs are byte-identical to what the authority returned.
- [x] Resolving an anchor fails with a clear error when referenced evidence is missing or does not match its hash.
- [x] Existing anchoring, notification and lookup tests pass, adapted only where they read evidence bytes from `AnchorRecord`, and automated tests cover the criteria above.

## Technical Details

### Evidence store

- New collections `IntegrityAnchorEvidence` and `IntegrityAnchorEvidenceChunks`, with their names added to `IntegrityAnchoringOptions`. Both are keyed by `_id` only and need no further index.
- An evidence document is keyed by the item's SHA-256 as 32-byte binary and holds the item's length plus either the data inline or its chunk count. Items up to the chunk size, which covers practically all tokens, certificates and CRLs, are stored inline and resolved with one read; larger items are split into chunk documents keyed by `{ hash, index }`.
- The chunk size is a fixed constant of the storage format, well below the document limit. Because the hash fixes an item's bytes and the chunk size fixes every chunk, all writes are idempotent: a duplicate key on an evidence or chunk document means that document's content is already stored. A duplicate chunk must not stop the remaining chunks from being written, and the evidence document is written only after every chunk is stored; an existing evidence document is never rewritten, so an incomplete one could never be repaired. Writes go chunks first, then the evidence document, then the reference on the anchor, so an item is complete before it is visible and referenced. Own chunking is chosen over GridFS, whose files-last uploads under a content-addressed id would need recovery of orphaned chunks after a crashed upload.
- The crash-safety test leaves behind a partial set of chunks that includes the first one, and a later run completes the item.
- Resolution reads the evidence document and, for a chunked item, its chunks in order, and checks length and SHA-256 before handing the bytes out. Missing or mismatching evidence fails resolution with a clear error; in the completion notifier it is handled like a failing hook, logged and retried on the next run.
- Evidence is never deleted. Only committed anchors receive timestamps and committed anchors are never removed, so evidence becomes unreferenced only through an attempt that failed between storing and referencing it; such leftovers are small, rare and reused by the retry.
- With size no longer limited by a document, `Rfc3161TimestampAuthorityOptions.MaximumResponseSize` keeps its 16 MiB default. Storage errors still propagate out of the run as today; the deterministic oversize failure the issue describes no longer exists.

### Anchors

- A stored timestamp entry holds the authority name, the token hash, the ordered certificate and CRL hashes, the token time and the time obtained. Each entry's size depends only on its chain length and CRL count, not on the evidence sizes. RFC 4998 renewal and the confirming CRLs of #181 can add further reference lists to an entry in the same store.
- `AnchorRecord` carries the reference entries (new internal type); `AnchorTimestamp` remains the resolved, self-contained shape. The completion notifier resolves through a new internal resolver before building `CompletedAnchor`, and #160 uses the same resolver when its verification API hands out an anchor. `SealingAnchorLookup`, `SealingAnchor` and `AnchoringRunResult` keep the unresolved record, so a lookup costs no evidence reads and a lookup per event in #160's completeness check stays cheap.
- The anchor digest does not cover stored evidence, so `AnchorEncoding.FormatVersion` stays unchanged.
- No migration: the feature is internal and its format a draft, so anchor documents in the old embedded format are neither read nor converted.
