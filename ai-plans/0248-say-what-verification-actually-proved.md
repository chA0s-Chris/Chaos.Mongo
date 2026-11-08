# Make tamper-evidence verification results say what was actually proven

> Issue: [#248](https://github.com/chA0s-Chris/Chaos.Mongo/issues/248)

## Rationale

The tamper-evidence verification results and the matching `verify.py` reference verifier can report a stronger
guarantee than what was actually checked. Six such gaps are listed in the issue, each to be covered by a failing
test first since (unless noted) they were found by review and not reproduced:

1. `TimestampRevocationStatus.Confirmed` is recorded both for a chain whose non-root certificates genuinely have no
   CRL distribution points (deliberate per plan 0181) and, potentially, for a chain where only some certificates
   have sources while others are silently never checked.
2. `StreamVerificationResult.IsIntact` is `true` for a stream with zero events, while `verify.py`'s CLI already
   treats that as a problem — but only by overriding the exit code in its `main()` wrapper, not in its own
   `verify_stream()` result, so the two tools' *reported* verdicts for the same input still diverge today.
3. The anchored-leaf comparison looks up leaves by the aggregate type name configured for the *current*
   `EventStreamVerifier<TAggregate>`/`--aggregate-type`, so renaming the aggregate class (or using the wrong CLI
   value) makes the comparison silently find zero leaves and report an intact, fully-covered stream instead of
   flagging that coverage could not be established.
4. `EventAnchorVerificationResult` does not record when the anchor it reports differs from an earlier covering
   anchor whose tokens were all invalid, so a reader cannot tell that the evidence degraded to a later time; and
   `verify.py check`'s own anchor loop has no equivalent tolerance at all, so it is stricter than the spec intends.
5. Requiring the chain `StoredTimestampVerifier`/`TimestampTokenValidation` rebuilds from stored certificates to be
   byte-identical to the stored/received chain can reject a genuine token that charts a different, still-trusted
   path (for example through a cross-signed root) years later.
6. An empty `TimestampAuthorityTrust.TrustedRoots`/`Rfc3161TimestampAuthorityOptions.TrustedRoots` silently falls
   back to the OS trust store instead of being rejected, so a deployment that forgot to configure roots trusts
   whatever CA the machine happens to trust.

The goal is for `StreamVerificationResult`, `EventAnchorVerificationResult` and the revocation status to describe
exactly what was checked, for `docs/tamper-evidence-specification.md` to define every status precisely, and for the
library and `verify.py` to agree on each of these cases, without changing what format v1 already persists except
where called out below.

## Acceptance Criteria

- [x] `TimestampRevocationStatus` gains a member distinct from `Confirmed` for a chain whose non-root certificates
      contribute no CRL source at all; anchoring's `TimestampRevocationConfirmer` records it instead of `Confirmed`
      for such a chain, and `StoredTimestampVerifier` recomputes it the same way. Existing persisted
      `"Unconfirmed"`/`"Confirmed"`/`"Revoked"` values remain valid, are not migrated, and are not retroactively
      invalidated by `FindInconsistency` merely because the rebuilt chain now has zero sources.
- [x] A chain where at least one non-root certificate has a CRL source but at least one other non-root certificate
      has none is never recomputed as `Confirmed`; it stays `Unconfirmed` (the same treatment already given to a
      source whose issuer cannot be verified), both at anchoring-time confirmation and at stored-token verification
      — unless a counting CRL proves a certificate of the chain revoked, which still yields `Revoked` regardless of
      another certificate's missing coverage. `TimestampRevocationConfirmer` no longer throws for a chain with
      partial or absent source coverage, nor for one whose every certificate has a source but the returned CRLs do
      not cover all of them; it records the computed status instead, and defers every chain recorded `Unconfirmed`
      this way — whether its source coverage is structurally partial or fully source-bearing but not yet covered by
      a returned CRL — by the same retry delay a failed confirmation attempt already uses, so it stops occupying
      every run's bounded read once its status is settled while still being retried on that schedule, preserving
      today's fairness for other chains' entries exactly as the throw it replaces already did for the fully
      source-bearing case.
- [x] `TimestampVerificationResult` exposes the revocation status recomputed under today's rule
      (`RecomputedRevocationStatus`) separately from the historically `StoredRevocationStatus`, so a historical
      zero-source chain stored as `Confirmed` is reported as cryptographically valid while also being identifiable
      as `NoRevocationSource` under current rules, without the library claiming a stronger historical guarantee
      than was actually recorded.
- [x] `StreamVerificationResult` reports a stream with zero events as not intact, through a new
      `StreamVerificationFailure` member, instead of `IsIntact` being unconditionally `true`. `verify.py`'s own
      `verify_stream()` reports the same verdict for the same input, not only `main()`'s CLI wrapper.
- [x] Verifying a stream whose anchored leaves were all recorded under an aggregate type name that no longer
      matches the verifier's configured type (a renamed aggregate class, or, for `verify.py`, a mismatched
      `--aggregate-type`), and for which leaves exist under a different type for the same `AggregateId`, is
      reported as a new, distinct failure (coverage could not be established) instead of an intact, fully-covered
      stream — without widening leaf selection to match across aggregate types, and without a new
      historical-name/producer identity contract. A regression covers two different aggregate types sharing one
      `AggregateId`, including through `SealingAnchorLookup`, staying isolated from each other.
- [x] `StreamVerificationResult` exposes how many anchored leaves were actually compared (`ComparedLeafCount`),
      counted after leaves at or beyond an existing chain break are excluded, so it reports zero when a break
      precedes every anchored leaf even if some of those leaves were otherwise readable; this is distinguishable
      from a result where anchors are configured but nothing was found to compare against, and from one where
      every available leaf matched.
- [x] `EventAnchorVerificationResult` reports when the verified anchor is not the first one that covers the event
      because every token of an earlier covering anchor was invalid, so a reader can tell the evidence degraded to
      a later anchor's time. `verify.py check`'s production anchor verification (not only its vector fixtures)
      grants the same tolerance: an anchor whose only failure is invalid timestamps no longer fails the overall
      result when a later anchor of the same stream, covering at least the same versions, is itself verified, and
      that later anchor's report names the earlier one it degraded from.
