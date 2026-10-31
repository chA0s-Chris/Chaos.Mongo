# Contributing to Chaos.Mongo

Thanks for your interest in contributing. To report a security problem, follow the [Security Policy](SECURITY.md) instead of opening an issue.

## Questions and issues

Use [GitHub Issues](https://github.com/chA0s-Chris/Chaos.Mongo/issues) for bugs, feature requests and questions.

Open an issue before starting anything beyond a small fix, so we can agree on the approach first. Link it from your pull request.

## Setup

You need:

- The .NET SDK selected by [`global.json`](global.json), and the runtimes for the target frameworks in [`Directory.Build.props`](Directory.Build.props) and any directory-specific props files.
- Docker, for the integration tests, which run MongoDB through Testcontainers.

Then restore the local tools:

```bash
dotnet tool restore
```

## Before opening a pull request

Run these commands from the repository root.

Apply the code style before committing, because the script only processes uncommitted files:

```bash
./cleanup_code.sh
```

Then inspect your uncommitted changes:

```bash
./inspect_code.sh
```

To inspect changes you've already committed, pass the commit or branch you started from, for example the upstream `main`:

```bash
./inspect_code.sh --base <revision>
```

Then build and run the tests:

```bash
dotnet build -c Release Chaos.Mongo.slnx
```

```bash
dotnet test Chaos.Mongo.slnx
```

Release builds treat warnings as errors, so the build must have no warnings. Don't use `dotnet format`, because it can contradict the repository's code style.

The merged line coverage must stay at 95% or above. To measure it, first remove earlier results, so they aren't merged in:

```bash
rm -rf artifacts/test-coverage
```

Then run the pipeline, which writes `artifacts/test-coverage/coverage.cobertura.merged.xml`:

```bash
bash ./build.sh Test
```

## Rules

These documents hold the full rules:

- [`AGENTS.md`](AGENTS.md): commands and general rules. AI tools read it too.
- [`CODESTYLE.md`](CODESTYLE.md): code style.
- [`src/AGENTS.md`](src/AGENTS.md): production code.
- [`tests/AGENTS.md`](tests/AGENTS.md): tests.

For pull requests, these matter most:

- Write commit messages in the [Conventional Commits](https://www.conventionalcommits.org/) format.
- Add new public API to the project's `PublicAPI.Unshipped.txt`. Every public API is a compatibility commitment, so keep implementation details non-public.
- Follow the [Compatibility Policy](docs/compatibility-policy.md) for anything that affects the public API, stored data, default names or metrics.
- Update the documentation in [`docs/`](docs/) when behavior changes.

The `ai-plans/` folder is part of the maintainer's workflow. You don't need to write a plan to contribute.

## AI-assisted contributions

AI-assisted contributions are welcome. You remain responsible for understanding, testing and being able to explain what you submit.

## License

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE).
