# Squad Team

> Chaos.Mongo — .NET MongoDB library with event store, outbox, migrations, locking, and queues

## Coordinator

| Name | Role | Notes |
|------|------|-------|
| Squad | Coordinator | Routes work, enforces handoffs and reviewer gates. |

## Members

| Name | Role | Charter | Status |
|------|------|---------|--------|
| Nate | Lead / Architect | `.squad/agents/nate/charter.md` | ✅ Active |
| Eliot | Library Dev (Core) | `.squad/agents/eliot/charter.md` | ✅ Active |
| Alec | Feature Dev (EventStore & Outbox) | `.squad/agents/alec/charter.md` | ✅ Active |
| Sophie | DevOps (CI/CD & Packaging) | `.squad/agents/sophie/charter.md` | ✅ Active |
| Parker | Test Engineer | `.squad/agents/parker/charter.md` | ✅ Active |
| Scribe | Session Logger | `.squad/agents/scribe/charter.md` | 📋 Silent |
| Ralph | Work Monitor | `.squad/agents/ralph/charter.md` | 🔄 Monitor |

## Project Context

- **Project:** Chaos.Mongo
- **Packages:** Chaos.Mongo (core), Chaos.Mongo.EventStore, Chaos.Mongo.Outbox
- **Stack:** .NET 8/9/10, MongoDB Driver 3.4, NUnit, Testcontainers
- **Build:** Nuke pipeline, GitHub Actions CI/CD, NuGet publishing
- **Created:** 2026-04-09