- [x] Accepting a token whose rebuilt certificate chain reaches a different, independently trusted root than the
      one originally stored — including through a longer bridging path — is proven by tests, in the library and
      `verify.py`, covering: a genuine longer/cross-signed path is accepted while the stored chain's own root is
      independently validated to truly terminate it; a substituted root (including a same-subject/different-key
      decoy) is rejected; an unrelated certificate inserted between the signer and the certificate that actually
      issued it directly is rejected; a chain valid only at the token's own time is accepted and evaluated at that
      time, while one already expired at that time is rejected; each of a path-length, a name, and an
      extended-key-usage constraint inherited along the complete recorded path rejects a token whose individual
      recorded links are each genuine in isolation, in both tools; a chain whose certificates are the same
      multiset as a genuine path but in a different order (possible when two recorded certificates genuinely
      share a subject name) is rejected by per-link adjacency checks alone, even though a check that only counts
      path depth would accept it; a recorded position with a genuine, shared-key sibling reachable through a
      different, independently trusted root is rejected as non-unique — including when a second, unaffected
      position must stay silent — while a position whose only remaining alternate is itself constraint-ineligible
      is accepted, proving the uniqueness search neither under- nor over-rejects, generalized to three siblings
      with mixed eligibility; the isolated single-candidate chain-building primitive the uniqueness search and
      Check B both use does not leak an unrelated, still-embedded certificate into a candidate it was not given;
      and a genuine, non-final self-issued intermediate — whose complete path is valid, including under a root's
      name constraint that exempts it specifically because it is not the path's own final certificate — is
      accepted in both tools, proving neither tool's adjacency or prefix-extension primitive revalidates it as if
      it were a path's own final target. All cases are mirrored as committed `spec/tamper-evidence/v1` vectors
      replayed by `TamperEvidenceVectorTests` and `verify.py vectors`, and exercised through the production
      `verify_timestamp`/`verify.py check` paths, not only fixture replay.
- [x] Configuring a timestamp authority's trust (`IntegrityVerificationBuilder.WithTimestampAuthorityTrust`,
      `IntegrityAnchoringBuilder.WithRfc3161TimestampAuthority`) with an empty `TrustedRoots` throws instead of
      silently falling back to the OS trust store; `verify.py` already requires at least one `--trusted-root` for
      `check` whenever `--anchors` is given, and keeps that requirement with added regression coverage.
