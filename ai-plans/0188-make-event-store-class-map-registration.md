# Make event store class map registration all-or-nothing

> Issue: [#188](https://github.com/chA0s-Chris/Chaos.Mongo/issues/188)

## Rationale

`MongoEventStoreSerializationSetup.RegisterClassMaps<TAggregate>` registers event class maps one at a time and validates them along the way: the reserved erasure discriminator and the reserved `_integrity` element are checked inside the registration loop, and discriminator uniqueness only after every event class map is registered. Class maps and their discriminators are process-global and cannot be unregistered, so a failed configuration leaves some event class maps behind. A corrected retry in the same process then reuses their old settings, such as a duplicate discriminator, and fails again until the process restarts.

The goal is to build all missing event class maps in memory, validate them together with the already registered class maps, and register them only when every check passes.

## Acceptance Criteria

- [ ] When registration fails because two event class maps share a discriminator, because a configured event type uses the reserved erasure discriminator, either through `WithEvent` or through its pre-registered class map, or because a class map maps the reserved `_integrity` element, no event class map of that call is registered, including intermediate class maps and class maps of event types that would have passed on their own.
- [ ] After such a failure, a retry in the same process with corrected options (a changed discriminator, or without the offending event type) succeeds, and its events round-trip with the discriminators configured for the retry.
- [ ] Discriminator uniqueness is checked across registered and pending class maps together, so a pending class map that collides with a class map of an earlier registration or of the consumer is still rejected.
- [ ] Successful registrations behave as before: same discriminators, same stored members, pre-registered class maps used unchanged, and the existing event store tests still pass.
- [ ] Automated tests cover each failure case above and the corrected retry.

## Technical Details

- **Staged event class maps:** the event type loop and `RegisterClassMapHierarchy` build missing class maps into a pending set keyed by CLR type instead of registering them. An intermediate type shared by several event types is built once, and each pending class map is chained to its base through the `BsonClassMap(classType, baseClassMap)` constructor, using the pending base when there is one. Configured discriminators are still applied while building, so `WithEvent` order does not matter. Once every check passes, pending class maps are registered base types first. The `<exception>` docs of `RegisterClassMaps` state that no event class map of the call is registered when registration fails for one of the listed reasons. This doesn't cover personal data mark errors, which are raised after registration.
- **No side effects before validation:** pending class maps must not be passed to `BsonClassMap.LookupClassMap`, `BsonSerializer.LookupSerializer`, or anything else that freezes them or registers missing base types, because registration adds discriminators to the driver's registry and lookups auto-register missing base types with default settings. Only the already registered root (`Event<TAggregate>`) may be looked up, as today.
- **Validation over registered plus pending class maps:** the reserved discriminator check covers both configured discriminators and pre-registered class maps, as today. `EnsureNoReservedElementCollision` and `EnsureUniqueEventDiscriminators` resolve class maps from the pending set first, then from the registry.
- **Scope of all-or-nothing:** only event class maps (configured event types and their intermediate types) are staged. The aggregate, `Event<TAggregate>`, `EventIntegrity`, `PersonalDataCommitment`, `IntegritySealingState`, `PersonalDataErased<TAggregate>`, and `PersonalDataErasureEntry` class maps keep being registered first, as today. Their settings depend only on the types, not on the event store options, so leaving them registered does not affect a retry. Checks that already fail before any event class map is registered (the observational base guard, `EnsureAggregateMapsRevision`, `EnsureEventBaseMapsIntegrity`, `EnsureEventBaseMapsRevision`) keep their current position.
- **Personal data marks stay after registration (non-goal):** `PersonalDataPathResolver.Resolve` validates marks through `BsonSerializer.LookupSerializer`, so it needs registered, frozen class maps. Marks do not affect class maps, so a retry with corrected marks already succeeds. `ObservationalEventDiscriminators` is likewise derived after registration.
- **Concurrency (non-goal):** concurrent registrations in one process keep today's check-then-register behavior.
- **Tests:** class maps are process-global, so each test uses its own aggregate and event types and a fresh `MongoBuilder`. Registration state can be asserted with `BsonClassMap.IsClassMapRegistered`, and a corrected retry by round-tripping an event with the discriminators configured for the retry. The existing `WithEventStore_IntermediateRegisteredByEventStoreMapsReservedElement_ThrowsWithoutRegisteringIt` test stays valid; the new tests add the case where other event types in the same call would have passed.
