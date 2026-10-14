# Tamper evidence: personal data commitments and erasure

> Issue: [#159](https://github.com/chA0s-Chris/Chaos.Mongo/issues/159)

## Rationale

Protected events are sealed into a hash chain over their stored bytes (#156) and anchored with RFC 3161 timestamps (#158). Without further measures, erasing personal data from an event breaks its hash and every anchor that covers it. Section 4 of [#154](https://github.com/chA0s-Chris/Chaos.Mongo/issues/154) decided to hash a salted commitment in place of each marked value. The value can then be set to `null` and its salt discarded, while the chain and anchors stay verifiable. A chained erasure event documents why values are missing.

The work stays one issue because erasure is meaningless without commitments, and commitments only exist to make erasure possible. It is delivered as a two-layer stack:

1. Commitments: the marking, the format and the verification that change what every protected append and seal writes.
2. Erasure: the transactional erasure operation and the reporting of erasures.

Everything stays internal until #160 makes the feature public.

## Acceptance Criteria

### Layer 1: Commitments

- [x] Personal data is marked through a new internal overload `WithEvent<TEvent>(discriminator, e => e.WithPersonalData(x => …))`. Nested member chains are supported, and nothing new is publicly reachable; the existing visibility test covers the new types and members.
- [x] Registration fails with a clear error when a marked member:
  - is unmapped
  - is non-nullable
  - traverses an array, or a segment whose serializer cannot resolve member element names
  - is not a plain member-access chain
  - overlaps another marked path of the same event type, that is, one path is a prefix of the other
- [x] Marked element paths are resolved through the class maps, so renamed elements are respected.
- [x] Append sealing and retroactive sealing both record `{ Path, Salt, Commitment }` in `_integrity` for every marked path present in the stored document. `Salt` is 32 random bytes.
- [x] Events without recorded paths keep the existing `_integrity` format.
- [x] The chain hash covers the canonical form, in which the value of every recorded path is replaced in place by its commitment, followed by a binding of the recorded paths.
- [x] Both append paths persist byte-identical documents for events without recorded paths. For events with recorded paths, the documents are identical outside `_integrity`, and both verify.
- [x] Stream verification reports a failure at the first version whose marked value no longer matches its commitment while its salt is present.
- [x] A recorded path without a salt verifies only when its value is `null`; otherwise stream verification reports a failure.
- [x] Stream verification reports a recorded path that does not resolve in the stored document as malformed integrity data.
- [x] Verification and canonicalization use only the paths recorded in `_integrity`. Changing or removing marks does not break verification of previously sealed events.
- [x] Automated tests cover the behavior above.

### Layer 2: Erasure

- [x] A new internal `ErasePersonalDataAsync(Guid aggregateId, ErasureRequest request, CancellationToken)` performs the following in one transaction. On failure, nothing is persisted.
  1. Sets the selected values to BSON `null` in place and removes their salts; commitments stay.
  2. Appends a chained erasure event.
  3. Rebuilds the read model.
  4. Rebuilds the affected checkpoints.
- [x] After erasure, the stream verification succeeds. A stream head anchored before erasure still matches its stored hash, and its Merkle inclusion proof still verifies.
- [x] `GetAtVersionAsync` and `GetAtRevisionAsync` replay events with erased personal data, and a later `AppendEventsAsync` validates against the rebuilt read model.
- [x] Stream verification reports a value that is `null` and has no salt, and that no later erasure event in the stream records, as "erased without an erasure record". This finding does not make the chain broken.
- [x] Erasure fails without persisting anything when:
  - an explicitly requested version does not exist or has no recorded commitments
  - a requested path is recorded, with or without its salt, in none of the selected events
  - a selected event is not sealed
  - protection is disabled
- [x] Erasure is idempotent: values already erased are skipped, and a request whose selected commitments are all erased appends no event and succeeds.
- [x] Erasure updates are not reported as anchor candidates by the change-stream candidate source.
- [x] Automated tests cover the behavior above.

## Technical Details

### Marking and registration

- New internal overload on `MongoEventStoreBuilder<TAggregate>`, illustrative:

  ```csharp
  internal MongoEventStoreBuilder<TAggregate> WithEvent<TEvent>(String? discriminator, Action<EventRegistrationBuilder<TAggregate, TEvent>> configure)
      where TEvent : Event<TAggregate>;

  // new
  internal sealed class EventRegistrationBuilder<TAggregate, TEvent>
  {
      public EventRegistrationBuilder<TAggregate, TEvent> WithPersonalData<TMember>(Expression<Func<TEvent, TMember>> member);
  }
  ```

- Validation runs in `MongoEventStoreSerializationSetup.RegisterClassMaps`, after the event class maps are registered. Resolving a mark looks up the serializers of the traversed member types, which creates and freezes their class maps at registration.
- **Nullability:** `Nullable<T>`, or a reference type that is not annotated as non-nullable (`NullabilityInfoContext`; oblivious counts as nullable).
- **Arrays:** any segment before the last whose type serializes as an array is rejected. The last segment may be an array or a complex object; either is committed and erased as a whole.
- **Element-name resolution:** each segment before the last must be serialized by a document serializer that resolves member element names (`IBsonDocumentSerializer.TryGetMemberSerializationInfo`), for example a class-map serializer. Otherwise, for example with a custom serializer or a primitive, registration fails. A dictionary segment is rejected explicitly, because the driver's dictionary serializer resolves every name as a key rather than a mapped member. An element name containing a dot is rejected, because the path could not address it. An element name starting with `$` is rejected too, because an update path cannot address it, so the value could be committed but never erased.
- **Storage of marks:** the resolved element paths per event type are stored in internal `MongoEventStoreOptions<TAggregate>` state, not in the process-global class maps. Stores configured differently can therefore verify the same events. Duplicate marks of one path are deduplicated. A registered event type inherits the marks of its registered base event types, so a derived event cannot store marked values without committing to them. An inherited mark is skipped when the derived class map does not map its first member, because such a member is not stored; since #183 the event store registers class maps along the class hierarchy, so this only happens for class maps a consumer registers on top of a base class map that skips the declaring class.
- **Without protection:** marks are validated but have no effect.
- Registration tests, both for errors and for resolved element paths such as renamed elements and nested chains, use dedicated aggregate and event types, because class-map registration is process-global and irreversible.
- New types live in the `Chaos.Mongo.EventStore.Integrity` namespace, which the visibility test enumerates. `EventRegistrationBuilder<TAggregate, TEvent>` stays in the root namespace next to `MongoEventStoreBuilder<TAggregate>`, and the visibility test is extended to cover it and the new `WithEvent` overload.

### Commitment format (draft, may change until #160)

```text
_integrity: { …, PersonalData: [ { Path: "Recipient.Name", Salt: BinData(32), Commitment: BinData(32) }, … ] }
```

- An array instead of a document keyed by path, because dotted keys cannot be addressed by update operators and may be rejected by the driver. Deviation from the issue wording, agreed during planning.
- `PersonalData` is omitted when no path is recorded, so existing documents and test vectors stay valid. In the `EventIntegrity` class map it is the only optional member.
- `Salt` is optional in the entry mapping; its absence means erased. `EventIntegrityChain.TryRead` and typed reads, which deserialize `_integrity` through the `Event<TAggregate>` base class map, accept such entries.
- `Commitment = SHA-256(Salt ‖ element type byte ‖ value bytes)`. The value is taken from the serialized document bytes, not re-encoded. Including the type byte binds the BSON type, so a value whose type is changed while its bytes stay the same is detected. This strengthens the issue text and was agreed during planning.
- A marked path is recorded only when it resolves to an element through embedded documents in the stored document; a stored `null` value is recorded too. A missing path, for example in a legacy document or under a `null` parent, is not recorded and cannot be erased later.
- **Canonical form:** remove `_integrity` (existing `RawBsonElements` helper), then replace every recorded path's element by an element of the same name and position: Binary, subtype `0x00`, holding the 32-byte commitment. The length prefixes of all enclosing documents are corrected. This is a new byte-level operation in `RawBsonElements` that never decodes and re-encodes. It needs focused tests for top-level and nested paths, first, middle and last positions, and changes in value size.
- **Path binding:** the chain hash is `SHA-256(canonical form ‖ binding ‖ previous hash)`, where the binding is present only when paths are recorded: a domain tag, then every recorded path in recorded order, each terminated by a zero byte. Without it, the canonical form would equal a document that genuinely stores a commitment's bytes in place of a value, so a value could be destroyed and the recorded path removed without a verification finding. Events without recorded paths keep the original hash input.
- **Salt reuse on retry:** salts come from `RandomNumberGenerator`. A transaction retry draws new salts. This is harmless, because a failed attempt persists nothing.

### Sealing

- **Append:** `MongoEventStore.SealEventDocumentsAsync` computes the commitments and the canonical form from the serialized bytes of each event document. Its marks come from the event's runtime type, including the marks inherited from its registered base event types.
- **Retroactive:** `EventStreamSealer.SealAsync` resolves the event type from the stored discriminator through the driver's discriminator lookup for `Event<TAggregate>`. Hierarchical discriminators resolve to their concrete type. The sealer applies that type's current marks. An unknown discriminator seals without commitments.

### Verification

- `EventStreamVerifier` builds the canonical form from the recorded paths.
- For each recorded path:
  - **Salt present:** `SHA-256(Salt ‖ type ‖ value)` must equal `Commitment`; otherwise a new `StreamVerificationFailure` member is reported (value does not match its commitment).
  - **Salt absent and value `null`:** the value is erased.
  - **Salt absent and value not `null`:** a second new failure member is reported.

  All three cases belong to Layer 1, so no intermediate layer accepts a changed value whose salt was removed.
- A malformed entry is reported as `MalformedIntegrity`: wrong salt or commitment length, a duplicate path, or a path that does not resolve.
- Layer 2 adds a separate collection of "erased without an erasure record" findings `(Version, Path)` to `StreamVerificationResult`. `IsIntact` keeps describing the chain only. An erasure is documented when a later erasure event in the same stream lists its version and path. The verifier reads erasure events from the raw documents and identifies them by their reserved concrete discriminator (the last element of a hierarchical discriminator).

### Erasure

- **Entry point:** new internal `PersonalDataEraser<TAggregate>` exposing `ErasePersonalDataAsync`, registered in `MongoBuilderExtensions.WithEventStore` only when protection is enabled. It is not added to `IEventStore<TAggregate>`, because an internal member on a public interface would break consumer implementations. #160 decides the public shape.
- **`ErasureRequest` (new, internal):**
  - `Reference`: required, non-whitespace; documented as free of personal data.
  - Optional `Versions` and `Paths` as BSON element paths, where `null` means all.
  - Selected events are the events named by `Versions`, or every event of the stream when `Versions` is `null`. The selection is every commitment recorded in a selected event that matches `Paths` and still has its salt.
  - An explicitly requested version that does not exist or has no recorded commitments, or a requested path that is recorded in none of the selected events, fails the request.
  - A selected event without `_integrity` fails the request, so a `Versions = null` request fails on a stream with an unsealed event rather than skipping it.
- **Result:** `ErasePersonalDataAsync` returns the erased `(Version, Paths)` entries, empty when nothing was left to erase. It does not return the aggregate from the reused append, which holds the state before erasure.
- **Erasure event (new, internal):** `PersonalDataErased<TAggregate> : ObservationalEvent<TAggregate>`, so it changes neither revision nor read model by itself and replay skips it.
  - It records `Reference` and entries `{ Version, Paths }`; `CreatedUtc` is its timestamp.
  - It uses a reserved discriminator and pinned element names, since the verifier reads it raw.
  - Its discriminator is added to `ObservationalEventDiscriminators`, so the server-side replay filter excludes it.
  - Its class map is registered for every aggregate type regardless of protection, so streams containing it still deserialize with protection disabled.
  - A consumer event with the same discriminator fails at registration.
- **Transaction:** erasure appends the erasure event through the existing append path, with the erasure work in its transactional callback. Sealing, concurrency retries and both write paths therefore apply unchanged. The callback:
  - Sets each selected value to `null` with `$set` on its element path; no `$unset`, so positions are kept.
  - Rewrites only the `_integrity.PersonalData` subfield, so the change-stream filter, which matches updates of the `_integrity` element as a whole, ignores the update.
  - Checks that every selected salt is still present. A concurrent erasure surfaces as `MongoConcurrencyException`.
  - The selection is computed from stored `_integrity` data before the append. A selected event without `_integrity` fails with `MongoEventStoreException` before anything is written.
  - A whole-stream request fails with `MongoConcurrencyException` when the stream grew after the selection, so no later event escapes it.
- **Rebuild:**
  - The first affected version is the earliest selected event that holds a value erased by this request or a requested value that is already erased, so values erased outside the eraser are removed from later checkpoints and the read model too. The erasure event still records only the values this request erases.
  - Replays the state-changing events inside the transaction, starting from the newest checkpoint below the first affected version, or from the stream start.
  - In the same pass, it rewrites the `State` of every existing checkpoint at or above the first affected version; `Id` and `Revision` stay unchanged.
  - It then replaces the read model and keeps its `CreatedUtc`.
  - No new checkpoints are created.
  - Checkpoints below the first affected version cannot contain the erased values and stay untouched.
  - A request with nothing left to erase does not rebuild.
- **Known limit:** a long stream without a checkpoint below the first affected version is replayed within one transaction, bounded by `transactionLifetimeLimitSeconds`.
- **Replay contract:** event `Execute` implementations must handle `null` in marked members. An `Execute` that throws aborts the erasure, and nothing is persisted. The rebuilt read model and checkpoints hold only what replaying the events produces; read-model fields written outside `Execute` are reset.

### Out of scope

- Per-element array paths.
- Mapping data subjects to streams, and erasure from backups, outbox messages, external blobs and logs.
- Documentation of erasure permissions and of "mark before enabling protection", which belongs to #160.
- A benchmark: unmarked events are unaffected, and the cost scales with marked fields only.

### Stack Design

1. `0159-tamper-evidence-personal-data-commitments`: marking and registration validation, the commitment format, commitments on append and retroactive sealing, canonical substitution and commitment verification. Depends on trunk.
2. `0159-tamper-evidence-personal-data-erasure`: erasure request and service, erasure event, transactional nulling with read-model and checkpoint rebuild, and erasure-record verification. Depends on the commitments layer.
