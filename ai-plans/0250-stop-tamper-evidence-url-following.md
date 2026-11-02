# Stop tamper-evidence verification from following URLs in stored certificates

## Rationale

[Issue #250](https://github.com/chA0s-Chris/Chaos.Mongo/issues/250): stored timestamp
data (a token's certificate chain) can be written by anyone with database access, and
tamper-evidence verification is the step that is supposed to distrust it. Two code paths
currently let that stored data make the process issue outbound HTTP requests:

- `TimestampTokenValidation.BuildChain` builds an `X509Chain` without setting
  `ChainPolicy.DisableCertificateDownloads`, so .NET's chain engine may fetch a
  certificate named by an Authority Information Access (AIA) extension of a chain
  certificate. `BuildChain` is used both when validating a token at receipt
  (`Rfc3161TimestampAuthority.TimestampCoreAsync`, `TimestampTokenValidation.ValidateReceived`)
  and, more sensitively, when `StoredTimestampVerifier` rebuilds the chain from
  certificates read back from storage long after receipt.
- `TimestampRevocationConfirmer.ConfirmChainAsync` reads a stored certificate chain by
  its stored hashes and passes it straight to `ITimestampAuthority.GetRevocationListsAsync`,
  which (for the built-in `Rfc3161TimestampAuthority`) downloads the CRL named by each
  distribution point of that chain. The confirmer never builds the stored chain to a
  trusted root first, so a chain whose certificates were substituted or inserted directly
  in the database can still name a CRL distribution point the confirmer will fetch — a
  stored-data-driven SSRF, independent of whether the chain, or the token it belongs to,
  is otherwise valid.

Both gaps let stored bytes decide which external location the server contacts, which is
exactly what tamper-evidence verification must not allow. The fix is to stop certificate
downloads during chain building unconditionally, and to require a stored chain to build
to a trusted root before any URL it names is used.

## Acceptance Criteria

- [x] Building a token's certificate chain — at receipt and when `StoredTimestampVerifier`
      rebuilds a stored chain — never fetches a certificate from an AIA location named by a
      certificate of the chain, even when the configured or default trust would otherwise
      accept the chain. A test verifies a token whose signing or intermediate certificate
      names an AIA URL and asserts no request reaches it.
- [x] `TimestampRevocationConfirmer` does not request any authority's CRLs for a stored
      certificate chain unless building that chain to a trusted root yields exactly the
      stored certificates (because no trust is configured for the authority, the stored
      root does not match the configured trusted roots, the stored chain is otherwise
      malformed or untrusted, or the stored chain contains a certificate that is not part
      of the path built to a trusted root). A test demonstrates that no CRL request is made
      for such a chain and that its due entries are retried later, the same way other
      authority failures are; this includes a chain that still builds to a trusted root for
      the token's real signer but carries an additional, unrelated certificate naming its
      own CRL distribution point.
- [x] `TimestampRevocationConfirmer` still requests and records CRLs, unchanged from
      current behavior, for a stored chain that does build to a trusted root (regression
      coverage distinguishing this from the previous criterion).
- [x] `docs/event-store.md` states, in the Anchoring section, when the library makes
      network requests (the TSA timestamp request; CRL downloads at receipt and during
      revocation confirmation, restricted to HTTP/HTTPS distribution points) and that
      building a certificate chain never fetches a certificate to complete it.
- [x] The Release build has no warnings and the full test suite passes.

## Technical Details

**Disable certificate downloads unconditionally in `BuildChain`.**
`TimestampTokenValidation.BuildChain` (`src/Chaos.Mongo.EventStore/Integrity/TimestampTokenValidation.cs:80-125`)
is the only place in `src` that constructs an `X509Chain`. Set
`chain.ChainPolicy.DisableCertificateDownloads = true` there unconditionally; it does not
need to become a `ChainSettings` option. Every call site already supplies the full
certificate set the chain needs through `ChainPolicy.ExtraStore` — the token's own
certificates plus `settings.ExtraCertificates` (the stored or received chain) — so a
legitimate chain never needed a download, and an incomplete chain now fails chain
building instead of triggering a fetch. This covers all three callers:
`Rfc3161TimestampAuthority.TimestampCoreAsync`, `TimestampTokenValidation.ValidateReceived`,
and `StoredTimestampVerifier.VerifyToken`.

**Validate the stored chain before trusting any URL it names, in `TimestampRevocationConfirmer`.**
`ConfirmChainAsync` (`TimestampRevocationConfirmer.cs:125-171`) currently reads the stored
certificate chain via `ReadChainAsync` and passes it directly to
`authority.GetRevocationListsAsync`, with no chain-of-trust check; the confirmer is also
constructed (`AnchoringRun.cs:92`) without the `IntegrityVerificationOptions` that carries
per-authority `TimestampAuthorityTrust`, so today it has no trust information to check
against even if it wanted to.

- Thread an `IntegrityVerificationOptions?` parameter into `TimestampRevocationConfirmer`'s
  constructor, the same options object `AnchoringRun` already holds (`_verification`) and
  already passes to `TimestampTokenValidation.ValidateReceived` for the receipt-time trust
  check (`AnchoringRun.cs:454`); pass `_verification` through at `AnchoringRun.cs:92`.
- Before calling `authority.GetRevocationListsAsync` in `ConfirmChainAsync`, require
  `verification?.FindTrust(authority.Name)` to be non-null — the same precondition
  `StoredTimestampVerifier.Verify` enforces (rejecting when `trust is null`, before
  building any chain) and `AnchoringRun.cs:454` already applies by only calling
  `BuildChain` when `trust is not null`. An authority with no configured trust is treated
  as untrusted, not as falling back to empty `TrustedRoots`/the system trust store; an
  empty `TrustedRoots` only applies once a `TimestampAuthorityTrust` for the authority
  exists.
- Given that trust, build the group's stored chain with `TimestampTokenValidation.BuildChain`,
  using `trust.TrustedRoots` and `X509RevocationMode.NoCheck` — this check only establishes
  trust in the chain's path, not its revocation status, which the confirmer is in the middle
  of establishing. `BuildChain` needs the decoded token and its signing certificate; read the
  chain group's token bytes from `AnchorEvidenceStore` by one entry's `TokenHash` (any entry in
  the group names the same chain) and decode with `Rfc3161TimestampToken.TryDecode`, mirroring
  how `StoredTimestampVerifier.Verify` already loads and decodes a stored token.
- `BuildChain` selects a valid path from the candidates in `ChainPolicy.ExtraStore`; it does
  not confirm that every certificate of the stored chain array is on that path, only that a
  valid path exists. Require the built chain's certificates to equal the stored chain array
  exactly, in order — the same comparison `StoredTimestampVerifier.cs:218-220` and
  `TimestampTokenValidation.ValidateReceived` (`:458-461`) already apply — before treating the
  stored chain as trusted. A stored chain that builds a valid path for the token's real signer
  while also carrying an extra or substituted certificate (for example one naming an unrelated
  CRL distribution point) fails this comparison and must not reach `GetRevocationListsAsync`.
- When trust is absent, the chain fails to build to a trusted root, or the built chain does
  not match the stored one, do not call `GetRevocationListsAsync` for it. Let the failure
  flow through the same path as any other authority error: the existing
  `catch (Exception ex) when (...)` block in `ConfirmChainAsync` already logs a warning,
  defers the chain's due entries by `MaximumRetryDelay` through
  `RecordTimestampConfirmationFailureAsync`, and leaves other chains unaffected — throwing a
  `TimestampTokenValidationException` for the absent-trust and chain-mismatch cases, from the
  same earlier, moved-up chain-build step, is enough; no new control flow is needed.
- This makes the confirmer apply the same rule `StoredTimestampVerifier.Verify` already
  applies (no configured trust, or a stored chain that differs from the one built to a
  trusted root, is an invalid chain, not a chain the process should still make requests
  for), so one policy governs both the on-demand verifier and the background confirmer.

**Documentation.** In the Anchoring section of `docs/event-store.md` (around the existing
`AttemptTimeout`/`RevocationMode`/`TrustedRoots` option table at lines 974-983 and the
revocation-confirmation paragraph around line 1005), add a short statement that: chain
building never fetches a certificate to complete the chain, regardless of trust settings;
and CRL downloads — at receipt and during revocation confirmation — are limited to HTTP
and HTTPS distribution points of a chain that has first been established to a trusted
root, bounded by the existing `AttemptTimeout`/`RevocationCheckTimeout` and
`MaximumResponseSize` settings.

**Testing.** The first acceptance criterion needs a real request destination, not the
existing `TestTimestampAuthority` `HttpMessageHandler` double
(`tests/Chaos.Mongo.EventStore.Tests/Integrity/TestTimestampAuthority.cs`): that double is
wired only into the `HttpClient` used for TSA requests and CRL downloads
(`options.HttpClientFactory`), but .NET's `X509Chain` engine fetches an AIA-named
certificate over its own connection, never through an injectable `HttpClient`, so the
double cannot observe or serve such a request; a chain with every certificate already
supplied also produces no AIA request regardless of the fix, so that case alone would not
exercise it either. Bind a real loopback HTTP listener instead (for example
`System.Net.HttpListener` on an ephemeral `localhost` port; there is no existing
loopback-server precedent in `tests/`, so this is new, narrowly scoped test
infrastructure), and build a test chain whose intermediate certificate is omitted from the
token's own certificates and from `ChainSettings.ExtraCertificates`, so completing the
chain requires fetching it, with an Authority Information Access extension (OID
`1.3.6.1.5.5.7.1.1`) naming the listener's URL, built by hand the same way
`TestTimestampAuthority` already builds the CRL Distribution Points extension (OID
`2.5.29.31`). Configure the root as trusted and `X509RevocationMode.NoCheck` to isolate
the AIA behavior from revocation checking. Assert that, with the fix applied, no request
reaches the listener and chain building fails because the intermediate is missing; pair it
with a positive control — the same setup but without disabling certificate downloads —
that does reach the listener and completes the chain, confirming the test can actually
detect the vulnerability it targets rather than passing regardless of the fix.
The second criterion (including its inserted-certificate case) and third criterion need a
chain that builds to a trusted root versus one that does not (for example by giving the
test authority's root a trust configuration that does not match it, as
`AnchoringRunTests.RunAsync_KnownTrustSettings_AreRequiredOnReceipt`,
`tests/Chaos.Mongo.EventStore.Tests/Integration/AnchoringRunTests.cs:413`, already does for
the receipt-time check) and asserting on whether a CRL request reached the
`TestTimestampAuthority` handler — which, unlike the AIA case, the confirmer's call to
`ITimestampAuthority.GetRevocationListsAsync` does route through that authority's own
`HttpClient` — and on the entries' deferred/confirmed outcome, likely as `AnchoringRunTests`
or `AnchoringRun`-level integration tests alongside the existing CRL-related cases there
(e.g. around `AnchoringRunTests.cs:98`, `:381`).
