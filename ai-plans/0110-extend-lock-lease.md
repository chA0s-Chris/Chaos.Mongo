# 0110 — Extend the Lease of a Held Distributed Lock

> Issue: [#110](https://github.com/chA0s-Chris/Chaos.Mongo/issues/110)

## Rationale

A lock's lease is fixed when it is acquired, so a long-running holder can lose exclusivity while it is still working. Raising the lease at acquisition is not an acceptable answer, because the lease also bounds how long the lock stays unclaimable after a holder crashes — a longer lease buys safety margin for slow work at the cost of slow recovery.

Extending decouples the two concerns: callers keep a short initial lease for fast crash recovery and renew it while work is still in progress. This plan adds the renewal primitive only. Automatic renewal is deliberately out of scope; it raises separate questions about renewal intervals and how many transient failures to tolerate before abandoning the protected work, and should be revisited once the primitive has real usage.

## Acceptance Criteria

- [ ] `IMongoLock` exposes a `TryExtendAsync` method that renews the lease of a held lock and updates `ValidUntilUtc`
- [ ] Calling `TryExtendAsync` without an explicit lease duration renews for the duration the lock was originally acquired with
- [ ] Extension is refused, without modifying the stored lock document, when the lease has already expired, when the document is held by a different holder, or when the lock has been disposed
- [ ] A MongoDB failure during extension propagates to the caller, does not mark the lock as lost, and leaves `ValidUntilUtc` unchanged
- [ ] A refused extension marks the lock as no longer valid, so `IsValid` reports `false` and the existing `EnsureValid()` extension throws afterwards
- [ ] `MongoLockExtensions` offers a throwing `ExtendAsync` companion that returns the same lock after a successful extension, surfaces a refused extension as an `InvalidOperationException`, and propagates cancellation and MongoDB failures unchanged
- [ ] The `MongoLock` constructor rejects a null extend delegate, and the constructor, `TryAcquireLockAsync`, and `TryExtendAsync` reject lease durations shorter than one millisecond before invoking a database operation or delegate, with guard-clause coverage matching the existing constructor tests
- [ ] Concurrent extension and disposal of the same `MongoLock` instance cannot resurrect a released lock or leave `ValidUntilUtc` inconsistent
- [ ] Releasing a lock deletes only the document carrying that lock's own lease expiry, so disposing a lost or expired lock cannot release a later acquisition made by the same holder
- [ ] Automated unit and integration tests cover successful renewal, every refusal case, the post-refusal validity state, sub-millisecond lease rejection, and release fencing
- [ ] A deterministic unit test pauses an in-flight extension while disposal starts, then verifies that disposal uses the resulting expiry and that the disposed lock remains invalid rather than being resurrected
- [ ] Concurrent real-MongoDB tests using a shared frozen `TimeProvider` verify both expiry-boundary outcomes: before expiry, extension succeeds, acquisition returns `null`, and the document contains the extended lease; at or after expiry, extension returns `false`, acquisition succeeds, and the document contains the new holder and expiry
- [ ] `docs/distributed-locking.md` documents extension, states that an expired lock cannot be renewed, and explains that extension narrows but does not close the window in which a stalled holder overlaps a new one — including clock skew between instances as a cause, since expiry is evaluated against client clocks
- [ ] The pull request carries a `breaking` label so release-drafter files the constructor and interface changes under "Breaking Changes" — satisfied at pull-request creation, not during implementation

## Technical Details

### Renewal semantics

Extension renews **from the current time**, not from the existing `ValidUntilUtc`. Accumulating onto the previous expiry would let an eager caller push expiry arbitrarily far into the future, reintroducing the long-lease problem this feature exists to solve.

An expired lock is **not** extendable. The atomic update in `MongoHelper` mirrors the take-or-steal filter already used by `TryAcquireLockAsync`, with the liveness condition inverted:

```text
Id == lockName && Holder == holderId && LeaseUntilUtc > now
```

The `LeaseUntilUtc > now` clause is the load-bearing part. Without it, a holder whose lease lapsed — but whose document nobody has stolen yet — could silently resurrect the lock while another instance is about to claim it. Failing closed is the only defensible default for a mutex: the caller learns it lost the lock and must abort or acquire again from scratch.

Exceptions from the underlying MongoDB operation propagate to the caller and leave the lock's state untouched. `false` is reserved for a definitive loss of the lock, so callers can distinguish a transient failure worth retrying from a lost lock that must abort the work. This distinction is the contract renewal loops depend on, and it matters precisely because automatic renewal is out of scope here.

Lock expiry is evaluated against the client's `TimeProvider` rather than the server clock, inherited from the existing acquisition path. Clock skew between instances therefore widens the window in which two holders can believe they hold the lock.

Lease durations shorter than one millisecond are rejected because MongoDB stores `DateTime` at millisecond precision. `TryAcquireLockAsync` validates before issuing any database operation, while the `MongoLock` constructor and `TryExtendAsync` validate before invoking their delegates. With a minimum one-millisecond lease, an acquisition that matches an expired stored lease necessarily writes an expiry at least one stored millisecond later, preserving the expiry-based release fence.

### Components

`MongoHelper` gains an `internal` extend operation alongside `ReleaseLockAsync`, performing a `FindOneAndUpdate` with the filter above and returning the new expiry, or `null` when the filter matched nothing. `TryAcquireLockAsync` passes it to the constructed `MongoLock` together with the lease duration it used. `ReleaseLockAsync` takes the lock's current expiry as an additional filter value, per the fencing rule below.

Both acquisition and extension must hand `MongoLock` the expiry **the server returned**, not the locally computed one. MongoDB stores `DateTime` at millisecond precision, so only the stored value can be matched by an equality filter; `TryAcquireLockAsync` currently constructs the lock from its local `leaseUntil` and must use the returned document's `LeaseUntilUtc` instead.

`MongoLock` takes the extend delegate and the original lease duration as **required** constructor parameters. This breaks the existing public constructor's source and binary compatibility, which is acceptable at 0.x; it is preferred over an optional parameter or a second overload because it makes a `MongoLock` that cannot be extended unrepresentable.

Adding `TryExtendAsync` to `IMongoLock` is a second breaking change: external implementers, including hand-written test doubles, no longer satisfy the interface. This is accepted rather than softened with a default interface implementation, which would hand implementers a lock that silently cannot be extended — the same unrepresentable state the constructor decision rules out.

Both breaks are communicated through the release notes, which are generated by release-drafter rather than edited by hand. The `breaking` label must be applied to the pull request; `.github/release-drafter.yml` already maps it to the "💥 Breaking Changes" category, and without it the change would be drafted as an ordinary feature.

```csharp
// Exact signatures.
public MongoLock(String id,
                 DateTime validUntilUtc,
                 TimeSpan leaseTime,
                 TimeProvider timeProvider,
                 Func<DateTime, Task> releaseAction,
                 Func<TimeSpan, CancellationToken, Task<DateTime?>> extendAction)

Task<Boolean> IMongoLock.TryExtendAsync(TimeSpan? leaseTime = null,
                                        CancellationToken cancellationToken = default);

public static Task<IMongoLock> ExtendAsync(this IMongoLock mongoLock,
                                           TimeSpan? leaseTime = null,
                                           CancellationToken cancellationToken = default);
```

`ValidUntilUtc` becomes settable privately on `MongoLock`; the interface member stays read-only, so consumers are unaffected.

`ExtendAsync` calls `TryExtendAsync` and returns the same lock instance on success. It rejects a null receiver, converts only a definitive `false` result into `InvalidOperationException`, and lets cancellation and MongoDB exceptions propagate unchanged.

### State and thread safety

A refused extension is terminal for the instance: record it in a lost flag that `IsValid` honours alongside the existing disposed flag.

Disposal needs more than the current holder filter. `MongoHelper` resolves one holder id per instance, so every lock it hands out carries the same `Holder` value and `ReleaseLockAsync` cannot distinguish a stale lock from a later re-acquisition of the same name — disposing a lost or expired lock would delete the document its successor owns, silently freeing a lock the caller believes it holds. Release must therefore also match the lease expiry the lock instance holds:

```text
Id == lockName && Holder == holderId && LeaseUntilUtc == <this lock's expiry>
```

Every later acquisition writes an expiry distinct from the expired value it matched, so the stored value acts as an acquisition fence without adding a document field. Extensions do not need to produce a distinct value on every call; they update the same lock instance's current expiry. This is preferred over a local "skip the release when lost or expired" check because it is evaluated atomically on the server, and because it also covers two processes sharing a configured `MongoOptions.HolderId` — something no local check can see.

`MongoLock` must be safe for concurrent use. The intended usage — renewing from a timer while the protected work runs on another thread — makes concurrency inherent rather than exceptional, so the mutable state (disposed flag, lost flag, `ValidUntilUtc`) has to be guarded, and disposal must not race with an in-flight extension in a way that resurrects a released lock. Disposal reads the current expiry under that synchronization and passes it to the `Func<DateTime, Task>` release delegate: when extension wins the synchronization race, disposal observes its returned expiry; when disposal wins, the extension is refused without invoking its delegate. This is a cold path, so favour the simplest construct that makes the invariant obvious over lock-free cleverness.

### Testing

Unit coverage drives the refusal cases through the public API against a fake extend delegate and a controlled `TimeProvider`, following the existing sociable-test approach in `MongoLockTests` and `MongoLockExtensionsTests`. Integration coverage extends `MongoHelperLockIntegrationTests` with the cases that only a real server can prove: a successful renewal observed in the lock document, a refused renewal after another holder has stolen an expired lock, a refused renewal on a lease that expired while unclaimed, and — for the release fencing — disposing a stale lock after the same helper has re-acquired the same name, asserting the successor's document survives.

Two real-MongoDB concurrency tests give both helpers the same frozen `TimeProvider` and start extension by the current holder and acquisition by a competing holder concurrently. With time frozen before expiry, extension must succeed, acquisition must return `null`, and the stored document must contain the extended lease. With time frozen at or after expiry, extension must return `false`, acquisition must succeed, and the stored document must contain the new holder and expiry. These tests verify the complementary filters and atomic document updates under a common clock; they do not claim to close the documented overlap window caused by skewed client clocks.

The constructor change touches all existing `MongoLock` construction sites — 21 across `MongoLockTests` and `MongoLockExtensionsTests`, plus the single production site in `MongoHelper`. The migration is mechanical, but the new parameters need guard-clause coverage matching the existing null-`releaseAction` test.

The extension/disposal concurrency criterion is verified without timing delays by a controllable extend delegate backed by `TaskCompletionSource`: pause extension inside the delegate, start disposal, complete extension, and assert that disposal releases with the returned expiry while the lock remains disposed and invalid. The test complements the synchronization invariant rather than relying on an opportunistic race.

The repository's 95% merged line coverage threshold applies to this change and is a completion condition, not a CI detail to discover afterwards.
