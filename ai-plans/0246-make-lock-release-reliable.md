# 0246 — Make Lock Release Reliable After a Cancelled Renewal

> Issue: [#246](https://github.com/chA0s-Chris/Chaos.Mongo/issues/246)

## Rationale

`MongoLock` fences renewal and release with the expiry it last observed (`src/Chaos.Mongo/MongoLock.cs:105,110`, `src/Chaos.Mongo/MongoHelper.cs:76-79,102-104`), a design `0110-extend-lock-lease.md` chose deliberately: every successful extension writes a new `LeaseUntilUtc`, so the stored value doubles as an acquisition fence without adding a document field. That design has two gaps. First, if `TryExtendAsync` is cancelled after MongoDB applied the update but before the client observed the result — exactly what the docs recommend doing on shutdown — the lock instance keeps the old expiry. `DisposeAsync` then releases using that stale value, matches no document, and because `DisposeAsync` swallows errors, the lock silently stays held until its lease expires. The same staleness also makes a retried `TryExtendAsync` fail even though nobody else took the lock, a caveat `docs/distributed-locking.md` already documents as a known limitation. Second, two acquisitions that happen to compute the same `LeaseUntilUtc` — plausible since `MongoOptions.HolderId` lets multiple processes share one `Holder` value, and `DateTime` is stored at millisecond precision — are indistinguishable by the current fence, so a stale handle from the first could affect the second.

This plan replaces the expiry-based fence with a per-acquisition token stored on the lock document, generated once when a lock is acquired and never changed by extension. Ownership no longer depends on the client having observed the lease's current value, which closes both gaps above. Because this changes the stored lock document's shape, it must land before 1.0.

## Acceptance Criteria

- [x] A test cancels a renewal after the server applied it (the client never observes the new expiry), disposes the lock, and the lock can be acquired again immediately.
- [x] A former holder can't release or renew a lock someone else now holds, including when two acquisitions compute the same `LeaseUntilUtc`.
- [x] A retried `TryExtendAsync` succeeds against a renewal that reached the server but whose result the caller never observed, instead of incorrectly marking the lock as lost.
- [x] Locks written in the old format (no per-acquisition token) can still expire and be acquired during an upgrade, and `docs/migrating-from-0.x.md` covers it.
- [x] `docs/compatibility-policy.md` covers the new field.
- [x] The Release build has no warnings and the full test suite passes.

## Technical Details

### Per-acquisition token replaces the expiry fence

`MongoLockDocument` (`src/Chaos.Mongo/MongoLockDocument.cs`) gains an internal-set, nullable `AcquisitionToken` property, following the existing `Holder`/`Id` property pattern. `TryAcquireLockAsync` (`src/Chaos.Mongo/MongoHelper.cs:123`) generates one `Guid.NewGuid().ToString()` per successful acquisition (including when it takes over an expired document) and writes it alongside `Holder`/`LeaseUntilUtc` in the same upsert; it never changes afterwards for that acquisition.

`ExtendLockAsync` and `ReleaseLockAsync` (`src/Chaos.Mongo/MongoHelper.cs:66,98`) filter on `Id == lockName && Holder == holder && AcquisitionToken == token` instead of the `LeaseUntilUtc` equality fence; `ExtendLockAsync` keeps its `LeaseUntilUtc > now` liveness clause and keeps updating `LeaseUntilUtc`, but the token itself is immutable for the life of the acquisition. `Holder` stays in the filter for diagnostics and to preserve today's shared-`HolderId` semantics; it is no longer load-bearing for uniqueness since the token already is.

`MongoLock` (`src/Chaos.Mongo/MongoLock.cs`) is `internal`, so its constructor can change freely without a public API break. Its `releaseAction`/`extendAction` delegates no longer need to carry the current expiry — the token identifying "which acquisition" is now closed over once at construction instead of being read from mutable instance state on every call:

```csharp
// Illustrative: the DateTime "which acquisition" parameter is gone from both delegates.
Func<Task> releaseAction
Func<TimeSpan, CancellationToken, Task<DateTime?>> extendAction
```

`TryAcquireLockAsync` closes over `lockName`, `_holderId`, and the freshly generated token when building these delegates for the new `MongoLock`. `MongoLock`'s own expiry tracking (`ValidUntilUtc`, `IsValid`) is unaffected — it keeps caching the last known expiry for those properties, it just no longer needs that value to be correct for release/extend to match.

### Why this closes both gaps

A cancelled renewal that reached the server changes `LeaseUntilUtc` but never the token, so a subsequent release or retried extension — keyed on the token, not the expiry — still matches the document. Two acquisitions that compute the same `LeaseUntilUtc` (and share a `Holder`, per a configured `MongoOptions.HolderId`) still get distinct `Guid`-based tokens, so a stale handle from the first cannot affect the second.

`docs/distributed-locking.md`'s "Extend a held lock" section currently documents the retry caveat this plan removes ("a renewal that reached the server before the response was lost leaves the lock instance tracking a stale expiry, and the retry is then refused even though no other holder took over"); update that paragraph to reflect that a retried extension now succeeds in that case. Leave the surrounding guidance intact: MongoDB/cancellation exceptions from extension still propagate without marking the lock as lost (unrelated to the fencing field), and the stalled-holder overlap window caused by extension-interval and clock-skew is a separate, still-true limitation.

### Compatibility with documents written before this change

`MongoLockDocument` is `[BsonIgnoreExtraElements]` and `AcquisitionToken` is nullable, so a document written by an earlier release deserializes with `AcquisitionToken == null`. Such a document is never extended or released by code that didn't acquire it — those delegates only run against the `MongoLock` instance returned by the acquisition that created them — so the missing field only matters on the take-over path, which stays `Id == lockName && LeaseUntilUtc <= now` and is unaffected by the new field, **for a document that has never carried a token**. No document-format/version flag is needed. Mirror the existing `MongoLockDocumentTests` "released shape" deserialization test (which proves the pre-#242 BSON shape still reads back) with an equivalent case for a document that predates `AcquisitionToken`.

**Boundary this does not cover:** an older (pre-this-change) instance's take-over update is `$set`-only on `Holder`/`LeaseUntilUtc` — it has no `AcquisitionToken` field to write. If that take-over lands on a document that already carries a token (because a *newer* instance acquired it first), the stale token survives unchanged, and the original newer-version handle that captured it can still release or renew the lease the older instance now believes it owns. This only matters when multiple instances share one configured `MongoOptions.HolderId`; the default, independently generated `HolderId` makes it unreachable. The supported rolling-upgrade transition is therefore: upgrade every instance sharing a `HolderId` together, or give each instance a unique `HolderId` for the duration of any overlap. Document this boundary rather than redesigning ownership to survive it — add a deterministic integration test to `MongoHelperLockIntegrationTests` that replays an older binary's exact `$set`-only take-over update (via `Builders<MongoLockDocument>.Update.Set` on `Holder`/`LeaseUntilUtc` only, applied directly through the test's `IMongoCollection<MongoLockDocument>`) against an expired, token-bearing document from a real `TryAcquireLockAsync` acquisition, and asserts both that the token on the document is unchanged afterward and that the original handle's `DisposeAsync` still deletes it.

Add an entry to `docs/migrating-from-0.x.md`'s distributed-locking section (new section, following the existing per-area heading style) stating: lock documents gain an internal per-acquisition token; release and extension now key off it instead of the last-known lease expiry, which also fixes a renewal that reached the server but whose response the caller never observed (including a cancelled renewal on shutdown) from leaving the lock held past disposal or refusing a legitimate retry. A lock already held when upgrading, or taken over by an older instance after its lease expired, lacks the token but still expires and can be acquired normally. No public API changed (`MongoLock`/`MongoLockDocument` are internal), so there is nothing for consumers to change beyond this behavior note. State the shared-`HolderId` rolling-upgrade boundary above explicitly, as its own callout in that section.

Update `docs/compatibility-policy.md`'s "Stored data" section to state that the lock document's per-acquisition token is an internal implementation detail, not part of the documented contract; that a document written without it (by an earlier release, or by an older instance taking over an expired lock) keeps expiring and being reacquired normally; and that this assumes no instance sharing a configured `HolderId` runs an older, pre-token release concurrently against the same lock, per the migration guide's boundary.

### Testing

Unit coverage in `MongoLockTests` follows the existing fake-delegate approach, updated for the new `Func<Task>`/`Func<TimeSpan, CancellationToken, Task<DateTime?>>` signatures.

The "cancelled renewal, server applied it" scenario must exercise an actual cancellation through `MongoLock.TryExtendAsync`, not merely a stale-state simulation, so it belongs in `MongoHelperLockIntegrationTests`. Acquire a lock via `TryAcquireLockAsync` to get the real document and token, then construct a second `MongoLock` directly against that same `Id`/token (the internal constructor is already used this way elsewhere in the test suite) with an `extendAction` double that first awaits the real `MongoHelper.ExtendLockAsync` call — so MongoDB genuinely applies the new `LeaseUntilUtc` — then cancels the token it was given and calls `cancellationToken.ThrowIfCancellationRequested()` instead of returning the result, standing in for cancellation observed after the server-side effect. Call `TryExtendAsync` with that cancellable token and assert the `OperationCanceledException` propagates and the instance's cached expiry stays at the pre-renewal value. Dispose that instance and assert the document is gone (release matched via the token, not the stale cached expiry), then assert `TryAcquireLockAsync` for the same name immediately succeeds.

Keep a separate, simpler test for the retried-extension criterion: call the internal `ExtendLockAsync` directly once to move `LeaseUntilUtc` forward out from under the original `mongoLock` (no cancellation involved), then call `mongoLock.TryExtendAsync()` and assert it succeeds (fixing today's documented false "lost" result). This test does not stand in for the cancellation scenario above.

The legacy-format takeover criterion must also be proven against a live document, not only via the `MongoLockDocumentTests` deserialization case. Add an integration test to `MongoHelperLockIntegrationTests` that inserts the pre-`AcquisitionToken` BSON shape directly (`_id`, `Holder`, `LeaseUntilUtc`, no `AcquisitionToken`) using a `FakeTimeProvider`-driven clock, then: asserts `TryAcquireLockAsync` for that name is refused while the frozen clock is still before the seeded `LeaseUntilUtc`; advances the clock past it and asserts `TryAcquireLockAsync` succeeds, writes a non-empty `AcquisitionToken`, and that the returned lock's `TryExtendAsync` and `DisposeAsync` both behave normally afterward. The existing deserialization case stays as complementary coverage for the BSON shape itself, not a substitute for proving the live take-over path.

The "two holds with the same expiry" scenario is a unit test against `MongoHelper`'s filter construction logic or an integration test that freezes a shared `TimeProvider` across two sequential acquisitions of the same lock name (second after the first is released) so both compute an identical `LeaseUntilUtc`, then shows that releasing via the first (already-disposed) lock's captured delegate does not touch the second acquisition's document — mirroring the existing release-fencing test in `MongoHelperLockIntegrationTests` from `0110-extend-lock-lease.md`, adapted to prove the fence now holds even when the expiries collide.
