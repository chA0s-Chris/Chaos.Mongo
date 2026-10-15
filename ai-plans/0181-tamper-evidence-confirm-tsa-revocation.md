# Tamper evidence: confirm TSA revocation status with CRLs issued after the token time

> Issue: [#181](https://github.com/chA0s-Chris/Chaos.Mongo/issues/181)

## Rationale

Each timestamp entry on an anchor carries the CRLs of the authority's chain that were downloaded right after stamping (#158). Those CRLs are almost always issued before the token's time. They therefore can't show that no certificate was revoked between their `thisUpdate` and the token's time, a gap as long as the CA's CRL issuance interval. The online check during chain building covers that gap at stamping time, but its result is not kept as evidence.

Long-term validation closes the gap with a grace period: once it has passed, the evidence is completed with revocation data issued after the time to be proven plus the grace period (ETSI EN 319 102-1; CAdES-X-L, RFC 5126). The anchor format is still a draft until #160 freezes it, so it can change now. Everything stays `internal`.

## Acceptance Criteria

- [ ] After a configurable grace period (default 24 hours) following a token's time, an anchoring run obtains CRLs for every CRL distribution point of the entry's non-root chain certificates. It accepts only CRLs whose `thisUpdate` is at or after the token's time plus the grace period. Until such CRLs are available for every point, the entry stays unconfirmed and later runs retry.
- [ ] A CRL that passes the checks but was issued too early leaves the entry unconfirmed. It does not count as a failure or log a warning, and the entry's chain is not requested again before that CRL's `nextUpdate` or one hour later, whichever comes first. Download errors, and CRLs that fail the checks at every location of a point, count as failures and are logged.
- [ ] Confirming CRLs pass the same checks as stamping-time CRLs: a single DER-encoded CRL of the expected issuer, stating a next update, not issued later than the local time allows, and within the response size limit. A CRL that fails these checks counts as unavailable at that location.
- [ ] A timestamp entry stores its confirming CRLs beside the stamping-time CRLs, together with:
  - its revocation status (unconfirmed, confirmed or revoked)
  - when that status was recorded
  - the number of failed confirmation attempts and the last failure
- [ ] When a confirming CRL lists a chain certificate as revoked at or before the token's time, the entry is marked revoked with its CRLs, an error is logged and a metric is incremented. The anchor's status does not change.
- [ ] Confirmation does not block anchoring:
  - anchors complete at the configured minimum of tokens, as today
  - confirmation runs in every run that holds the lock, including when the anchor awaiting timestamps cannot complete and when the anchoring phases throw; the anchoring exception still fails the run afterwards
  - a failed confirmation is logged and retried later, without failing the run or affecting other entries
- [ ] When the oldest unconfirmed entry's token time is older than a configurable threshold (default 72 hours), this is reported through a log entry and a metric, like the anchor delay.
- [ ] The grace period and the threshold are configurable through the anchoring builder. Registration rejects a non-positive grace period and a threshold that does not exceed the grace period.
- [ ] Automated tests cover:
  - confirmation after the grace period
  - CRLs issued too early, which are retried
  - each CRL check
  - revoked chain certificates, including a revocation after the token's time, which does not count
  - chains without distribution points
  - a run whose pending anchor cannot complete
  - a run whose anchoring phases throw
  - the unconfirmed-delay report
  - the configuration

## Technical Details

### Obtaining confirming CRLs

- `ITimestampAuthority` gains a member that returns the CRLs for a stored certificate chain, each issued no earlier than a given time. The signature below is illustrative:

  ```csharp
  Task<RevocationLists> GetRevocationListsAsync(IReadOnlyList<Byte[]> certificateChain,
                                                DateTimeOffset issuedNotBefore,
                                                CancellationToken cancellationToken);
  ```

  `RevocationLists` (new) either holds the CRLs, or reports that a point's CRL was issued too early, together with the earliest `nextUpdate` among those CRLs. `TimestampAuthorityException` is thrown only for errors. Custom authorities must implement it, which is acceptable because the interface stays internal until #160.
- `Rfc3161TimestampAuthority` reuses its stamping-time path:
  - the distribution-point sources
  - the download over its `HttpClient` across a point's locations
  - the checks in `CheckCrl`
  - the size bound and `AttemptTimeout`

  It adds the `issuedNotBefore` check. A chain that names no distribution points yields an empty list, so its entry is confirmed with no CRLs, which matches stamping time.
- The run re-checks every returned CRL's `thisUpdate` with `CertificateRevocationListHeader`, so the acceptance rule also holds for custom authorities.
- An entry's authority is matched by name. If an entry's authority is no longer configured, the entry stays unconfirmed and shows up in the delay report.

### Revocation check

- A new internal decoding of CRL entries, built with `System.Formats.Asn1` beside `CertificateRevocationListHeader`, reads each entry's serial number, `revocationDate`, and the `invalidityDate` and `certificateIssuer` entry extensions.
- A chain certificate counts as revoked when an entry matches its issuer and serial number and the earlier of `revocationDate` and `invalidityDate` lies at or before the token's time.
- The entry's issuer is the CRL issuer unless a `certificateIssuer` extension of an indirect CRL says otherwise (RFC 5280, section 5.3.3).
- A `certificateHold` entry is treated as a revocation; for an alert, the conservative choice is right.
- CRL signatures are still not verified; that remains #160's job. A revoked marking is therefore an alert and stored evidence, not a final verdict.
- The anchor stays `Completed`. The next anchor's tokens cover it through the digest chain, so its evidence degrades to that later time instead of being lost. Whether to reopen anchors is out of scope.

### Run integration

- Confirmation is a separate step of `AnchoringRun`, run after the anchoring phases under the same lock and whatever their outcome, including an exception, which is rethrown after confirmation; it is skipped only when the lock is lost. It checks the lock with `EnsureLockAsync` before each authority call.
- It covers entries of `AwaitingTimestamp` and `Completed` anchors whose token time plus the grace period has passed.
- Downloads are bounded per run. Each authority is asked once per distinct certificate chain, with the earliest `issuedNotBefore` among that chain's due entries. The CRLs it returns confirm every due entry of that chain whose `issuedNotBefore` they meet. A backlog after an outage therefore costs one download set per chain, not one per entry.
- When a chain's CRL was issued too early, the next attempt time is recorded for that chain (per authority and chain), and the run skips the chain until then, including for entries that become due in the meantime. This is safe because a CRL that is too early for the chain's earliest due entry is too early for all later ones. The pacing therefore holds across instances. Where the time is stored, in a small state document per authority and chain or on the chain's entries, is an implementation choice. The one-hour cap is a fixed internal value; it is needed because many CAs state a `nextUpdate` days ahead while issuing CRLs daily.
- Each entry update is filtered by the entry's unconfirmed status, which keeps updates idempotent and crash-safe. An entry is identified by its authority and token rather than by authority alone, because RFC 4998 renewal may later add more entries for the same authority.
- `AnchorStore` gets an index that lets a run find anchors with unconfirmed entries without scanning the whole anchors collection, which grows by about 8,600 anchors a month at the default interval.

### Storage, options and diagnostics

- `AnchorTimestamp` gains the confirming CRLs, the revocation status, its recording time and the confirmation failures. They are stored the same way as the stamping-time CRLs, so #171's content-addressed evidence store can take both: whichever of #171 and this issue lands second moves both. Until #171 lands, confirmation roughly doubles the CRL bytes embedded per entry.
- `IntegrityAnchoringOptions` and `IntegrityAnchoringBuilder` gain the grace period and the unconfirmed-delay threshold. The 72-hour default covers the 24-hour grace period plus one daily CRL interval with a margin.
- `IntegrityAnchoringDiagnostics` gains three instruments (new): the age of the oldest unconfirmed entry, a counter of runs above the threshold, and a counter of entries marked revoked. `IntegrityAnchoringService` reports them like the anchor delay.
- The completion hook is unchanged. Its documentation states that a `CompletedAnchor`'s entries are usually unconfirmed when it is delivered, and that confirming CRLs are read from the store and, later, through #160.
