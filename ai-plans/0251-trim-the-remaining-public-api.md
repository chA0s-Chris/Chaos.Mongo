# Trim the remaining public API before the freeze

> Issue: [#251](https://github.com/chA0s-Chris/Chaos.Mongo/issues/251)

## Rationale

#210 and the trims that followed reduced the public surface to its 1.0 contract, but review found a handful of members that are still public without a consumer use, are named inconsistently, or expose mutable collections, plus one unused generic constraint and one missing validation. After 1.0 every one of them is covered by SemVer, so removing, renaming or relaxing them later is a breaking change.

This plan resolves them in one pass across `Chaos.Mongo`, `Chaos.Mongo.EventStore` and `Chaos.Mongo.Outbox`, and records the rule for the `Mongo` type prefix so that new types are named consistently.

## Acceptance Criteria

### Chaos.Mongo

- [ ] `IMongoQueueSubscription<TPayload>` is internal, and `docs/queues.md` no longer describes it as public.
- [ ] `MongoQueueDefinition` has no `PayloadHandlerType` member, and `docs/queues.md` no longer lists it.
- [ ] `MongoDefaults` exposes `QueueAutoStartSubscription`, `LockRetryDelay` and `MigrationLockName` in place of `AutoStartSubscription`, `RetryDelay` and `MigrationsLockName`.
- [ ] `MongoOptions.MigrationsLockName` is renamed to `MigrationLockName`. The default value `"ChaosMongoMigrations"` is unchanged.
- [ ] Queue and outbox payload type parameters are constrained to `class` only, without `new()`, in every declaration.
- [ ] `docs/queues.md` and `docs/transactional-outbox.md` no longer show `new()` on queue and outbox payload signatures.
- [ ] Automated tests show that a positional record works as a queue payload: it is published, stored, and delivered to its handler with an equal value.
- [ ] Automated tests show that a positional record works as an outbox payload: it is added, stored, and `OutboxMessage.DeserializePayload<TPayload>()` returns an equal value.

### Chaos.Mongo.EventStore

- [ ] `MongoEventStoreOptions<TAggregate>` has no public constructor.
- [ ] `PersonalDataErased<TAggregate>.Entries` and `PersonalDataErasureEntry.Paths` are `IReadOnlyList<>` properties without a public setter.
- [ ] The stored format of erasure records is unchanged: the existing tests for pinned element names, extra-element tolerance and verification pass, and an automated test deserializes a raw erasure record in the v1 shape into both types.

### Chaos.Mongo.Outbox

- [ ] `OutboxMessage` has no public constructor.
- [ ] A new public static `OutboxMessage.Create(...)` builds a message for tests of an `IOutboxPublisher`. Automated tests cover the values it sets, its argument validation, and that its payload deserializes, and `docs/testing.md` shows unit-testing a publisher with it.
- [ ] `IOutboxConfiguratorRunner.RunConfiguratorsAsync` replaces `RunAsync`, and `docs/transactional-outbox.md` and the package README use the new name.
- [ ] Registering two payload types with the same discriminator in one outbox makes `WithOutbox` throw an `InvalidOperationException` that names the discriminator and both payload types, covered by automated tests.
- [ ] The same discriminator registered in two different outboxes is still accepted, covered by an automated test.

### Cross-cutting

- [ ] `src/AGENTS.md` records the `Mongo` prefix rule for public types and its listed exceptions.
- [ ] Each project's `PublicAPI.Unshipped.txt` shows exactly these changes.
- [ ] `docs/migrating-from-0.x.md` lists every change.
- [ ] The Release build has no warnings and the full test suite passes.

## Technical Details

### Verified during planning

A throwaway check against the pinned MongoDB.Driver 3.9.0 showed:

- Positional records round-trip through both payload paths, also with unknown extra elements. The outbox path is `new BsonClassMap(type)` with `AutoMap()`, then `ToBsonDocument` and `Deserialize`. The queue path is a `required init` member of an auto-mapped generic item.
- A type with a non-public parameterless constructor and non-public setters deserializes with `AutoMap()` alone, including an `IReadOnlyList<T>` member.

The constraint removal is therefore unconditional. The tests in the acceptance criteria are the lasting proof.

### Queues

- Nothing instantiates a payload: `MongoQueueItem<TPayload>.Payload` is `required init`. The `new()` constraint appears on the queue types in `src/Chaos.Mongo/Queues`, on `MongoBuilder`'s queue registration and on `MongoSerializationSetup.RegisterQueueItemClassMap<TPayload>()`. The aggregate constraint `TAggregate : class, IAggregate, new()` in the event store is used and out of scope.
- Dropping `new()` breaks consumers that implement a generic interface method with the old constraint, in particular `IMongoQueuePayloadPrioritizer.CreateSortDefinition<TPayload>()`. Their implementation must drop `new()` too. Generic consumer code that only calls the API keeps compiling. The migration guide must say both.
- `PayloadHandlerType` is removed instead of made accurate. A handler registered through the factory overload `MongoQueueBuilder<TPayload>.WithPayloadHandler(Func<IServiceProvider, IMongoQueuePayloadHandler<TPayload>>)` has no statically known type, so the member could never be reliable. `MongoQueueSubscription<TPayload>` already logs the runtime handler type.
- `IMongoQueueSubscription<TPayload>` is mocked with Moq in the queue tests. `Chaos.Mongo.csproj` already grants `InternalsVisibleTo` to `DynamicProxyGenAssembly2`, so those tests keep working.

### MongoDefaults and MongoOptions

- `MongoDefaults` members follow `<Feature><Setting>` with the feature in the singular: `Queue…`, `Lock…`, `Migration…`. The global switches (`ApplyMigrationsOnStartup`, `RunConfiguratorsOnStartup`, `UseDefaultCollectionNames`, `UseTransactionsForMigrationsIfAvailable`) keep their names.
- `MongoOptions` binds from configuration, so renaming `MigrationsLockName` also renames the configuration key. The migration guide and `docs/configuration.md` must cover the key, not only the property.
- The lock name value is persisted as the lock document's id and stays `"ChaosMongoMigrations"`.

### Event store

- `MongoEventStoreOptions<TAggregate>` stays a public type. `MongoEventStoreBuilder<TAggregate>` creates the instance, and the tests construct it through `InternalsVisibleTo`.
- `AutoMap()` maps properties whose setter is non-public. The class maps that `MongoEventStoreSerializationSetup` registers for both erasure types, and a class map a consumer registered earlier, therefore keep mapping `Entries` and `Paths`. `ErasureEntryClassMapMatches` and the matching check for `PersonalDataErased<TAggregate>` need no change.
- Format v1 is frozen. Element names and BSON types of `Entries` and `Paths` must not change.
- The constructors and the `Reference` and `Version` setters of the two erasure types stay public. The issue names only the two list members. Update the remarks that describe the list setters as public for deserialization.

### Outbox

- Exact signature of the new factory:

  ```csharp
  public static OutboxMessage Create(String type,
                                     BsonDocument payload,
                                     String? correlationId = null,
                                     ObjectId? id = null,
                                     DateTime? createdUtc = null,
                                     Int64 attemptCount = 1)
  ```

  An omitted `id` is generated and an omitted `createdUtc` is the current UTC time. `attemptCount` defaults to `1` because that is what a publisher sees on a message's first claim. Lock and state members keep their defaults. It throws `ArgumentException` for a null or whitespace `type`, `ArgumentNullException` for a null `payload`, `ArgumentOutOfRangeException` for an `attemptCount` below 1, and `ArgumentException` for a `createdUtc` that is not UTC. The factory exists for consumer tests; `MongoOutbox` keeps creating messages itself.
- `RunConfiguratorsAsync` matches `IMongoConfiguratorRunner.RunConfiguratorsAsync` and `IMongoMigrationRunner.RunMigrationsAsync`.
- Duplicate discriminators are detected in `OutboxBuilder.Validate()`, which runs inside `WithOutbox`, so the application fails while composing its services. The comparison is ordinal and scoped to one outbox, because every outbox has its own collection. Registering the same payload type again with another discriminator still replaces the earlier registration.

### Naming rule

The rule goes into `src/AGENTS.md` instead of `CODESTYLE.md`, which the issue names. `CODESTYLE.md` is managed by repo-baseline and must not be edited in this repository. The rule to record:

- The rule covers public types. Internal types, such as `MongoOutbox` and `MongoEventStore<TAggregate>`, are not covered.
- Types in the core `Chaos.Mongo` package carry the `Mongo` prefix, `IMongo…` for interfaces.
- Types in the feature packages `Chaos.Mongo.EventStore` and `Chaos.Mongo.Outbox` are named after their feature concept, without the prefix.
- Extension classes are named after the type they extend, for example `MongoBuilderExtensions` and `ServiceCollectionExtensions`.
- Existing exceptions are kept for 1.0 and are not a precedent: `ICollectionTypeMap`, `MongoEventStoreBuilder<TAggregate>`, `MongoEventStoreOptions<TAggregate>` and the five `Mongo*Exception` types in `Chaos.Mongo.EventStore.Errors`.
- `MongoDefaults` members follow `<Feature><Setting>` as described above.

### Public API baselines

Members listed in `PublicAPI.Shipped.txt` get a `*REMOVED*` entry in `PublicAPI.Unshipped.txt`. Members that exist only in `PublicAPI.Unshipped.txt`, such as `MongoDefaults.AutoStartSubscription.get`, are edited there directly.
