# Guard the Public API: Public Signing, PublicApiAnalyzers and Package Validation

## Rationale

The 0.x packages are strong-named with `PublicKeyToken=258e5594a5dc8a36`, but only the Nuke `Pack` target signs, using the private key that the release workflow decodes from the `CHAOSMONGO_SNK` secret. Every other build is unsigned. Because the identities differ, `InternalsVisibleTo` is wrapped in `#if !NUGET_RELEASE`: tests run against assemblies with a different identity than the released ones, and the released assemblies carry no `InternalsVisibleTo` at all. Nothing detects accidental public API changes either, which matters now that the trim PRs (#210 to #214) are about to reshape the API before 1.0.

The owner decided that the private key stays secret ([#215](https://github.com/chA0s-Chris/Chaos.Mongo/issues/215)). The repository holds only the public key, ordinary builds use public signing with the same identity, and only the release signs fully and proves it did. The public API is recorded in `PublicAPI.*.txt` files so every API change shows up as a reviewable diff, and package validation runs on every pack. Setting `PackageValidationBaselineVersion` and moving `Unshipped` to `Shipped` are release steps after 1.0 and are out of scope.

## Acceptance Criteria

- [ ] The repository contains a public-key-only key file extracted from the published 0.9.0 assemblies; it contains no private key and its token is `258e5594a5dc8a36`.
- [ ] Every project under `src/`, `tests/` and `benchmarks/` builds with `SignAssembly=true`, `PublicSign=true` and that key file in Debug and Release, so local and CI builds produce assemblies with token `258e5594a5dc8a36`.
- [ ] The Nuke `Pack` target signs fully with the private key and `PublicSign=false`. It fails with a clear message when the private key file is missing or empty, and it never falls back to public signing.
- [ ] `Pack` recompiles the packable projects, so a `Pack` after an ordinary `Build` or `Test` packages freshly signed assemblies rather than stale public-signed outputs.
- [ ] `Pack` verifies every assembly in every `.nupkg` it leaves for publishing before it succeeds: it fails unless the token is `258e5594a5dc8a36` and the strong-name signature is cryptographically valid for the committed public key. Public-signed assemblies and assemblies signed with any other key are rejected, and Nuke `Publish` cannot push a package that failed verification.
- [ ] Every `InternalsVisibleTo` declaration names the friend assembly with the full public key, works in Debug, Release and the release pack, and is present in the packed assemblies.
- [ ] The `NUGET_RELEASE` constant, its `#if` guards and the `AdditionalConstants` plumbing that only existed to pass it are gone.
- [ ] `Chaos.Mongo`, `Chaos.Mongo.EventStore` and `Chaos.Mongo.Outbox` reference `Microsoft.CodeAnalysis.PublicApiAnalyzers` and each has `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt`, starting from the current public API.
- [ ] Adding, removing or changing a public API without a matching `PublicAPI.*.txt` change fails the build in Debug and Release.
- [ ] `EnablePackageValidation=true` is set for the packable projects, package validation runs during `Pack`, and `PackageValidationBaselineVersion` is not set.
- [ ] Each test project contains a test asserting that the product assembly it covers has token `258e5594a5dc8a36`.
- [ ] `dotnet build -c Release Chaos.Mongo.slnx`, the complete test suite and `bash ./build.sh Test` succeed, and merged line coverage stays at or above 95%.

## Technical Details

### Public key file and signing defaults

New file `Chaos.Mongo.Public.snk` in the repository root. It contains exactly the 160-byte public key blob that `AssemblyName.GetPublicKey()` returns for the published `Chaos.Mongo` 0.9.0 assembly; Roslyn accepts this blob as a public-signing key file. The name must stay distinct from `Chaos.Mongo.snk`, which `.gitignore` excludes and which the release workflow writes the private key to. Extract the blob with a throwaway program outside the repository; no extraction tooling is committed.

The root `Directory.Build.props` sets `SignAssembly`, `PublicSign` and `AssemblyOriginatorKeyFile` (an absolute path built from `$(MSBuildThisFileDirectory)`), which covers `src/`, `tests/` and `benchmarks/`. The Nuke project in `build/` deliberately does not import the root props and stays unsigned; it is a build tool, not a shipped or friend assembly.

The SDK passes `/nowarn:8002`, and all compile-time dependencies of the test, benchmark and load-test projects are strong-named anyway, so signing these projects introduces no new warnings.

### Release signing and verification

`BuildPipeline.Pack` (`build/BuildPipeline.NuGet.cs`) currently passes `SignAssembly`, a relative `AssemblyOriginatorKeyFile` and `AdditionalConstants=NUGET_RELEASE`. Afterwards it passes `PublicSign=false` and the absolute path of `Chaos.Mongo.snk` in the repository root as global properties, which override the defaults from the props files. Before packing, it asserts that the file exists and is not empty. Roslyn already rejects a key file without a private key (`CS7032`), so the public key file can never be used silently for a full signature.

MSBuild does not recompile when only signing properties change. A trial build with a different key file and `PublicSign=false` reused the public-signed output from `obj/`. A local `Pack` after `Build` or `Test` could therefore package stale public-signed assemblies. Post-pack verification is the guard that makes this a hard failure. In addition, `Pack` must force a fresh compilation of the packable projects and the project references they compile, so that a correctly keyed local pack succeeds. The mechanism (a rebuild, or clearing their intermediate outputs) is an implementation detail. The release workflow packs from a fresh checkout and is unaffected.

Verification is a method in the Nuke project that `Pack`'s `Executes` body calls synchronously after the last `dotnet pack` and before its summary, so a failure fails `Pack` itself. `Publish` keeps its only dependency, `DependsOn(Pack)`, and therefore never runs after a failed verification; `release.yml` likewise fails before it pushes the tag or publishes. Do not move verification into a target that `Pack` triggers: Nuke runs triggered targets after the triggering target has succeeded, without ordering them against `Publish`. A standalone target may call the same method to verify existing packages, but `Pack` does not trigger it. Verification covers every `.nupkg` in the publish directory, which is the set `Publish` pushes. It opens each package, takes every `lib/**/*.dll`, and checks two things:

- The assembly's public key token equals the hard-coded `258e5594a5dc8a36`, which also rejects a different or swapped committed key.
- The strong-name signature is valid for the committed public key. This contract was confirmed against the published 0.9.0 assemblies, which verify, and a public-signed build, which does not. Compute SHA-1 over the PE headers up to the end of the section table, without alignment padding, followed by each section's raw data. Zero the PE checksum field and the certificate-table data-directory entry, and skip the strong-name signature blob (`CorHeader.StrongNameSignatureDirectory`). The stored signature is byte-reversed; verify it with RSA PKCS#1 v1.5 against the key from the public key blob after its 12-byte header (`ImportCspBlob`). A public-signed assembly has an all-zero signature blob, but the `StrongNameSigned` CLI flag is set either way, so the flag alone proves nothing.

Validation without the real private key:

- The verification accepts the published 0.9.0 packages.
- It rejects public-signed packs.
- It rejects packs signed with a throwaway key pair, written as a CSP blob outside the repository, because the token is wrong.
- After an ordinary `Build`, a `Pack` with the throwaway key packages assemblies whose signature is valid for that key, so verification rejects them only because of the token. This shows that no stale public-signed output was packaged.
- `Pack` fails when `Chaos.Mongo.snk` is absent.
- With a failing package, `Publish` does not reach its push step. Check this with the target graph or a fake feed, never with a real publication.

`release.yml` keeps its existing `Decode SNK` step.

### InternalsVisibleTo

Use the SDK's `InternalsVisibleTo` items in place of the two `Properties/InternalsVisibleTo.cs` files. The existing friends stay the same: `Chaos.Mongo.Tests` for `Chaos.Mongo`, and `Chaos.Mongo.EventStore.Tests`, `Chaos.Mongo.EventStore.Benchmarks` and `Chaos.Mongo.EventStore.LoadTests` for `Chaos.Mongo.EventStore`. Define the public key hex string once in the root props as the `PublicKey` property. `Microsoft.NET.GenerateAssemblyInfo.targets` appends `PublicKey=$(PublicKey)` to every `InternalsVisibleTo` item without `Key` metadata. Friend access compiling in the test projects proves that the hex string matches the key file. Pack verification does not check friend declarations; their presence in the packed assemblies is confirmed by inspecting the metadata of a local pack. `Chaos.Mongo.Outbox` has no friends today and gains none. No `DynamicProxyGenAssembly2` grant is needed, because no test currently mocks an internal type.

### Public API files

Add `Microsoft.CodeAnalysis.PublicApiAnalyzers` centrally in `Directory.Packages.props` and reference it with `PrivateAssets=all` from `src/Directory.Build.props`. Use the latest stable version that the SDK's compiler can load: 5.6.0 at the time of writing, with SDK 10.0.403 shipping Roslyn 5.9. The package picks up `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt` from each project directory automatically. Both files start with `#nullable enable`, because the projects enable nullable reference types. One pair per project suffices: the public API is identical across `net8.0`, `net9.0` and `net10.0`, and the only `#if` (`MongoEventStore.CreateEventId`) is private.

Decision: the current public API goes into `PublicAPI.Shipped.txt`, and `PublicAPI.Unshipped.txt` starts empty. A trial showed that the analyzer applies RS0026 and RS0027 (multiple overloads with optional parameters) only to symbols that are not shipped. With the current API in `Unshipped`, `Chaos.Mongo` alone reports 26 such diagnostics for existing overloads, which fails Release builds. With the API in `Shipped`, the build is clean. A shipped baseline also matches what the analyzer means by "shipped": the 0.x API exists in released packages. The trim PRs therefore show removals as `*REMOVED*` entries and additions as new lines in `Unshipped`. The planned 1.0 release step later folds `Unshipped` into `Shipped`.

Generate the entries rather than typing them by hand. The RS0016 diagnostic message carries the exact entry text. Do not use `dotnet format`, per repository policy. Raise RS0016 (undeclared public API) and RS0017 (declared API not found) to errors in every configuration, for example with `WarningsAsErrors` in `src/Directory.Build.props`, so that a Debug build fails too. The other analyzer rules keep their default severity and become errors in Release through `TreatWarningsAsErrors`. Test, benchmark and load-test projects do not get the analyzer.

### Package validation

Set `EnablePackageValidation=true` in `src/Directory.Build.props`. Without a baseline it validates API compatibility between the target frameworks inside each package. It runs in the SDK's `RunPackageValidation` target during every `dotnet pack`, and therefore in Nuke `Pack`. A trial pack of `Chaos.Mongo.EventStore` passed with no validation diagnostics.

### Tests

Each test project gets one small unit test asserting the token of the product assembly it references (`Chaos.Mongo`, `Chaos.Mongo.EventStore` or `Chaos.Mongo.Outbox`). These tests guard the public-signing defaults for local and CI builds. Release signing is covered by `Pack` verification rather than by tests, because the private key is unavailable outside the release workflow. The benchmark and load-test projects must still build in Release. A BenchmarkDotNet dry run of one benchmark confirms that the generated harness accepts the signed assembly.
