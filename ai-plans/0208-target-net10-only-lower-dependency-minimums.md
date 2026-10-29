# Target net10.0 Only and Lower the Dependency Minimums

## Rationale

All three packages target net10.0, net9.0 and net8.0. .NET 8 and .NET 9 both reach end of support on 2026-11-10, and dropping a target framework after 1.0 would need a major release, so 1.0 ships for net10.0 only ([#208](https://github.com/chA0s-Chris/Chaos.Mongo/issues/208)). net11.0 is added later in #209, which is not breaking.

With central package management, the versions in `Directory.Packages.props` become the minimum dependency versions in every published nuspec. Today they force every consumer onto the latest patches (10.0.14 for the Microsoft packages, MongoDB.Driver 3.13.0), and Renovate's `rangeStrategy: bump` raises them with every release. The goal is to declare the lowest versions that are free of known vulnerabilities and provide the APIs the libraries use, and to keep Renovate from raising them except to fix a vulnerability.

## Acceptance Criteria

- [ ] `MainTargets` is `$(LatestTarget)` (`net10.0`). Every `src/` and test project builds only for `net10.0`, and `src/`, `tests/` and the MSBuild files contain no `NET*_OR_GREATER` branches and no `TargetFramework` conditions.
- [ ] Events appended without an `Id` receive a version 7 GUID on both the default and the bulk-write append path. Explicitly supplied IDs are kept unchanged on both paths. Automated tests cover both behaviours on both paths.
- [ ] The `src/` dependencies in `Directory.Packages.props` are at the minimum versions listed in Technical Details. `Directory.Packages.props` documents, for each of them, the API, dependency constraint or advisory that sets its version, for example in one comment block above the `src/` minimums.
- [ ] `Chaos.Mongo` references `Microsoft.Extensions.Configuration.Abstractions` instead of `Microsoft.Extensions.Configuration`.
- [ ] For each of the three packages, `dotnet list package --vulnerable --include-transitive` reports no vulnerable packages. The nuspec from a Release `dotnet pack` has a single `net10.0` dependency group with exactly these minimum versions and still includes the SourceLink `repository` commit.
- [ ] `renovate.json` makes Renovate hold regular updates of the `src/` dependencies for Dependency Dashboard approval instead of opening PRs. Vulnerability-fix PRs for them are still raised. Test-only and build-only packages update as before.
- [ ] Every `actions/setup-dotnet` step in `ci.yml` and `release.yml` installs only `10.0.x`.
- [ ] `dotnet build -c Release Chaos.Mongo.slnx` reports no warnings. The complete test suite, including integration tests, passes, and `bash ./build.sh Test` meets the 95% merged line coverage required by `tests/AGENTS.md`.
- [ ] `README.md`, `docs/event-store.md` and the package READMEs describe net10.0-only support consistently, with no remaining .NET 8 or .NET 9 compatibility notes. Automatic event-ID generation is documented as GUID version 7 for both append paths, and the examples with explicitly supplied IDs remain valid.

## Technical Details

### Target framework

`Directory.Build.props` sets `<MainTargets>$(LatestTarget)</MainTargets>`. The plural `TargetFrameworks` properties stay, so #209 only needs to extend `MainTargets`. `MongoEventStore.CreateEventId` becomes `Guid.CreateVersion7()` without the `#if`. Its XML comment no longer refers to the target framework. The method is private, so the `PublicAPI.*.txt` files do not change. Package validation has no baseline version, so dropping the two frameworks does not trip it. The `breaking` label on the issue covers the release notes.

The test projects for EventStore and Outbox lose both `TargetFramework` conditions. The `BooksApi`/`OrdersApi` sources and their integration tests are compiled unconditionally, and the ASP.NET Core test packages become plain references.

`EventStoreGeneratedIdTests` already runs with and without bulk-write optimization. Its generated-ID test additionally asserts `Guid.Version == 7`, and the explicit-ID test stays as it is.

### Dependency minimums

The rule from the issue is the lowest version that has no known vulnerability and provides every API used. Some constraints come from the dependency graph rather than from our own API usage, and the table records them:

| Package (`src/`) | Minimum | Determined by |
| --- | --- | --- |
| Microsoft.Extensions.DependencyInjection | 8.0.0 | Keyed services (`AddKeyedSingleton`, `GetRequiredKeyedService`) |
| Microsoft.Extensions.Hosting.Abstractions | 8.0.0 | `IHostedLifecycleService` |
| Microsoft.Extensions.Options | 8.0.0 | `OptionsBuilder.ValidateOnStart` in the Options package |
| Microsoft.Extensions.Logging.Abstractions | 8.0.0 | Hosting.Abstractions 8.0.0 depends on it at ≥ 8.0.0, so a lower direct version is an NU1605 downgrade |
| Microsoft.Extensions.Configuration.Abstractions *(new reference)* | 8.0.0 | Same as Logging.Abstractions; provides `IConfiguration` |
| Microsoft.Extensions.Options.ConfigurationExtensions | 2.1.0 | `OptionsBuilder.Bind`, first shipped in 2.1.0 (2.0.x has no `OptionsBuilder` extensions) |
| Microsoft.Extensions.Configuration.Binder | 2.1.0 | Options.ConfigurationExtensions 2.1.0 depends on it at ≥ 2.1.0, so a lower direct version is an NU1605 downgrade |
| System.IO.Hashing | 8.0.0 | `XxHash3` |
| System.Security.Cryptography.Pkcs | 8.0.2 | GHSA-846f-f764-76mx and GHSA-2fmv-rv2c-59x7 affect 8.0.0–8.0.1 |
| MongoDB.Driver | 3.9.0 | Older 3.x releases pull in Snappier ≤ 1.3.0 (GHSA-pggp-6c3x-2xmx, < 3.8.1) or SharpCompress 0.30.1 (GHSA-6c8g-7p36-r338, < 3.9.0). `ClientBulkWriteException` alone would allow 3.1.0 |

Two decisions are not obvious from the rule:

- **The Microsoft.Extensions floors span two release lines.** Options.ConfigurationExtensions 2.1.0 and Binder 2.1.0 target netstandard2.0. Their own dependencies (Options, Configuration.Abstractions, DependencyInjection.Abstractions ≥ 2.1.0) resolve to the 8.0.0 floors above without a downgrade. Binder 2.1.0 still depends on `Microsoft.Extensions.Configuration` 2.1.0, so consumers get that package transitively. Only Binder 5.0.0 and later depend on `.Abstractions` alone. No published advisory affects any version of Options.ConfigurationExtensions, Binder or `Microsoft.Extensions.Configuration`. A release-line preference is not a reason for a higher floor.
- **The Pkcs floor is 8.0.2, not 6.0.x/7.0.x.** The 6.0 and 7.0 lines show no advisory only because they were out of support when GHSA-846f and GHSA-2fmv were published, and they never received a fix. Only lines that were still serviced when the newest advisory appeared count as free of known vulnerabilities.

These versions were checked in a scratch copy on SDK 10.0.403, outside this worktree. With them, the Release solution build had no warnings, `dotnet list package --vulnerable --include-transitive` was clean for all three projects, and the test projects resolved MongoDB.Driver 3.9.0, Pkcs 8.0.2 and System.IO.Hashing 8.0.0, so the suite runs against those floors. `Chaos.Mongo.Tests`, which holds the configuration-binding tests, also resolves Options.ConfigurationExtensions and Binder at 2.1.0. The EventStore and Outbox test projects resolve them at 10.0.14 through their test-only hosting packages. The complete suite passed there with 1,637 tests. NuGet audit runs transitively on net10.0, and `TreatWarningsAsErrors` turns NU190x into errors, so the Release build also guards against a vulnerable floor. The documentation in `Directory.Packages.props` also states that held minimums are reviewed by hand when an advisory affects a newer release line, because advisories stop listing a line once it is out of support.

Item 6 of the issue resolved: `Chaos.Mongo` uses only `IConfiguration` and `OptionsBuilder.Bind`, so it does not need a direct `Microsoft.Extensions.Configuration` reference. It references `.Abstractions` and keeps `.Binder` and `.Options.ConfigurationExtensions`. The transitive `Microsoft.Extensions.Configuration` 2.1.0 that Binder 2.1.0 brings in is not a declared dependency of the package. `Chaos.Mongo.Tests` uses `ConfigurationBuilder`/`AddInMemoryCollection` and gains a direct `Microsoft.Extensions.Configuration` reference. That package stays in `Directory.Packages.props` as a test-only dependency at its current version.

**The `Microsoft.SourceLink.GitHub` package reference is removed** from `src/Directory.Build.props` and `Directory.Packages.props`. Version 10.0.403 declares a dependency on `System.IO.Hashing >= 10.0.14`. That dependency does not flow to consumers, but it does take part in restore, so lowering System.IO.Hashing fails with an NU1605 downgrade error. The .NET 8+ SDK bundles the same SourceLink provider. Without the package, the packed nuspec still carries the repository URL and commit, and the PDBs still get SourceLink. The comment in `src/Directory.Build.props` that mentions SourceLink must stay accurate. Suppressing NU1605 or keeping System.IO.Hashing at 10.0.14 were rejected: the first hides real downgrades, and the second defeats the goal.

### Renovate

A new `packageRules` entry, placed after the existing NuGet `rangeStrategy` rule, lists exactly the `src/` dependencies from the table above and sets `dependencyDashboardApproval: true` and a dedicated `groupName`. Renovate's built-in `vulnerabilityAlerts` config sets `dependencyDashboardApproval: false` and `groupName: null`. It is applied to vulnerability fixes, so those fixes still open PRs at once, and with the lowest fixed version. `enabled: false`, the issue's example, is not used: according to Renovate's documentation it suppresses vulnerability-fix PRs as well. The dedicated group keeps the held packages out of the `dotnet` monorepo group that `config:recommended` applies through `group:monorepos`. That group covers dotnet/runtime and dotnet/aspnetcore. Without the dedicated group, the held packages could ride along in a PR for test-only packages such as `Microsoft.Extensions.Hosting` or `Microsoft.AspNetCore.Mvc.Testing`. Packages that `src/` shares with tests or benchmarks, for example `Microsoft.Extensions.DependencyInjection`, are held as well. Validate `renovate.json` with `renovate-config-validator` if it is available, and say so in the handoff if it is not.

### Workflows and documentation

The two multi-version `setup-dotnet` steps in `ci.yml` and `release.yml` collapse to `dotnet-version: 10.0.x`. The step in the `publish` job already uses only `10.0.x`.

`README.md` states that the packages target .NET 10. In `docs/event-store.md`, the GUID note drops its ".NET 9+" qualifier and the ".NET 8 or earlier" advice. The generated-ID note states version 7 for both append paths. The `X509CertificateLoader` sample loses its .NET 8 comment. The package READMEs contain no framework statements. They are only checked for consistency. The root `AGENTS.md` is not edited: its "Here is Your Space" section asks for observations to be reported rather than written into the file. Its statement that the test projects target multiple frameworks no longer holds, so the implementation handoff and the PR description report it for the owner to decide. `CHANGELOG.md` is generated at release time and is not edited.
