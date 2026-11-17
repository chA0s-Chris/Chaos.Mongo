# Close the gaps in the tamper-evidence format v1 specification

> Issue: [#249](https://github.com/chA0s-Chris/Chaos.Mongo/issues/249)

## Rationale

Format v1 freezes at 1.0, and independent verifiers are built from `docs/tamper-evidence-specification.md` alone.
Review found the format itself sound, but four rules are missing, so two correct verifiers can disagree or a producer
can create evidence that cannot be maintained later:

1. Unknown elements are only ruled on for `_integrity`. For anchor documents, leaf documents and timestamp entries
   the library and `verify.py` both ignore them today, but the specification does not say so.
2. No algorithm or key policy exists for the certificates between the signing certificate and the root. `verify.py`
   checks every link directly, while the library leaves recorded links to `X509Chain.Build`, whose answer depends on
   the platform.
3. Retroactive sealing seals an event whose discriminator does not resolve without commitments, even when the
   aggregate has personal data marked. That value can never be erased afterwards without breaking the chain.
4. The threat table does not cover forged erasure records.

The goal is that the specification states each rule, the library and `verify.py` agree on it, and shared vectors pin
it, without changing anything format v1 already persists.

## Acceptance Criteria

- [x] `docs/tamper-evidence-specification.md` states that readers ignore unknown elements of anchor documents, of
      their `Timestamps` and `AuthorityFailures` entries, and of leaf documents, as long as the document is readable
      as a whole, and that an unknown element never changes a format v1 verification result.
- [x] The library and `verify.py` ignore an unknown element in an anchor document, a timestamp entry, an
      `AuthorityFailures` entry and a leaf document. When the unknown element cannot be read, both report an anchor
      document, or one with such an entry, as `MalformedAnchor`, and a leaf document as `AnchoredLeafInvalid` for its
      stream and `LeafSetMismatch` for its anchor. Automated tests cover each place in both tools.
- [x] The specification states the policy for chain certificates: every certificate signature on a path that Check A
      or Check B validates uses RSA with PKCS #1 v1.5 padding, RSASSA-PSS or ECDSA, each with SHA-256, SHA-384 or
      SHA-512, under an issuer key that is an RSA key of at least 2048 bits or an ECDSA key of at least 256 bits. The
      self-signature of a configured trusted root stays exempt.
- [x] The library applies that policy itself to every link of the path it validates, both when a token is received
      at anchoring and when a stored token is verified or confirmed, so which signature algorithms and key sizes are
      accepted does not depend on the platform's X.509 implementation. `verify.py` applies the same policy. Automated
      tests in both tools cover an intermediate signed with SHA-1, an intermediate issued under an undersized key,
      and a trusted root whose self-signature uses SHA-1.
- [x] The threat table in `docs/event-store.md` covers forged erasure records, and the specification's Limitations
      section says the same: what verification cannot tell apart, and what a reader has to check outside the
      database.
- [x] Retroactive sealing of an event whose discriminator is missing or does not resolve to a registered event type
      fails with a `MongoEventStoreException` when the aggregate has personal data marked, and writes no integrity
      data for that event. The sealing sweep counts the stream as failed and continues with the next stream, and an
      append to that stream fails for as long as the event is unsealed.
- [x] An aggregate without personal-data marks still seals events with an unknown discriminator as before.
- [x] Automated tests cover the sealing cases above: the sweep, the append path, and the aggregate without marks.
- [x] The specification states the producer rule for sealing with an unknown event type, and `docs/event-store.md`
      describes the failure and its remedy.
- [x] `spec/tamper-evidence/v1` holds vectors for unknown elements in an anchor document, a leaf document and a
      timestamp entry, for an intermediate signed with SHA-1, for an intermediate issued under an undersized key, and
      for a trusted root with a SHA-1 self-signature. `TamperEvidenceVectorTests` and `verify.py vectors` reproduce
      all of them. Every existing vector keeps its name and its expected outcome (status and failure); only values
      derived from regenerated keys change.
- [x] The Release build (`dotnet build -c Release Chaos.Mongo.slnx`) has no warnings, and the full test suite passes.

## Technical Details

### Unknown elements

The rule writes down existing behavior. `AnchorStore.ReadPreparedAnchor`, `ReadPreparedLeaf` and `ReadTimestamp` look
elements up by name after `StoredDocuments.Prepare` rejected repeated names, and `verify.py`'s `read_anchor`,
`read_leaf` and `read_timestamp` do the same after `is_readable`. No reader change is expected; if a test shows a
difference between the tools, the tool that rejects is the one to change.

- The rule mirrors the existing sentence for `_integrity` in the Event Documents section and belongs next to the
  anchor, leaf and timestamp entry tables.
- The specification's header says a change to anything it defines needs a new format version. State how the rule fits:
  an unknown element carries no meaning in format v1, so an element that verifiers must act on still needs a new
  format version.
- The rule that a chunk identifier holds exactly `Hash` and `Index`, in this order, stays as it is. Evidence item and
  chunk documents are out of scope.
- `ReferenceVerifierIntegrationTests.Check_UnknownElementThatCannotBeRead_MakesTheDocumentMalformedLikeTheLibrary`
  already covers an unreadable unknown element in `_integrity` and in an anchor; extend that coverage to a timestamp
  entry, an `AuthorityFailures` entry and a leaf document.

### Certificate policy for the chain

Validation policy, so the boundary is fixed before review:

| Concern | Checked by |
|---|---|
| Signature algorithm of every non-root certificate on the path, and of a recorded root that is not configured as trusted | Our code: `DirectSignatureVerification` in the library, `verify_raw_signature` in `verify.py` |
| Type and size of every issuer key on the path, the trusted root's key included | Our code, by the same check |
| Path building, validity at `genTime`, basic, name and policy constraints | `X509Chain` in the library, `openssl verify` in `verify.py`, as today |
| Self-signature of a configured trusted root | Not checked, as today (Check B accepts a configured root as configured) |

- `verify.py` already applies the policy: `whole_chain_validates` calls `issuer_signature_ok` for every link. The
  library calls `DirectSignatureVerification.IsDirectIssuer` only in the uniqueness search and for the root's
  self-signature. The library change is to run that existing check on every link of the path
  `TimestampTokenValidation.BuildChain` returns (Check A) and of the stored chain in `MatchesTrustedChain` (Check B).
  Every trust decision goes through `BuildChain`, including `Rfc3161TimestampAuthority.TimestampAsync`, which never
  calls `MatchesTrustedChain`.
- `ValidateReceived` without a trust stays a structural check: no trust decision is made there, so the policy does not
  apply.
- The key rule is the one item 6 of the Timestamp Tokens section already states for the signing key, including that a
  key with the RSASSA-PSS identifier is rejected.
- The rejection must come from our check, not from the platform. A weak intermediate is rejected by some platforms'
  chain building already, so an end-to-end rejection alone proves nothing; cover the link policy with a test that
  does not depend on what `X509Chain.Build` reports.
- A stored token whose chain has a weak intermediate becomes invalid in the library, as it already is in `verify.py`.
  Its anchor's evidence degrades to the next anchor under the existing coverage rule. Nothing is migrated.
- Accepted limit: the library checks the one path `X509Chain` builds. When that path holds a link outside the policy
  while another path with the stored prefix follows it, for example past a cross-certificate signed with SHA-1
  between two configured roots, the library rejects the token and `verify.py`, which examines the candidate paths
  itself, accepts it. The difference fails closed and is stated in the specification's Limitations. No vector pins
  it, because the library's outcome depends on the path the platform picks.
- State the policy and the boundary above in the doc comments of the types that enforce it.

Non-goals: no additional algorithms such as EdDSA, no policy for the self-signature of a configured trusted root, no
change to revocation or to the CRL signature policy, and no check of embedded certificates that are not on a
validated path.

### Sealing with an unknown event type

`EventStreamSealer.ResolvePersonalDataPaths` returns no paths when the discriminator is absent or
`EventDocumentFields<TAggregate>.ResolveEventType` returns `null`, and `SealAsync` then seals without `PersonalData`.

- The new failure applies only when `MongoEventStoreOptions<TAggregate>.PersonalDataPaths` is not empty. Without any
  mark there is nothing to commit to, so the event type does not matter.
- The exception is thrown before the chunk's bulk write, so no event of the chunk is sealed. It names the aggregate,
  the version and the stored discriminator.
- `EventStreamSealingSweep` already logs a stream that cannot be sealed, increments `StreamsFailed` and continues, so
  `IntegritySealingStatus.IsCompleteWithoutFailures` stays `false` until the type is registered again. No sweep change
  is expected.
- `SealForAppendAsync` goes through the same `SealAsync`, which is why an append to such a stream fails. This is
  intended: the alternative is sealing personal data without a commitment.
- The remedy to document is registering the event type again with its stored discriminator.
- In the specification this is a rule for producers, not for verifiers: a verifier cannot tell that a sealed event
  should have had commitments.
- No entry in `docs/migrating-from-0.x.md`: tamper evidence did not ship in v0.9.0.

### Forged erasure records

Documentation only. An erasure event involves no secret, so anyone with write access can set a recorded value to
`null`, remove its salt and append a correctly sealed `PersonalDataErased` event; verification then reports the value
as erased with a record. The documentation has to say that verification cannot distinguish this from a genuine
erasure, that the record's `Reference` must be checked against the erasure request kept outside the database, and
what anchors still bound: when the record existed, and that a record inserted below an anchored version shows in the
comparison with anchors.

### Vectors

- The explicit `TamperEvidenceVectorGenerator` regenerates the committed files once. Follow the existing convention of
  one distinctly named authority and one `trust-<scenario>.pem` per trust scenario.
- The Test vectors list in the specification names the new vectors.
- Sealing with an unknown event type and forged erasure records get no vector: neither is observable to a verifier.