- [x] `docs/tamper-evidence-specification.md` defines the new revocation status (and its historical-compatibility
      treatment), the empty-stream rule, the rename coverage-failure report, the degraded-evidence and
      compared-leaf-count reporting (including `verify.py check`'s production degradation tolerance), the
      possibly length-changing trusted-root relaxation together with the independent validation of the stored
      chain's own recorded root, and the non-empty-`TrustedRoots` requirement.
- [x] `spec/tamper-evidence/v1`'s shared test vectors cover: a chain with no revocation sources, a chain with
      partial source coverage, a chain with demonstrated revocation alongside another certificate's missing
      coverage, an empty stream, a stream anchored under one aggregate type name and verified under another with
      no leaves found under the new name, two different aggregate types sharing one `AggregateId`, and anchor-token
      evidence that degrades to a later anchor (including in `verify.py check`'s production path, not only its
      fixture runner). `TamperEvidenceVectorTests` and `verify.py vectors` reproduce all of them identically.
- [x] The Release build (`dotnet build -c Release`) has no warnings, and the full test suite passes.

## Technical Details

### 1. Revocation status for chains with no (or partial) CRL sources, without invalidating historical evidence

`StoredTimestampVerifier.RecomputeRevocationStatus` (`src/Chaos.Mongo.EventStore/Integrity/StoredTimestampVerifier.cs:158-182`)
and `TimestampRevocationConfirmer.RecordStatusesAsync` (`TimestampRevocationConfirmer.cs:205-296`) both treat
`sources.Count == 0` as "nothing to check, so confirmed" (spec `docs/tamper-evidence-specification.md:256`). That
collapses two different situations: a chain whose non-root certificates genuinely name no CRL distribution point at
all (today's deliberate case, kept per plan 0181), and a chain where some certificates have sources and others —
silently — do not, because `TimestampTokenValidation.GetCrlSources(X509Certificate2)` (`TimestampTokenValidation.cs:561-577`)
returns an empty list for a certificate without a distribution point extension, and the per-certificate information
is lost once sources from every certificate are flattened and deduplicated by `GetCrlSources(IEnumerable<X509Certificate2>)`
(`TimestampTokenValidation.cs:142-151`).

Add `TimestampRevocationStatus.NoRevocationSource` ("no non-root certificate of the chain names a CRL distribution
point, so there is nothing to confirm"). Thread through, beside the existing deduplicated `CrlSource` list, whether
every non-root certificate of the chain contributed at least one source (illustrative: a `GetCrlSources` overload or
sibling that also returns this per-chain fact, computed from the same per-certificate calls `GetCrlSources` already
makes before deduplicating).

Both `RecomputeRevocationStatus` and `RecordStatusesAsync` already evaluate demonstrated revocation
(`RevocationEvidence.FindRevoked`) **before** deciding between `Confirmed`/`Unconfirmed`/`NoRevocationSource`
(`StoredTimestampVerifier.cs:174-181`; `TimestampRevocationConfirmer.cs:253-262`): a certificate proven revoked by a
counting CRL always yields `Revoked`, independent of whether some other certificate of the same chain has no source
at all. This plan keeps that check order; the new no-source/partial-source rules below only apply once no
certificate is found revoked:

- Every non-root certificate has zero sources → `NoRevocationSource`.
- At least one certificate has sources and at least one other does not → `Unconfirmed` unconditionally (it can
  never become `Confirmed`; this matches the existing treatment of a source named by an indirect CRL issuer, which
  `RevocationEvidence.CountsFor` never marks as covered).
- Every certificate has at least one source → today's rule: `Confirmed` only when `RevocationEvidence.Evaluate`'s
  `Covered` is `true` for every source, else `Unconfirmed`.

`TimestampRevocationConfirmer.RecordStatusesAsync` (`TimestampRevocationConfirmer.cs:260-265`) currently *throws*
`InvalidDataException` for a chain's whole due batch when any source is uncovered and any entry in the batch is not
revoked, which leaves such entries perpetually retried (deferred by `MaximumRetryDelay`, `TimestampRevocationConfirmer.cs:45`)
rather than ever settling — an authority whose CRL never covers one distribution point would retry forever. Replace
that throw with the same three-way decision above per entry (demonstrated revocation first, then
`NoRevocationSource`/partial-`Unconfirmed`/full-coverage), storing the result instead of throwing; this is needed for
the confirmer to reach the same per-chain status as `StoredTimestampVerifier.RecomputeRevocationStatus` for the new
cases, not only for `Confirmed`/`Revoked`.

A chain recorded as `NoRevocationSource`, `Confirmed` or `Revoked` naturally stops being selected by
`AnchorStore.FindUnconfirmedTimestampsAsync` (`AnchorStore.cs:506-529`) and `ReadPreparedUnconfirmedTimestamps`
(`AnchorStore.cs:1000-1010`) on the next run, since both filter by the stored status being `Unconfirmed`
(`HasUnconfirmedTimestamp()`) — the existing mechanism already settles these the same way it settles `Confirmed`
today. Every chain recorded `Unconfirmed`, however, stays `Unconfirmed` by design (the acceptance criterion above)
and would otherwise remain due and re-selected on every subsequent run, exactly as if it had failed, because
calling `ConfirmTimestampsAsync` (`AnchorStore.cs:188-205`) does not change `ConfirmationNotBeforeUtc`. This covers
two distinct cases that both reach `Unconfirmed` once the throw below is removed: a chain whose per-certificate
source coverage is structurally partial (bullet above), and a chain whose every certificate has a source but the
returned CRLs do not cover one or more of them — today's existing uncovered-source check
(`TimestampRevocationConfirmer.cs:260-265`) that the throw this plan removes currently also protects. Left
unhandled for either case, this reintroduces the same starvation `MaximumRetryDelay`'s deferral already guards
against — a chain that cannot yet reach full coverage would occupy every run's bounded read
(`MaximumAnchorsPerRun`) forever, exactly like a chain that keeps failing, undoing the fairness
`TimestampRevocationConfirmationTests.RunAsync_ChainKeepsFailing_DoesNotKeepOtherChainsFromBeingConfirmed` already
covers for the throwing case it replaces — including for the fully source-bearing, CRL-not-yet-covering case, which
today reaches that same fairness guarantee only because the throw is caught by `ConfirmChainAsync`'s existing
exception handler (`TimestampRevocationConfirmer.cs:163-173`) and deferred there; replacing the throw with a stored
status must not silently drop that existing guarantee for this case. After recording an entry's status, whenever
that status is `Unconfirmed` for either reason, call the existing `AnchorStore.DeferTimestampConfirmationAsync`
(`AnchorStore.cs:211-214`) with `now + MaximumRetryDelay`, the same delay a failed or too-early attempt already
uses, so it is retried on the same schedule as a failing chain rather than being read on every run — retried, not
abandoned, since nothing in the stored data distinguishes "never will be covered" from "not covered by the CRLs
read so far." This also changes `RunAsync_CustomAuthorityReturnsUnusableCrl_CountsTheFailure`'s "broken signature"
and "delta CRL" cases (`TimestampRevocationConfirmationTests.cs:320-348`): they currently reach this condition
through the thrown exception and assert a recorded `ConfirmationFailure` with the exception's message; once the
throw is replaced by a stored status, the same input instead reaches `ConfirmTimestampsAsync` without ever calling
`RecordTimestampConfirmationFailureAsync`, so these two cases' assertions must change to expect `Unconfirmed` with
no `ConfirmationFailure` recorded and a deferred `ConfirmationNotBeforeUtc`, not a failure message. Add an
integration regression with an old partial-coverage chain, a later fully-covered chain and
`MaximumAnchorsPerRun = 1`, running the confirmer across several ticks and asserting the authority's request count
and the healthy chain's eventual `Confirmed` status, matching the existing failing-chain fairness test's shape; add
a second regression of the same shape for an old chain whose every certificate has a source but whose returned CRL
does not cover one of them (the same "broken signature"/"delta CRL" shape above), asserting the same bounded, fair
retry behavior now that it is reached by a stored status rather than a thrown exception.

`StoredTimestampVerifier.FindInconsistency` (`StoredTimestampVerifier.cs:103-139`) gains a matching branch for a
stored `NoRevocationSource`: consistent only when every non-root certificate of the rebuilt chain indeed has zero
sources, inconsistent otherwise. **A stored `Confirmed` is not flagged inconsistent merely because the rebuilt chain
now has zero sources** — that was a legitimately produced status under the pre-existing rule (spec line 256, plan
0181), and the stored string alone cannot distinguish a historical honest zero-source confirmation from a status
this plan computes differently today; invalidating it would be a verification-compatibility break the issue does not
ask for. `FindInconsistency`'s existing check (a stored `Confirmed` where some source is not `Covered`) is unchanged
and still catches a genuinely falsified `Confirmed`.

Because historical no-source `Confirmed` entries stay cryptographically valid but no longer match what today's rule
would compute, add `TimestampVerificationResult.RecomputedRevocationStatus` (`TimestampRevocationStatus`, alongside
the existing `RevocationStatus` and `StoredRevocationStatus`) that always reports the status `RecomputeRevocationStatus`
computes under the current rule — including `NoRevocationSource` for a historical zero-source chain whose
`StoredRevocationStatus` is `Confirmed`. `RevocationStatus` (used for the revoked/valid decision) is unaffected,
since the only new value it can return besides `Confirmed`/`Unconfirmed`/`Revoked` is `NoRevocationSource`, which is
not `Revoked` and so does not invalidate the token; `RecomputedRevocationStatus` exists so a caller can tell "this
token's revocation evidence, recomputed under current rules, only shows there was nothing to check" apart from "it
was genuinely confirmed," without the library claiming a stronger historical guarantee than was actually recorded.

**Format v1 impact.** The `RevocationStatus` string element (`docs/tamper-evidence-specification.md:211`) gains a
fourth possible value. Existing stored `"Unconfirmed"`/`"Confirmed"`/`"Revoked"` anchors are unaffected, are not
migrated, and remain cryptographically valid evidence; only future anchoring runs can write the new value for
newly-confirmed entries. `TimestampVerificationResult.RecomputedRevocationStatus` is a new runtime-only result field,
not a stored one.

### 2. Empty streams

`EventStreamVerifier<TAggregate>.VerifyStreamAsync` (`EventStreamVerifier.cs:130-152`) falls into the
`StreamVerificationResult.IntactWith(unrecorded)` branch whenever `scan.Events.Count == 0` and
`CompareWithLeaves` finds nothing, because `ChainScan`'s loop never runs and never breaks. Decision: an empty
stream is always reported as not intact — the library cannot tell "this aggregate never existed" apart from "every
event of this stream, and its anchored leaves, were deleted", and `verify.py`'s existing CLI rationale ("a wrong
identifier or element name would otherwise look like an intact empty stream",
`docs/tamper-evidence-specification.md` "Verifying Independently" section) applies to the library's callers too, not
only to CLI operators.

Add `StreamVerificationFailure.EmptyStream`. In `VerifyStreamAsync`, when neither the chain scan nor
`CompareWithLeaves` found a break and `scan.Events.Count == 0`, return `StreamVerificationResult.Broken(1,
StreamVerificationFailure.EmptyStream)` instead of `IntactWith(...)`. This only changes the truly-empty case: a
leaf showing a higher anchored version already reports `AnchoredVersionMissing` first, which is more specific and
unchanged.

Mirror this in `verify.py`'s `verify_stream()` (not only `main()`'s `eventCount == 0` override,
`tools/tamper-evidence-verifier/verify.py:2255-2259`): when it finishes with zero documents and no other break, set
its own `report["intact"] = False` with a matching `"failure"` value, so `verify_stream()` itself — and anyone
calling it other than through `main()` — gives the same verdict as the library for the same input. Simplify
`main()`'s override accordingly.

### 3. Rename-independent coverage reporting without crossing stream identity

`AnchorStore.StreamLeavesFilter` (`AnchorStore.cs:1035-1041`) matches leaves by `AggregateType`, `AggregateId` and
`Version`, and backs `FindCompletedStreamLeavesInOrderAsync`, `ReadStreamLeavesAsync` and (through
`FindStreamLeavesAsync`) `SealingAnchorLookup.FindAsync` (`SealingAnchorLookup.cs:36`). The module's aggregate stores
keep separate event collections per `TAggregate` (`MongoEventStoreOptions<TAggregate>`), but anchors and leaves are
shared module-wide collections keyed only by `AggregateId` within the leaf document; nothing in format v1 stops two
different aggregate types from using the same `AggregateId`. Dropping the `AggregateType` match from this filter —
matching leaves by `AggregateId` alone — would let such same-`AggregateId`, different-type streams compare against
each other's independently valid leaves, causing false hash/truncation failures and letting `SealingAnchorLookup`
assign a stream's events to the wrong type's anchor. This plan keeps `StreamLeavesFilter` and `StreamLeavesSort`
(`AnchorStore.cs:1043-1048`) unchanged, does not widen them to match by `AggregateId` alone, and does not introduce a
historical-name/stable-identity contract for producers.

Instead, when a stream is verified under an aggregate type name that no longer matches the name its leaves were
recorded under (a renamed aggregate class, or, for `verify.py`, a mismatched `--aggregate-type`),
`StreamLeavesFilter`'s exact-type match finds zero leaves for the *current* name — indistinguishable in format v1
from "this stream has never been anchored," which this plan must not let through as a silently intact, fully
covered stream, per the issue's explicitly permitted alternative: "fail when no covering anchor could be compared
but anchors exist."

Add a presence-only check, used solely to decide whether to report this ambiguity, never to compare hashes or
accept inclusion across types: whether a leaf exists for the same `AggregateId` under some *other* `AggregateType`.
Add `AnchorStore.HasLeavesForOtherAggregateTypeAsync(String aggregateType, Guid aggregateId, CancellationToken)`,
querying the leaves collection by `AggregateId` only, projecting `AggregateType`, and returning whether any returned
document's `AggregateType` differs from the given one (a single-document `Limit` is not safe here, since the first
matching document by insertion order could be the same type). Mirror it in `verify.py`'s `check()` as the same kind
of query over `leaf_documents` for the same `AggregateId` with any `AggregateType` other than
`arguments.aggregate_type` — `verify.py`'s `verify_stream()`, like the library's `VerifyStreamAsync`, only receives
the leaves `check()` already selected, so it cannot run this presence query itself.

