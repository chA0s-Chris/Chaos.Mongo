# AGENTS.md for Production Code

## Overview of the projects

- Chaos.Mongo provides the core MongoDB connection, dependency-injection, migration, distributed-locking, configuration, and MongoDB-backed queue infrastructure, including subscriptions, lock recovery, retries, retention, and diagnostics.
- Chaos.Mongo.EventStore provides aggregate-based event sourcing, including event persistence, repositories, and checkpoints.
- Chaos.Mongo.Outbox provides a transactional outbox with reliable at-least-once message delivery and background processing.
