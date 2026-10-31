# Security Policy

## Reporting a vulnerability

Please report vulnerabilities through [GitHub's private vulnerability reporting](https://github.com/chA0s-Chris/Chaos.Mongo/security/advisories/new). Do not open a public issue.

Include the affected package and version, steps to reproduce the problem, and what an attacker could achieve.

## Supported versions

| Version | Supported |
| --- | --- |
| Latest 1.x minor release, once 1.0 is released | Yes |
| Latest prerelease, until 1.0 is released | Yes |
| Anything older | No |

Fixes ship in a new release and are not backported to older versions.

## Scope

This policy covers:

- The `Chaos.Mongo`, `Chaos.Mongo.EventStore` and `Chaos.Mongo.Outbox` packages.
- The [tamper-evidence format](docs/tamper-evidence-specification.md) and the [reference verifier](tools/tamper-evidence-verifier/verify.py).

Beyond the [limitations documented for tamper evidence](docs/event-store.md#threat-model-guarantees-and-limitations), anything that makes verification report altered, removed or backdated data as intact is a vulnerability, even if it requires write access to the database. Tamper evidence is meant to reveal changes made with that access.

This policy does not cover:

- Vulnerabilities in MongoDB or the MongoDB driver. Please report those to their maintainers.
- Insecure configuration of an application or database, unless the documentation recommends it.

## What happens next

Responses are best effort. You can normally expect a first reply within 7 days.

Once a report is confirmed, the fix is developed privately and published together with a security advisory. Reporters are credited in the advisory unless they prefer otherwise.

Format v1 of the tamper-evidence format stays verifiable by every future version (see the [Compatibility Policy](docs/compatibility-policy.md)), so a flaw in the format itself may need a new format version rather than a patch. Advisories state whether evidence that is already stored is affected.