`AnchorStore`'s only indexes on the `Leaves` collection today are `LeavesByStreamIndexName`
(`AggregateType`, `AggregateId`, `AnchorSequence`, `Version`) and `LeavesByAnchorIndexName` (`AnchorId`, `Index`)
(`AnchorStore.cs:276-300`); neither leads with `AggregateId` alone, so `HasLeavesForOtherAggregateTypeAsync`'s filter
would otherwise need a full scan of the shared `Leaves` collection — which holds leaves of every aggregate type in
the module — on every call, and this call runs whenever a stream has events but `ComparedLeafCount` is zero,
including the ordinary case of a stream that simply has not been anchored yet, not only the rename/error case. Add
a supporting index, `{AggregateId:1, AggregateType:1}`, alongside the two existing ones in
`AnchorStore.EnsureIndexesAsync`; the query only needs `AggregateId` to filter and `AggregateType` to project, so it
is fully covered by this index and never scans the collection.

`EventStreamVerifier<TAggregate>.VerifyStreamAsync` calls this check only when the stream has at least one event
(the zero-event case is §2's `EmptyStream`) and `CompareWithLeaves` found zero completed leaves to compare
(`ComparedLeafCount == 0`, §4) with no earlier break. When it finds a leaf under another type for the same
`AggregateId`, return `StreamVerificationResult.Broken(1, StreamVerificationFailure.AggregateTypeMismatch)` instead
of `IntactWith(...)` — explicitly reporting that coverage could not be established, rather than an intact, fully
covered stream. When no such leaf exists under any other type, the stream is reported as today: never anchored, so
intact, with `AnchorCoverageChecked` true and `ComparedLeafCount` zero (distinguishable from a stream whose anchors
were all found and matched, since that already reports a positive `ComparedLeafCount`). Mirror the same
`AggregateTypeMismatch`-equivalent `"failure"` value: `verify_stream()` gains an `other_type_leaf_exists` parameter
alongside `leaves`/`first_unreadable`, and `check()` passes the result of its own presence query into it, so both
tools report the identical failure for the identical input.

This does not detect a truncation that happens strictly after the rename and is covered only by leaves recorded
under the *new* name — those are found normally, by the unchanged filter, and compared as today. It detects and
reports the previously-silent case where the verifier's configured name finds no leaf for the stream at all, while
the stream was in fact anchored under a different name, so that case is never reported as "intact, fully covered"
rather than "coverage could not be established." `--aggregate-type` keeps gating `verify.py`'s leaf selection
exactly as the library's `_aggregateTypeName` does; it is not required to equal the historically anchored name for
genesis computation, which already reads the aggregate type from the stored version-1 document
(`EventStreamVerifier.cs:523-531`, `verify.py:678-685`), unaffected by this change.

Update the "Renaming an aggregate class" limitation in `docs/tamper-evidence-specification.md` (Limitations
section) to describe the fail-closed report instead of a silent coverage split.

### 4. Explicit "nothing compared" and degraded-evidence reporting

Add `StreamVerificationResult.ComparedLeafCount` (`Int32`): the number of completed leaves `CompareWithLeaves`
(`EventStreamVerifier.cs:260-283`) actually evaluates, i.e. the count of `leaves.Completed` **after** the existing
`Where(completed => !(completed.Leaf.Version >= brokenVersion))` filter that excludes leaves at or beyond an
already-broken prefix (`EventStreamVerifier.cs:267`) — not the unfiltered `leaves.Completed.Count`, which would
overstate how many leaves were actually examined whenever a break precedes some anchored leaves, recreating the
exact reporting gap the issue addresses. `CompareWithLeaves` returns this count alongside its existing finding so
`VerifyStreamAsync` can carry it into the result without recomputing the filter. Together with the existing
`AnchorCoverageChecked`, this lets a caller tell "anchors are configured, and every leaf available to compare
matched" apart from "anchors are configured, but there was nothing yet to compare" (a brand-new stream not yet
anchored, or — per §3 — one whose anchors could not be found under the current name).

