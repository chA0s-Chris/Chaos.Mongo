# build.sh fails before any target runs inside a git worktree

> Issue: [#321](https://github.com/chA0s-Chris/Chaos.Mongo/issues/321)

## Rationale

`build.sh` fails in a linked git worktree outside the clone's directory tree before any target runs, so neither the test pipeline nor the merged coverage measurement required by `tests/AGENTS.md` can be used there. Nuke looks for a `.git` directory and does not resolve a worktree's `.git` file. It finds none there, leaves the `[GitRepository]` property `null`, and `OnBuildInitialized` dereferences it for two log lines. The build does not need that information, so the property goes away.

## Acceptance Criteria

- [ ] `bash ./build.sh Test` runs to completion in a linked git worktree outside the clone's directory tree and writes the merged coverage file.
- [ ] The build prints no `Could not inject value for BuildPipeline.GitRepository` warning in such a worktree.
- [ ] `bash ./build.sh Test` and `bash ./build.sh PackCheck` succeed in an ordinary clone whose `origin` is the GitHub repository, and the only change to their behavior is that the `Repository` and `Branch` log lines are gone.

## Technical Details

Remove the `GitRepository` property and its `using Nuke.Common.Git;` from `build/BuildPipeline.Common.cs`, and the two log lines that read it from `OnBuildInitialized` in `build/BuildPipeline.cs`. Nothing else reads the property, and no target, version or package depends on it. Package metadata takes the repository URL from `src/Directory.Build.props` and the commit from the SDK's SourceLink, neither of which uses it.

Removing it is preferred over `GitRepository?.`, which would keep Nuke's warning and stack trace in every such worktree build and one more Nuke API for #192 to migrate. In a worktree inside the clone, Nuke finds the outer `.git` directory instead, and the build logs the outer checkout's branch. The lost log lines repeat what the CI run and the local checkout already show.

No automated test is added. `tests/BuildPipeline.Tests` links only build sources that do not depend on Nuke, so it cannot run the pipeline, and the fix deletes the failing code. The criteria are verified by running the commands in such a worktree and such a clone. A worktree inside the clone does not count, because the unmodified build already succeeds there, and neither does a clone whose `origin` is a local path, because Nuke cannot parse that URL and the unmodified build fails. No documentation mentions the limitation.
