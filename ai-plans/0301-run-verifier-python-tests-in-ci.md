# Run the reference verifier's Python tests in CI

> Issue: [#301](https://github.com/chA0s-Chris/Chaos.Mongo/issues/301)

## Rationale

`tools/tamper-evidence-verifier/test_trusted_chain.py` unit-tests the chain-trust validation of `verify.py`, and for several rules it is the only test of the reference verifier: a stored root with a corrupted self-signature, a counterfeit issuer group embedded in the token, the bound of the trust-path search, and RSA PKCS #1 v1.5 links with SHA-384 and SHA-512. Nothing runs it. CI only runs the NUKE pipeline, which reaches `verify.py` through the vector replay and `ReferenceVerifierIntegrationTests`, and the test's one third-party dependency, `cryptography`, is unpinned.

Independent implementations of format v1 are compared against `verify.py` once the format freezes at 1.0, so a regression there should fail a build. This lands before the rc.2 tag of #254.

## Acceptance Criteria

- [x] A new job in `.github/workflows/ci.yml` runs `test_trusted_chain.py` with `python3 -m unittest` on every pull request to `main` and every push to `main`, outside the `ci` matrix and without .NET, Docker or MongoDB.
- [x] The job is in the `needs` of the `result` job, so a failing or cancelled run fails `result`.
- [x] A new `tools/tamper-evidence-verifier/requirements-test.txt` pins `cryptography` and every package pip installs with it to exact versions with hashes, and the job installs it with `--require-hashes`.
- [x] `renovate.json` has a rule that puts every update of the packages in that file into one pull request, and `renovate-config-validator` accepts the configuration.
- [x] All tests in the file pass against the current `verify.py` with the pinned versions installed through `--require-hashes`.
- [x] The verifier's README says how to install the dependency from the requirements file and that CI runs the tests.

## Technical Details

### CI job

The job follows the existing jobs in `ci.yml`: `ubuntu-latest`, a timeout, `permissions: contents: read`, and the same digest-pinned `actions/checkout` with `persist-credentials: false`. It has no path filter, because `result` is the required check and the job is fast. The existing `workflow_call` trigger means `release.yml` runs it too.

Python comes from `actions/setup-python`, pinned by digest like every other action (`helpers:pinGitHubActionDigests`), with one explicit version: 3.12, the version the tests were verified on. The runner's system Python is not used; it is externally managed and changes with the runner image.

The tests shell out to `openssl` through `verify.py`, so the job relies on the runner's preinstalled OpenSSL 3.x. No install step is needed.

### Dependency pin

`requirements-test.txt` is hand-kept and lists the full closure pip installs for `cryptography`, currently `cryptography`, `cffi` and `pycparser`, because `--require-hashes` rejects any unpinned transitive package. A later `cryptography` release that adds a transitive dependency needs a manual line; the job then fails on the Renovate pull request, which is the intended signal. The file also restricts pip to wheels with `--only-binary :all:`.

The file name marks the dependency as test-only. `verify.py` stays standard library only, and the README's Requirements section must keep saying so.

### Renovate

Renovate's default `pip_requirements` file pattern matches `requirements-test.txt`, and the `ignorePaths` of the shared `github>chA0s-Chris/repo-baseline//renovate/default` preset do not exclude `tools/`. No manager configuration is needed.

A new `packageRules` entry in `renovate.json` groups the packages of this file and sets `separateMajorMinor: false`, so every update of `cryptography` and its dependencies arrives in one pull request. Without that option Renovate splits major and non-major updates even inside a group. The rule also sets `dependencyDashboardApproval: false`: the shared preset holds major updates for Dependency Dashboard approval, and `cryptography` raises its major version routinely, which would otherwise hold almost every update of a test-only dependency.

Renovate refreshes hashes itself: its `pip_requirements` manager runs `hashin` for every updated line that carries `--hash=`.

One thing can only be confirmed after merge: that the Dependency Dashboard lists the three packages and the first Renovate pull request installs cleanly. If that pull request fails because hashes were not refreshed, the fallback is a `requirements-test.in` compiled with `pip-compile --generate-hashes` and Renovate's `pip-compile` manager. That would be a follow-up, not part of this plan.

### Tests

All 19 tests pass locally against the current `verify.py` on Python 3.12 with `cryptography` 50.0.2, `cffi` 2.1.1, `pycparser` 3.12 and OpenSSL 3.0.13, so no test fixes are expected. The criterion is checked locally with the job's own commands; the CI job confirms it on the pull request. If the pinned versions break a test, the fix goes into the test. A failure that shows a real defect in `verify.py` is reported to the maintainer rather than worked around.

### Documentation

The README's Testing section replaces the `pip install cryptography` hint with the install from `requirements-test.txt` using `--require-hashes`, and states that CI runs the tests. The module docstring of `test_trusted_chain.py` carries the same hint and is updated to match.

### Non-goals

- Running the vector replay in this job; `dotnet test` already covers it.
- Verifying the README's "Python 3.8 or later" claim.
- Adding shared vectors to `spec/tamper-evidence/v1` for the cases only this file covers.
