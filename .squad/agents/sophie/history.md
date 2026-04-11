# Sophie — History

## Core Context

- **Project:** A .NET MongoDB library providing migrations, event store, transactional outbox, queues, and helpers published as multiple NuGet packages
- **Role:** DevOps
- **Joined:** 2026-04-09T19:19:45.617Z

## Learnings

### PR #73 CI/CD Risk Assessment (2026-04-11)

**Finding:** Test container lifecycle violation in `MongoQueueLockExpiryIntegrationTests`.
- **Pattern:** Assembly-level `[SetUpFixture]` manages MongoDB container as singleton
- **Risk:** Test class that disposes the container violates this contract
- **Impact:** Breaks parallel test execution (nondeterministic failures)
- **Key Insight:** Test infrastructure issues can hide until CI runs with parallel workers; always audit shared resource lifecycles in integration tests

**Distinction:** Product risk vs. pipeline risk:
- Lease expiry logic itself is sound
- Issue is purely test infrastructure (how tests manage shared container)
- This shows importance of reviewing test setup patterns, not just product code

**Action:** Fix before merge—remove `[OneTimeTearDown]` disposal (1 line).

### PR #73 CI/CD Verification (2026-04-11)

**Review:** Verified that uncommitted fix correctly addresses container lifecycle violation.

**Fix Verification:**
- ✅ Removes 4 lines (`[OneTimeTearDown]` disposal method)
- ✅ Respects assembly singleton pattern (MongoAssemblySetup owns lifecycle)
- ✅ Pattern now matches `MongoHelperIntegrationTests.cs` (exemplar for correct fixture usage)
- ✅ No other integration test classes have disposal conflicts

**CI/CD Impact:**
- ✅ **No workflow changes needed** — current pipeline uses sequential execution
- ✅ **No build script changes needed** — Nuke target has no parallel worker flags
- ✅ **Ready for parallelization** — fix preemptively prevents `ObjectDisposedException` if workers are added later

**Lesson:** Assembly-level container fixtures in NUnit require discipline across all test classes in the assembly. The `.squad/skills/shared-testcontainer-lifecycle/` pattern captures this as institutional knowledge to prevent regression.

### PR #73 Commit (2026-04-11)

**Task:** Commit all PR follow-up changes on branch `squad/9-queue-resilience`.

**Work Performed:**
- ✅ Reviewed git status: 5 modified .squad files + 1 untracked skill directory
- ✅ Staged all changes: `git add -A`
- ✅ Committed with comprehensive message covering container lifecycle fix and agent history updates
- ✅ Included required Copilot co-author trailer
- ✅ **Result:** Commit `57718bc` — ready for PR merge

**Files in Commit:**
1. `tests/Chaos.Mongo.Tests/Integration/Queues/MongoQueueLockExpiryIntegrationTests.cs` — fixture corrected (no more disposal)
2. `.squad/skills/shared-testcontainer-lifecycle/SKILL.md` — new skill documenting assembly pattern
3. `.squad/agents/*/history.md` — all 4 agents' learnings recorded (Sophie, Nate, Parker, Scribe)

**Commit Message:** Focused on container lifecycle violation (the blocking issue) and skill documentation. Emphasized that this fix prevents nondeterministic CI failures if parallel test workers are added.

### PR #75 Creation for Issue #71 (2026-04-11)

**Task:** Create PR for `squad/71-queue-dead-letter-handling-and-retry-policies` branch.

**Verification:**
- ✅ Branch state: clean, latest commit `562327d` (enhance queue item retention and filtering)
- ✅ No prior PR existed for this branch
- ✅ Branch is tracking 3 commits above `main`

**PR Created:**
- **URL:** https://github.com/chA0s-Chris/Chaos.Mongo/pull/75
- **Title:** Queue dead-letter handling and retry policies
- **Body:** Linked to #71 (Phase 2 queue resilience), dependencies on #9 and #10
- **Base:** main
- **State:** OPEN

**Key:** PR body references issue #71 and explains this as Phase 2 follow-up to completed Phase 1 work (lock expiry and cleanup).