Add `EventAnchorVerificationResult.DegradedFromAnchor` (nullable `AnchorReference`). In
`VerifyEventAnchorAsync` (`EventStreamVerifier.cs:46-88`), `firstWithoutValidToken` already holds the result of the
first covering anchor whose tokens were all invalid (`EventAnchorFailure.NoValidTimestamp`) when the walk continues
to a later anchor. When a later anchor's check ends the loop (verified or failed for a different reason), attach
`firstWithoutValidToken.Anchor` as the returned result's `DegradedFromAnchor` (`null` when no earlier anchor was
skipped). Document in the spec's "Coverage" paragraph that verification results now name the anchor evidence
degraded from, not only the one it degraded to.

**`verify.py check`'s production anchor verification has no equivalent tolerance today and must gain one, not only
its vector fixtures.** `check()` (`verify.py:2074-2127`) reads every completed leaf/anchor pair the stream's leaves
select across the *stream's entire anchor history* (every anchor that ever recorded a leaf for it, in sequence
order, `verify.py:2116`) and ANDs every one of their `anchor_report["verified"]` into the overall `ok`
(`verify.py:2125`) — so a single historical anchor whose only token was later revoked fails `ok` even when a later
anchor fully re-covers the same versions with a valid token, which is the per-event degradation the C#
`VerifyEventAnchorAsync` already tolerates; this is the concrete instance of "`verify.py` is stricter than the spec
here" the issue names. Change `check()`'s loop: for a leaf/anchor pair whose `anchor_report["verified"]` is `False`
only because every one of its timestamps is invalid (never for an inclusion-proof or digest mismatch, which must
still fail `ok`), that pair no longer fails `ok` when a *later* leaf of the same stream (higher `Sequence`, per the
existing `leaves.sort` at `verify.py:2116`) with a version at least as high is itself verified; record
`anchor_report["degradedFrom"]` (the earlier anchor's identifier) on that later, accepted report. A pair with no
such later, covering, verified leaf still fails `ok` exactly as today. Mirror `EventAnchorVerificationResult.DegradedFromAnchor`
as this `"degradedFrom"` field in both the vector fixtures and `check`'s per-anchor report, so both tools name the
same degradation in the same place.

### 5. Accepting a genuine token whose rebuilt chain takes another trusted path

`TimestampTokenValidation.ValidateReceived` (`TimestampTokenValidation.cs:403-481`, comparison at 461-466),
`StoredTimestampVerifier.VerifyToken` (`StoredTimestampVerifier.cs:188-227`, comparison at 212-220) and
`TimestampRevocationConfirmer.RequireTrustedChainAsync` (`TimestampRevocationConfirmer.cs:309-350`, comparison at
330-337) each call `TimestampTokenValidation.BuildChain` and reject the token unless the rebuilt chain is
byte-for-byte identical, element by element, to the stored/received chain. `TimestampAuthorityTrust`'s existing
remark names the resulting false positive directly: the chain built years later may take another path, for
example through a cross-signed root, while the signer and every intermediate stay the same. Replace the single
full-chain-equality comparison with two independent checks at all three sites.

**Check A — the token remains genuinely trusted today.** Call `BuildChain` exactly as today (its `ExtraStore`
already includes the token's own embedded certificates, `TimestampTokenValidation.cs:95-96` — this is where a
TSA-supplied bridging/cross-signing certificate travels with the stored token bytes). Require the result to begin,
in order, with exactly the stored/received chain's signer and every intermediate —
`built.Certificates.Take(storedOrReceived.Count - 1)` sequence-equals `storedOrReceived.Take(storedOrReceived.Count - 1)`
— but drop today's requirement that the two chains have equal length: the engine may terminate at that same length
(today's common, unaffected case) or continue through further certificates to its own self-issued,
`trust.TrustedRoots`-member final element. This keeps the signer and every intermediate — and the CRL distribution
points `GetCrlSources` derives from them — pinned exactly as today; only what the chain reaches *after* that
prefix is now free to vary, including in length.

A passing prefix comparison alone does not prove the recorded prefix was the *only* candidate `BuildChain` could
have selected at each position: when two certificates genuinely share both subject name and key pair, a signature
that validates against one validates identically against the other, so `BuildChain` can incidentally select the
recorded certificate while a genuine sibling, drawn from the token's own embedded certificates, could equally have
reached a *different*, still-trusted root from the same position. Nor is restricting `ChainSettings` to one
candidate path and re-calling `BuildChain` sufficient: `BuildChain` always adds every one of the token's own
embedded certificates to `ExtraStore` regardless of which candidate is under test, so the engine can still resolve
through an unrelated, still-embedded certificate and pass or fail for the wrong candidate. Add
`BuildIsolatedChain(X509Certificate2 signer, DateTimeOffset verificationTime, IReadOnlyList<X509Certificate2>
candidates, X509Certificate2 trustedRoot, X509RevocationMode revocationMode, TimeSpan revocationCheckTimeout)`
beside `BuildChain`, sharing their `ChainPolicy` setup (application policy, revocation mode/timeout,
`verificationTime`) through a common private helper so the two cannot drift apart, but setting
`ChainPolicy.ExtraStore` to *exactly* `candidates` and `ChainPolicy.CustomTrustStore` to exactly `[trustedRoot]`
(`TrustMode = X509ChainTrustMode.CustomRootTrust`) — it takes no `Rfc3161TimestampToken` parameter, so there is
nothing for it to implicitly add.

For each recorded intermediate position `i` in `1 .. storedOrReceived.Count - 2` (an empty range for a
directly-rooted signer, so this adds nothing there), prove uniqueness by search rather than by trusting one
`BuildChain` call's outcome: build a directed graph over the finite candidate pool — the union of `ExtraStore` and
`ExtraCertificates`, excluding every certificate byte-identical to `storedOrReceived[i]`, plus every member of
`trust.TrustedRoots` — with an edge from `c` to `r` exactly when `r`'s subject matches `c`'s issuer and `c`'s
signature validates under `r`'s public key (direct cryptographic issuer adjacency only, not a chain-validation
call — isolated as its own helper so it cannot be defeated by a constraint that only manifests across a complete
path). Enumerate every simple path from the signer to a self-issued `TrustedRoots` member, and validate each one
as a complete path by calling `BuildIsolatedChain` with that path's own certificates (excluding its final root) as
`candidates` and its final certificate as `trustedRoot`, requiring success and an exact, ordered, byte-for-byte
reproduction of the path. Position `i` is unique only when *every* enumerated path fails this whole-path
validation, including when none exist; any single success proves the excluded position was not the only way to
reach trust, and the token is rejected even though the original `BuildChain` call succeeded. Factor the graph
search and the per-candidate `BuildIsolatedChain` call as shared private helpers in `TimestampTokenValidation`,
called from all three comparison sites after their prefix comparison passes.

**Check B — the stored/received chain's own recorded root is independently genuine, not merely configured.**
Check A alone would accept a corrupted or substituted stored root (if two roots are configured and the signer is
genuinely issued by one, but the stored chain records the other as its final certificate, Check A's path search
simply finds the real path through the first and its prefix comparison still passes). Independently of Check A,
call `BuildIsolatedChain` — not `BuildChain`, whose unconditional embedded-certificate addition would let the
engine substitute one of them for a recorded certificate — with `candidates` set to the stored/received chain's
own certificates excluding its final element and `trustedRoot` set to its final certificate; no other certificate
is offered. Require success and `built.Certificates.SequenceEqual(storedOrReceived)` (ordered and exact, not a
count match), so a built path of the same length through a different order is also rejected. Because no other
certificate is available to substitute, success is only possible when the stored chain's own final certificate is
genuinely self-issued and the genuine cryptographic issuer of the certificate before it, recursively to the
signer — independent of whether that root is still configured as trusted today.

A token is accepted only when Check A's prefix comparison, Check A's per-position uniqueness search, and Check B
all succeed. The common case — the stored chain's own root is still directly reachable and configured, with no
shared-key sibling in the candidate pool — satisfies all three without any behavior change, since `BuildChain`
already resolves the shortest valid path, that path already equals the stored chain exactly, and the uniqueness
search's range is empty or finds no alternate.

**`verify.py` needs the equivalent relaxation, using a direct cryptographic adjacency primitive instead of path
validation for every per-link/per-edge test.** `chain_failure` (`verify.py:1796-1811`) rejects a stored chain
whose final certificate is not currently among the configured `--trusted-root`s before `verify_timestamp` ever
reaches `openssl ts -verify` — exactly the restriction Check A relaxes. Drop that disjunct, keeping only the
self-issued-name requirement already checked on `chain_fields[-1]`.

A single `openssl ts -verify` call only reports that *some* path exists, never *which* certificates it used, so it
cannot stand in for Check A's explicit prefix comparison or for Check B's recorded-root validation; both need
their own primitive, as in the library. Every adjacency test below — Check A's prefix pin, Check A's per-position
search edges, and Check B's per-link calls — must test direct cryptographic issuer adjacency, never
`openssl verify -partial_chain -trusted <parent> <child>`. That call does not merely test whether `parent` signed
`child`: it performs complete path validation with `child` as the call's own final target, and RFC 5280's
processing of several constraints — the self-issued-certificate exemption from name-constraint checking chief
among them — depends on whether a certificate is a path's *final* certificate or an intermediate one. A genuine,
non-final self-issued intermediate can therefore fail this call (for example on a `permitted subtree violation`
inherited from its own issuer) even though the complete, real path through it validates and is exempt from
exactly that check, because the exemption no longer applies once the call makes that intermediate the final
target. This was confirmed with a decoded, signature-verified token and a genuine self-issued (but not
self-signed) intermediate signed by a name-constrained root: the complete path validates and `openssl ts -verify`
accepts the real token, but the pairwise call from the root to that intermediate alone fails.

Add `certificate_parts(certificate)` beside `crl_parts`, returning a certificate's TBSCertificate bytes, outer
signature algorithm and signature value, and `issuer_signature_ok(child_fields, child_parts, parent_fields)`:
verifies `child_parts`'s signature validates under `parent_fields`'s public key (dispatching RSA PKCS #1 v1.5,
RSASSA-PSS or ECDSA by the signature algorithm, the same dispatch `crl_signature_accepted` already performs for
CRL signatures) and that `child_fields["issuer"]` matches `parent_fields["subject"]` under `name_key` — a direct
signature-and-name check with no path-building and no RFC 5280 constraint propagation, so it carries none of
`openssl verify`'s final-target-dependent semantics. Use `issuer_signature_ok` as the only adjacency primitive
throughout this section, replacing every use of `openssl verify -partial_chain` for that purpose:

- **Check A's prefix pin.** For every `i` in `0 .. n-3` (every recorded link except the one into the stored
  chain's own last certificate), require `issuer_signature_ok(cert[i], cert[i+1])`. Then, instead of a second call
  that makes the recorded chain's own last intermediate the final target of its own validation — which
  reintroduces the same final-target artifact `issuer_signature_ok` exists to avoid — validate one complete path
  from the *signer* to a trusted root using the whole-chain mechanism Check B defines below, requiring its first
  `n-1` depths (the signer through the recorded last intermediate) to equal the recorded prefix in order; depths
  beyond that are unconstrained, other than terminating at a self-issued, currently trusted root. This proves the
  pinned prefix extends to trust today while keeping the signer, not an intermediate, as the path's genuine
  terminus throughout.
- **Check A's per-position search.** For each recorded intermediate position `i` in `1 .. n-2`, build the same
  kind of directed graph as the library — the token's own embedded certificates and every recorded certificate
  except `cert[i]`, plus every configured `--trusted-root`, with `issuer_signature_ok` as the edge test — and
  enumerate every simple path from the signer to a self-issued trusted root. Validate each candidate path with the
  whole-chain, exact-depth-count mechanism Check B uses below (`-show_chain`, requiring exactly as many `depth=`
  lines as the path's own certificate count, plus the per-link `issuer_signature_ok` calls and
  `application_policy_failure` applied to that path's own certificates). Position `i` is unique only when every
  enumerated path fails this whole-path validation, including when none exist.

**Check B.** A single `openssl verify -trusted <root> -untrusted <rest>` call resolves OpenSSL's own shortest path
and can silently skip a recorded certificate that was never actually needed; per-link adjacency alone cannot catch
a propagated constraint (path length, name constraints, and others) that only manifests across the *complete*
path. Use a single whole-chain `openssl verify -show_chain -attime <token_time> -trusted <cert[n-1]> -untrusted
<cert[1]>,...,<cert[n-2]> <cert[0]>` call, requiring success and exactly `n` `depth=<i>:` lines in its output — a
call succeeding with fewer depths has found a *shorter* path through the same fixed candidates rather than
validated the one recorded, and this count rules that out. This alone does not establish that the recorded
*order* is the genuine one: `-untrusted` is an unordered pool, so when it contains two certificates that can
occupy the same structural position (for example two genuinely distinct certificates sharing a subject name), a
call can still succeed with exactly `n` depths via a genuine path using the same multiset of certificates in a
different order than recorded. Add `n - 1` independent adjacency calls, one per recorded pair:
`issuer_signature_ok(cert[i], cert[i+1])` for every `i` in `0 .. n-2`, each proving the *specific* recorded link,
not merely that some permutation of the pool reaches trust. (The C# side does not share this gap: Check B's
`BuildIsolatedChain` result is compared to the stored chain as an ordered sequence already.)

**Application policy.** `BuildChain` adds `id-kp-timeStamping` to `ChainPolicy.ApplicationPolicy` on every call
(`TimestampTokenValidation.cs:93`), so the C# side already requires every certificate of a built path to carry no
extended key usage extension or one that includes `id-kp-timeStamping`/`anyExtendedKeyUsage`. Neither a bare
`openssl verify -show_chain` call nor one with `-purpose timestampsign` reproduces this restriction on the
project's OpenSSL version. Add `OID_ANY_EXTENDED_KEY_USAGE = "2.5.29.37.0"` alongside the existing
`OID_TIME_STAMPING` constant and `application_policy_failure(chain_fields)`: for every certificate of the recorded
chain, when its extended key usage extension (if present) excludes both OIDs, return a failure naming that
certificate's position. Call it on Check B's own `chain_fields` and, inside the per-position search above, on
each candidate path's own certificates (so an EKU-ineligible candidate is rejected the same way as any other
propagated constraint, without a separate pre-filter).

A token is accepted only when `chain_failure`'s structural checks, the relaxed current-trust validation (prefix
pin, prefix-preserving extension and per-position search, all via `issuer_signature_ok`), Check B's per-link
`issuer_signature_ok` calls, the whole-chain exact-depth-count call, and `application_policy_failure`, all
succeed.

