# AGENTS.md for Production Code

## Overview of the projects

- Chaos.Mongo provides the core MongoDB connection, dependency-injection, migration, distributed-locking, configuration, and MongoDB-backed queue infrastructure, including subscriptions, lock recovery, retries, retention, and diagnostics.
- Chaos.Mongo.EventStore provides aggregate-based event sourcing, including event persistence, repositories, and checkpoints.
- Chaos.Mongo.Outbox provides a transactional outbox with reliable at-least-once message delivery and background processing.

## Naming of public types

These rules cover public types. Internal types, such as `MongoOutbox` and `MongoEventStore<TAggregate>`, are not covered.

- Types in the core `Chaos.Mongo` package carry the `Mongo` prefix, `IMongo…` for interfaces: `MongoOptions`, `IMongoLock`, `MongoQueueBuilder<TPayload>`.
- Types in the feature packages `Chaos.Mongo.EventStore` and `Chaos.Mongo.Outbox` are named after their feature concept, without the prefix: `IEventStore<TAggregate>`, `Aggregate`, `IOutbox`, `OutboxMessage`.
- Extension classes are named after the type they extend, for example `MongoBuilderExtensions` and `ServiceCollectionExtensions`.
- A few existing types do not follow these rules. They are kept for 1.0 and are not a precedent for new types: `ICollectionTypeMap` in the core package, and `MongoEventStoreBuilder<TAggregate>`, `MongoEventStoreOptions<TAggregate>` and the five `Mongo*Exception` types of `Chaos.Mongo.EventStore.Errors` in the event store.
- Members of `MongoDefaults` are named `<Feature><Setting>` with the feature in the singular, for example `QueueBatchSize`, `LockRetryDelay` and `MigrationLockName`. Switches that apply to the whole library, such as `RunConfiguratorsOnStartup`, need no feature prefix.
