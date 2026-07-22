# AGENTS.md for Benchmarks

- Use BenchmarkDotNet against the production implementation rather than copied or synthetic helper logic.
- Keep container startup, dependency-injection setup, index creation, and capability detection outside the measured operation, and warm one-time paths before measurement.
- When parameters have constrained combinations, model the valid scenarios explicitly through `ParamsSource`.
- Use unique aggregate IDs or isolated data so duplicate keys and aggregate growth do not skew results.
- Keep benchmark projects single-targeted through `benchmarks/Directory.Build.props`.
- Exclude unrelated transactional callback work when comparing EventStore baseline and bulk-write paths.