Update `TimestampAuthorityTrust`'s remark and the spec's relevant passage to describe both checks instead of only
recommending operational mitigation, and note that `verify.py` applies the equivalent checks rather than
delegating the whole case to a single `openssl ts -verify` call gated by current-trust membership.

**Tests** (library and matching `verify.py` regressions for each; mirrored as committed `spec/tamper-evidence/v1`
vectors replayed by `TamperEvidenceVectorTests`/`verify.py vectors` and exercised through the production
`verify_timestamp`/`verify.py check` paths):

1. A genuine path through a bridging certificate carried in the token's own embedded set is *longer* than the
   originally stored chain — accepted (Check A's relaxed length; Check B independently validates the shorter
   stored chain terminates genuinely).
2. The stored chain's final certificate is substituted for a different, independently trusted root that never
   issued the stored intermediate — including a decoy sharing the genuine root's subject name but a different key
   — rejected (Check B).
3. An unrelated certificate, sharing a genuine certificate's subject name but actually issued by it and naming an
   unrelated CRL distribution point, is recorded between the signer and the certificate that in fact issued the
   signer directly — rejected (the exact-depth-count call and the library's prefix check catch the certificate
   that was never actually used, which an unconstrained shortest-path search would otherwise skip).
4. A chain genuinely valid only at the token's own time, since expired, is accepted and evaluated at that time
   (`-attime`/`VerificationTime`, not current time); a chain already expired at the token's own time is rejected.
