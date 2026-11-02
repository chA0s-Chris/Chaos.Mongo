# Compatibility Policy

This page states the compatibility guarantees Chaos.Mongo 1.x makes. If you are upgrading from 0.x, see
[Migrating from 0.x](migrating-from-0.x.md); for supported platforms and MongoDB server versions, see
[Requirements](requirements.md).

## Public API

Chaos.Mongo follows [Semantic Versioning](https://semver.org/) for source and binary compatibility of its public
API: a breaking change to a public type or member ships only in a major release.

- A public interface only grows through members that carry a default implementation; adding a member without one is
  a breaking change.
- A public enum used as a result type may gain new values in a minor release. Consumers must not assume its value
  set is closed and should handle unrecognized values, for example with a default `switch` arm.
- Adding a `required` member to an existing public type is a breaking change, because it breaks every existing
  object initializer that does not set it.

## Stored data

- Every 1.x release reads data written by every earlier 1.x release.
- Adjacent minor versions can run side by side during a rolling upgrade, as long as no feature introduced in the
  newer version is used yet.
- Default collection names, index names, lock names, persisted enum values (names or numbers, depending on their
  representation) and metric names, tags and units are contract: changing one is a breaking change.
- The lock document's per-acquisition token is an internal implementation detail, not part of the documented
  contract. A document written without it, by an earlier release or by an older instance taking over an expired
  lock, keeps expiring and being reacquired normally. This guarantee assumes no instance sharing a configured
  `HolderId` runs an older, pre-token release concurrently against the same lock; see
  [Migrating from 0.x](migrating-from-0.x.md#distributed-locking-per-acquisition-token) for the rolling-upgrade
  boundary this implies.

## Tamper-evidence format

The tamper-evidence format (see [Tamper Evidence](event-store.md#tamper-evidence) and its
[specification](tamper-evidence-specification.md)) is versioned separately from the packages:

- Format v1 stays verifiable by every future package version, including future majors.
- A new format version arrives at most as opt-in in a minor release. Writing it by default requires a major
  release.

## Platform

- .NET versions and MongoDB server versions are dropped in a minor release when their vendor ends support.
- Dependency minimums are raised in a minor release only when needed.

## Not covered

This policy does not cover: internal types, the text of log and exception messages, performance characteristics, and
undocumented behavior. These can change in any release.
