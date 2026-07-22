# Reorganize Documentation and Add Package-Specific READMEs

> Issue: [#107](https://github.com/chA0s-Chris/Chaos.Mongo/issues/107)

## Rationale

The root README currently combines the repository overview with extensive feature documentation, making it difficult to scan and unsuitable as a shared NuGet landing page. Reorganize the documentation so the root presents the project clearly, detailed feature guidance has canonical pages under `docs/`, and each package has a concise README that works both in its source directory and on NuGet.org.

## Acceptance Criteria

- [x] The root `README.md` is a concise repository overview with a package comparison, installation guidance, a minimal quick start, a linked feature summary, and relevant project links.
- [x] The root README clearly communicates that Event Store and Transactional Outbox functionality is provided by separate NuGet packages.
- [x] Detailed guidance removed from the root README is preserved in focused, canonical Markdown pages under `docs/` without duplicate long-form copies.
- [x] `Chaos.Mongo`, `Chaos.Mongo.EventStore`, and `Chaos.Mongo.Outbox` each have a concise `README.md` beside their project file that describes package scope, installation, a minimal example, package relationships, and links to detailed documentation.
- [x] Each package README uses the repository's badge presentation, with its NuGet version and download badges linking to and reporting on that README's actual package; the root README continues to use `Chaos.Mongo` for its NuGet badges.
- [x] Repository navigation and cross-document links are valid on GitHub, while links in package READMEs also resolve correctly when rendered on NuGet.org.
- [x] Each project packages its local `README.md` as the NuGet package README.
- [x] All three projects pack without warnings, and every generated package contains the correct package-specific `README.md` at the package root.

## Technical Details

Treat the root README, package READMEs, and detailed documentation as separate layers. The root README is the repository landing page and should link to the three source-project directories and the canonical feature pages. Files under `docs/` own detailed guidance for getting started, configuration, migrations, configurators, distributed locking, queues, transactions, Event Store, and Transactional Outbox; the exact page boundaries may be adjusted when related material is clearer together.

Place package landing pages at `src/Chaos.Mongo/README.md`, `src/Chaos.Mongo.EventStore/README.md`, and `src/Chaos.Mongo.Outbox/README.md`. Keep examples package-specific and small. Because these files are rendered in both GitHub directory views and NuGet.org, use stable absolute GitHub URLs for links whose relative form would not resolve in both contexts. Avoid copying detailed sections into package READMEs so the files under `docs/` remain authoritative.

Reuse the root README's badge style in the package READMEs, but parameterize both the shields.io NuGet endpoints and their destination links with the owning project package ID: `Chaos.Mongo`, `Chaos.Mongo.EventStore`, or `Chaos.Mongo.Outbox`. Project-wide badges such as license, repository activity, and CI may continue to target the shared repository.

Keep `PackageReadmeFile` set to `README.md` in each package project and replace the existing external README package items with an explicit local item:

```xml

<None Include="README.md" Pack="true" PackagePath="" />
```

The local Markdown files are not included in the evaluated default `None` items for these projects, so an explicit `Include` is required for packing. Verify the package contract by packing all three projects with Release warning behavior and inspecting each `.nupkg`, not solely by reviewing the project files.
