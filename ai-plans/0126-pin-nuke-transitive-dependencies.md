# 0126 — Pin Vulnerable Transitive Dependencies of Nuke

> Issue: [#126](https://github.com/chA0s-Chris/Chaos.Mongo/issues/126)

## Rationale

Every restore of the build project emits nine NuGet audit warnings — one low-severity advisory against `NuGet.Packaging` 6.12.1 and eight high-severity advisories against `System.Security.Cryptography.Xml` 9.0.0. Both packages arrive transitively through `Nuke.Common` 10.1.0, and Nuke is no longer maintained, so the usual remedy of upgrading the direct dependency is unavailable.

Persistent warnings are corrosive: they train everyone reading CI output to skim past the audit block, which is exactly where a genuine advisory against a shipped package would appear. Pinning the two packages to patched versions restores a clean audit so that a future warning means something again.

## Acceptance Criteria

- [ ] `dotnet list build/Nuke.csproj package --vulnerable --include-transitive` reports no vulnerable packages
- [ ] Restoring `Chaos.Mongo.slnx` emits no NU1901 or NU1903 warning
- [ ] Both pinned packages carry their versions in `Directory.Packages.props` and are referenced without a version from `build/Nuke.csproj`, per the repository's central package management rule
- [ ] `build/Nuke.csproj` records in place why two packages it never references in source are declared there, so they are not later removed as dead configuration
- [ ] `bash build.sh Test` passes with an empty warnings block
- [ ] `bash build.sh Pack` produces all three packages, demonstrating that the `NuGet.Packaging` bump does not break Nuke's tooling assembly at pack time
- [ ] No project other than `build/Nuke.csproj` gains a reference to either pinned package, leaving the shipped libraries' dependency sets unchanged
- [ ] The pull request carries `skip-changelog` — satisfied at pull-request creation, not during implementation

## Technical Details

### Origin

The two packages reach the build project through independent chains, so pinning only one leaves the other in place:

```text
Nuke.Common 10.1.0 -> Nuke.Tooling -> NuGet.Packaging 6.12.1                       (low, 1 advisory)
Nuke.Common 10.1.0 -> Nuke.ProjectModel -> Microsoft.Build.Tasks.Core 18.0.2
                                        -> System.Security.Cryptography.Xml 9.0.0  (high, 8 advisories)
```

`System.Security.Cryptography.Xml` is not a dependency of `NuGet.Packaging`; it comes from MSBuild by way of `Nuke.ProjectModel`.

### Pinning mechanism

A direct `PackageReference` outranks a transitive one, so declaring both packages in the build project fixes their resolved versions. `build/Directory.Build.props` deliberately blocks the root `Directory.Build.props` import, but that does not affect `Directory.Packages.props` discovery — `Nuke.Common` and `SemanticVersioning` are already referenced version-less from `build/Nuke.csproj`, so central package management is in force and the versions belong there.

```xml
<!-- Directory.Packages.props -->
<PackageVersion Include="NuGet.Packaging" Version="6.12.5" />
<PackageVersion Include="System.Security.Cryptography.Xml" Version="10.0.11" />
```

The two references in `build/Nuke.csproj` look like dead configuration to anyone who greps the build sources for them, so the reason they exist has to travel with them.

### Version selection

`NuGet.Packaging` is patched at 6.12.5 — a patch bump inside the same 6.12 line Nuke already resolves, and therefore the lowest-risk option available.

`System.Security.Cryptography.Xml` is patched at 9.0.15 and at 10.0.6. The pin is **10.0.11**: it matches the build project's `net10.0` target and the eleven other 10.0.11 pins in `Directory.Packages.props`. Staying on the 9.x line with 9.0.19 would be equally correct and a smaller delta, but consistency with the rest of the file wins here because nothing in the build project constrains the major version.

Renovate needs no special handling. `config:recommended` with the repository's `rangeStrategy: bump` will track both packages like any other dependency, which is the point of declaring them — they stop being invisible transitives. A future `NuGet.Packaging` major could in principle break Nuke's tooling assembly, but `Pack` runs in CI on every pull request and would catch it.

### Rejected alternative

`CentralPackageTransitivePinningEnabled` would achieve the same result without the somewhat fictitious direct references, but it changes CPM resolution for every project in the repository to solve a problem confined to one build project. The blast radius is not worth the tidier build file.

### Verification

This change adds no behavior and therefore no automated tests; restore and build *are* the verification. The audit command and a solution-wide restore prove the warnings are gone, `Test` proves the build pipeline still runs, and `Pack` is included deliberately because a `NuGet.Packaging` API break would surface only there.

The change treats the symptom, not the cause. As long as Nuke stays unmaintained, further advisories against these packages will need manual bumps.