5. Each of a path-length constraint, a name constraint and an extended-key-usage restriction, inherited from the
   recorded root (or, for name constraints, propagated to the signer) and violated only when the complete recorded
   path is evaluated as a whole, rejects a token whose individual recorded links are each genuine in isolation, in
   both tools — including a cross-certificate variant (a certificate sharing a trusted root's own subject and key,
   inserted before a path-length-restricted intermediate) for the path-length case, and the application-policy
   mirror specifically, since neither a bare nor a `-purpose`-qualified `openssl verify` call reproduces .NET's
   enforcement.
6. A chain listing the same multiset of certificates as a genuine path but in an order inconsistent with their
   actual issuer relationships — possible when two recorded certificates genuinely share a subject name — is
   rejected by the per-link `issuer_signature_ok`/`SequenceEqual` checks in both tools, even though a call that
   only counts path depth would accept it.
7. Two certificates genuinely share both subject name and key pair but are issued by different, independently
   trusted roots; the token embeds the one not recorded. The recorded position is rejected as non-unique (the
   per-position search finds the embedded sibling's own path reaches its own trusted root), even though the
   prefix comparison and a bare `openssl ts -verify` call alone both accept it. Generalized to a chain with two
   recorded intermediates, confirming an unaffected inner position stays silent while the affected outer position
   is rejected, and to the token embedding *both* siblings with the stored chain recording whichever `BuildChain`
   happens to select (so the prefix comparison trivially passes) — still rejected once the unselected sibling's
   own path is found to validate fully.
8. The same shared-key-sibling construction, except the only remaining alternate sibling's own path is itself
   constraint-ineligible (extended key usage, then — generalized to three siblings — a name constraint on a
   different sibling than the EKU case) — accepted, proving the search neither reports a false alternate nor
   stops at the first candidate a single call happens to try.
9. `BuildIsolatedChain` itself, given same-root siblings (so restricting a candidate's own trusted root cannot
   separate them), does not leak a still-embedded, unoffered sibling into the result — the isolated call for one
   sibling must succeed using only that sibling's own certificates, not any other still-embedded candidate.
10. A genuine, non-final self-issued intermediate, signed by a name-constrained root, whose complete path
    validates (exempt from the root's name constraint specifically because it is not the path's final
    certificate) is accepted in both tools — proving neither `issuer_signature_ok`/the graph-edge test nor the
    prefix-preserving extension revalidates it as if it were a path's own final target, the way a pairwise
    `-partial_chain` call would.

### 6. Reject an empty `TrustedRoots`

`IntegrityVerificationBuilder.WithTimestampAuthorityTrust` (`IntegrityVerificationBuilder.cs:66-77`) and
`IntegrityAnchoringBuilder.WithRfc3161TimestampAuthority` (`IntegrityAnchoringBuilder.cs:156-168`) both create an
options object, run the caller's `configure` callback, and register it without checking `TrustedRoots`. After
`configure?.Invoke(...)`, throw `ArgumentException` when `TrustedRoots.Count == 0` (`nameof(configure)`), in both
methods. `ChainSettings.BuildChain`'s own "empty means system trust" branch (`TimestampTokenValidation.cs:98-102`)
stays as an internal primitive — unreachable through the two supported configuration entry points once they
validate eagerly — but update its XML doc and `TimestampAuthorityTrust.TrustedRoots`'s/
`Rfc3161TimestampAuthorityOptions.TrustedRoots`'s remarks, since "when empty, the system trust store is used" is no
longer reachable through supported configuration.

`verify.py`'s `main()` already requires at least one `--trusted-root` for the `check` subcommand whenever
`--anchors` is given (`verify.py:2237-2238`); this is existing, preserved behavior — add a regression test for it
rather than new argument validation.

### Test vectors

Extend `TamperEvidenceVectorGenerator.Generate()` (`tests/Chaos.Mongo.EventStore.Tests/Integration/TamperEvidenceVectorGenerator.cs`)
and the committed `spec/tamper-evidence/v1` fixtures with: a stream with zero events; a chain whose non-root
certificates all lack CRL distribution points (`NoRevocationSource`) and one with only some lacking them
(`Unconfirmed`, never `Confirmed`); a chain with demonstrated revocation on one certificate alongside another
certificate's missing coverage (`Revoked`, not `Unconfirmed`); a stream anchored once under one aggregate type name
and then verified under a different, never-anchored-under name with no leaves found
(`AggregateTypeMismatch`); two different aggregate types sharing one `AggregateId`, each resolving only its own
leaves; a token whose rebuilt chain reaches a different, independently trusted root through a longer bridging path
while its stored chain's own root is independently validated (accepted); a token whose stored chain records a
substituted root, including a same-subject/different-key decoy, that never issued its stored intermediate
(rejected); a token whose stored chain records an unrelated, same-subject-named certificate between a signer and
the root that in fact issued it directly (rejected, the exact-depth-count check); a token whose stored chain was
genuinely valid only at the token's own time and has since expired (accepted), and one already expired at that
time (rejected); one violation from each of a path-length constraint (including a cross-certificate variant), an
inherited name constraint, and an extended-key-usage restriction, each manifest only across the complete recorded
path while every individual link is genuine (rejected in both tools, including `application_policy_failure`'s
mirror of .NET's enforcement, since neither a bare nor a `-purpose`-qualified `openssl verify` call reproduces
it); a token whose stored chain lists the same certificates as a genuine path in a different order, made possible
by two certificates sharing a subject name (rejected by the per-link adjacency checks even though the whole-chain,
exact-depth-count call alone would accept it); a shared-key sibling intermediate reachable through a different,
independently trusted root at a recorded position (rejected as non-unique), generalized to two recorded
intermediates — one position affected, one unaffected — and to the token embedding both siblings with the stored
chain recording whichever `BuildChain` selects; the same construction where the only remaining alternate sibling's
own path is itself constraint-ineligible, first by extended key usage and then, generalized to three siblings, by
a name constraint on a different sibling (accepted in both tools, proving the search does not over-reject);
`BuildIsolatedChain` not leaking a still-embedded, unoffered sibling into an isolated candidate's result when two
siblings share a trusted root; and a genuine, non-final self-issued intermediate whose complete path is exempt
from its issuing root's own name constraint (accepted in both tools, even though a pairwise adjacency call that
makes it a final target alone would reject it), all
reproduced in a real
`verify.py` run (§5), not only the
fixture runner; and an anchor chain where an event's first covering
anchor's only token is revoked and a later anchor's token is valid, asserting `DegradedFromAnchor` names the
earlier one, reproduced both in `TamperEvidenceVectorTests`/`verify.py vectors`' fixture replay and in a real
`verify.py check` run over the same fixtures (so the production path, not only the fixture runner, is proven to
tolerate the degradation). Reuse the existing `RenamedStream`
pattern (`TamperEvidenceVectorGenerator.cs:26`, `:66-68`) as a starting point for the rename scenario, but anchor the
stream (the existing vector's verifier has no anchors configured and so does not exercise leaf lookup at all).
`TamperEvidenceVectorTests.ToJson` gains the new `StreamVerificationResult.ComparedLeafCount` field; its
anchors-vector JSON gains the new `EventAnchorVerificationResult.DegradedFromAnchor` field where applicable. Mirror
every new scenario in `verify.py vectors` so both tools reproduce the same committed vectors.
