# Alec — History

## Core Context

- **Project:** A .NET MongoDB library providing migrations, event store, transactional outbox, queues, and helpers published as multiple NuGet packages
- **Role:** Feature Dev
- **Joined:** 2026-04-09T19:19:45.617Z

## Learnings

<!-- Append learnings below -->
- Queue subscriptions must exclude `IsTerminal=true` items both from the available-item filter and from the closed-item TTL partial filter so dead-letter queries remain possible; touchpoints: `src/Chaos.Mongo/Queues/MongoQueueSubscription.cs`, `tests/Chaos.Mongo.Tests/Queues/MongoQueueSubscriptionTests.cs`, and `tests/Chaos.Mongo.Tests/Integration/Queues/MongoQueueRetentionIntegrationTests.cs`.
- Queue review-fix validation succeeded with targeted queue tests in `tests/Chaos.Mongo.Tests/Chaos.Mongo.Tests.csproj` and the full `bash build.sh Test` pipeline.
