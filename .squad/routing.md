# Work Routing

How to decide who handles what.

## Routing Table

| Work Type | Route To | Examples |
|-----------|----------|----------|
| Core library (MongoHelper, connections, collections, DI) | Eliot | Fix MongoHelper, add collection mapping, update AddMongo() |
| Migrations & locking | Eliot | Add migration step, fix distributed lock, update migration runner |
| Queues | Eliot | MongoDB-backed queue changes, queue configuration |
| Event store | Alec | Aggregate repository, event persistence, checkpointing, projections |
| Outbox | Alec | Outbox processor, publisher interface, hosted service, message dispatch |
| Architecture & API design | Nate | Package boundaries, public API review, ADRs, trade-off analysis |
| Code review | Tara | Fresh-eyes review, merge-gate feedback, cross-cutting consistency checks |
| Reviewer gate | Tara | PR review before merge, contract validation, public-surface drift checks |
| CI/CD & build pipeline | Sophie | GitHub Actions, Nuke targets, NuGet packaging, release workflow |
| Testing | Parker | Write tests, find edge cases, Testcontainers setup, coverage |
| Scope & priorities | Nate | What to build next, trade-offs, decisions |
| Session logging | Scribe | Automatic — never needs routing |

## Issue Routing

| Label | Action | Who |
|-------|--------|-----|
| `squad` | Triage: analyze issue, assign `squad:{member}` label | Nate (Lead) |
| `squad:eliot` | Core library work | Eliot |
| `squad:alec` | EventStore/Outbox work | Alec |
| `squad:sophie` | CI/CD/build work | Sophie |
| `squad:parker` | Test work | Parker |
| `squad:tara` | Expert review work | Tara |
| `squad:{name}` | Pick up issue and complete the work | Named member |

### How Issue Assignment Works

1. When a GitHub issue gets the `squad` label, the **Lead** triages it — analyzing content, assigning the right `squad:{member}` label, and commenting with triage notes.
2. When a `squad:{member}` label is applied, that member picks up the issue in their next session.
3. Members can reassign by removing their label and adding another member's label.
4. The `squad` label is the "inbox" — untriaged issues waiting for Lead review.

## Rules

1. **Eager by default** — spawn all agents who could usefully start work, including anticipatory downstream work.
2. **Scribe always runs** after substantial work, always as `mode: "background"`. Never blocks.
3. **Quick facts → coordinator answers directly.** Don't spawn an agent for "what port does the server run on?"
4. **When two agents could handle it**, pick the one whose domain is the primary concern.
5. **"Team, ..." → fan-out.** Spawn all relevant agents in parallel as `mode: "background"`.
6. **Anticipate downstream work.** If a feature is being built, spawn the tester to write test cases from requirements simultaneously.
7. **Issue-labeled work** — when a `squad:{member}` label is applied to an issue, route to that member. The Lead handles all `squad` (base label) triage.

## Work Type → Agent

| Work Type | Primary | Secondary |
|-----------|---------|----------|
| Architecture, API design | Nate | Tara (for fresh-eyes review) |
| Core library, connections, DI, migrations, locking, queues | Eliot | — |
| Event store, outbox, aggregate repository | Alec | Eliot (when touching core APIs) |
| CI/CD, Nuke build, NuGet packaging, releases | Sophie | — |
| Tests, Testcontainers, edge cases, coverage | Parker | — |
| Fresh-eyes review, merge gate, cross-cutting consistency | Tara | Nate |
