# Tamper Evidence Verifier

A standalone Python reference implementation of
[`docs/tamper-evidence-specification.md`](../../docs/tamper-evidence-specification.md), the byte-level format behind
Chaos.Mongo.EventStore's [tamper evidence](../../docs/event-store.md#tamper-evidence) feature. It reads raw BSON
exported with `mongodump`, recomputes the hash chains and Merkle trees itself using only the Python standard library,
and delegates verification of RFC 3161 timestamp tokens and CRL signatures to OpenSSL. Its purpose is to prove that
the specification is sufficient on its own: an independent third party can verify the evidence without Chaos.Mongo,
.NET or any proprietary tooling.

This tool is not part of the Chaos.Mongo library and ships no NuGet package; it is a reference verifier maintained
alongside the specification it implements.

## Requirements

- Python 3.8 or later (standard library only; no third-party packages are installed or imported).
- OpenSSL 1.1.1 or later available on `PATH` as `openssl`, for `openssl ts -verify` and `openssl crl` (developed and
  tested against OpenSSL 3.0).

## Usage

```bash
# Verify a stream and, optionally, its anchors from mongodump exports:
python3 verify.py check --events Orders_Events.bson --aggregate-type Order --aggregate-id <uuid> \
    [--anchors IntegrityAnchors.bson --leaves IntegrityAnchorLeaves.bson --evidence IntegrityAnchorEvidence.bson \
     [--evidence-chunks IntegrityAnchorEvidenceChunks.bson] \
     --trusted-root [NAME=]PEM [--policy NAME=OID ...] [--grace-hours HOURS] [--trusted-head SEQUENCE:DIGESTHEX]] \
    [--element-name NAME=STORED ...]

# Run the bundled test vectors against their expected values:
python3 verify.py vectors DIRECTORY
```

Both commands print a JSON report to stdout and exit with `0` when everything verifies, `1` when a check fails, and
`2` on a usage error or an unreadable input file.

See [Verifying Independently](../../docs/tamper-evidence-specification.md#verifying-independently) in the
specification for the full option reference — including `--aggregate-type`, `--element-name`, trusted roots and
`--trusted-head` — and for the exact `mongodump` invocations that produce the input files.

## Related documentation

- [Tamper Evidence Specification](../../docs/tamper-evidence-specification.md)
- [Event Store: Tamper Evidence](../../docs/event-store.md#tamper-evidence)
