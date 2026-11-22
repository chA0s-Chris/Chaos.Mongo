// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox.Tests;

using FluentAssertions;
using NUnit.Framework;

public class OutboxProcessorMetricsTests
{
    [Test]
    public async Task StartAsync_PublishSucceeds_RecordsPublishedDurationAndAgeOnce()
    {
        await using var fixture = new OutboxMetricsFixture();
        var time = fixture.Time;
        fixture.Collection.Message.CreatedUtc = time.GetUtcNow().UtcDateTime - TimeSpan.FromSeconds(90);
        fixture.Publisher.Handler = (_, _) =>
        {
            time.Advance(TimeSpan.FromMilliseconds(250));
            return Task.CompletedTask;
        };

        await fixture.RunUntilAsync(fixture.Collection.UpdatesReached.Task);

        var tags = new Dictionary<String, String?>
        {
            ["outbox.destination"] = fixture.Destination
        };
        fixture.Measurements.Should().HaveCount(3);
        var published = fixture.Measurements.Should().ContainSingle(m => m.InstrumentName == "chaos.mongo.outbox.published").Subject;
        published.Value.Should().Be(1);
        published.Unit.Should().BeNull();
        published.Tags.Should().Equal(tags);
        var duration = fixture.Measurements.Should().ContainSingle(m => m.InstrumentName == "chaos.mongo.outbox.publish.duration").Subject;
        duration.Value.Should().Be(0.25, "only the publisher call is measured");
        duration.Unit.Should().Be("s");
        duration.Tags.Should().Equal(tags);
        var age = fixture.Measurements.Should().ContainSingle(m => m.InstrumentName == "chaos.mongo.outbox.message.age").Subject;
        age.Value.Should().Be(90.25, "the age runs from creation to the processed timestamp");
        age.Unit.Should().Be("s");
        age.Tags.Should().Equal(tags);
    }

    [Test]
    public async Task StartAsync_CreatedUtcInTheFuture_ClampsMessageAgeToZero()
    {
        await using var fixture = new OutboxMetricsFixture();
        fixture.Collection.Message.CreatedUtc = fixture.Time.GetUtcNow().UtcDateTime + TimeSpan.FromHours(1);

        await fixture.RunUntilAsync(fixture.Collection.UpdatesReached.Task);

        fixture.Measurements.Should().ContainSingle(m => m.InstrumentName == "chaos.mongo.outbox.message.age")
               .Which.Value.Should().Be(0);
    }

    [Test]
    public async Task StartAsync_PublisherThrows_RecordsPublishFailedAndNoPublished()
    {
        await using var fixture = new OutboxMetricsFixture();
        fixture.Publisher.Handler = (_, _) => throw new InvalidOperationException("Broker down");

        await fixture.RunUntilAsync(fixture.Collection.UpdatesReached.Task);

        var failed = fixture.Measurements.Should().ContainSingle().Subject;
        failed.InstrumentName.Should().Be("chaos.mongo.outbox.publish.failed");
        failed.Value.Should().Be(1);
        failed.Unit.Should().BeNull();
        failed.Tags.Should().Equal(new Dictionary<String, String?>
        {
            ["outbox.destination"] = fixture.Destination
        });
    }

    [Test]
    public async Task StartAsync_PublishFailsAfterMaxRetriesExhausted_RecordsFailedAndTerminalFailedOnce()
    {
        await using var fixture = new OutboxMetricsFixture(1);
        // MaxRetries 1 allows two attempts, so the second attempt is the terminal one.
        fixture.Collection.Message.AttemptCount = 2;
        fixture.Publisher.Handler = (_, _) => throw new InvalidOperationException("Broker down");

        await fixture.RunUntilAsync(fixture.Collection.UpdatesReached.Task);

        fixture.Measurements.Select(m => m.InstrumentName).Should().BeEquivalentTo(
            "chaos.mongo.outbox.publish.failed",
            "chaos.mongo.outbox.publish.terminal_failed");
        var terminal = fixture.Measurements.Single(m => m.InstrumentName == "chaos.mongo.outbox.publish.terminal_failed");
        terminal.Value.Should().Be(1);
        terminal.Unit.Should().BeNull();
        terminal.Tags.Should().Equal(new Dictionary<String, String?>
        {
            ["outbox.destination"] = fixture.Destination
        });
    }

    [Test]
    public async Task StartAsync_PublishFailsWithUnlimitedRetries_NeverRecordsTerminalFailed()
    {
        await using var fixture = new OutboxMetricsFixture(null);
        fixture.Collection.Message.AttemptCount = 1_000;
        fixture.Publisher.Handler = (_, _) => throw new InvalidOperationException("Broker down");

        await fixture.RunUntilAsync(fixture.Collection.UpdatesReached.Task);

        fixture.Measurements.Should().ContainSingle().Which.InstrumentName.Should().Be("chaos.mongo.outbox.publish.failed");
    }

    [Test]
    public async Task StartAsync_PermanentFailureLosesOwnership_RecordsFailedWithoutTerminalFailed()
    {
        await using var fixture = new OutboxMetricsFixture(1);
        fixture.Collection.Message.AttemptCount = 2;
        fixture.Collection.Update = (_, _) => Task.FromResult(MetricsOutboxCollection.Modified(0));
        fixture.Publisher.Handler = (_, _) => throw new InvalidOperationException("Broker down");

        await fixture.RunUntilAsync(fixture.Collection.UpdatesReached.Task);

        fixture.Measurements.Should().ContainSingle().Which.InstrumentName.Should().Be("chaos.mongo.outbox.publish.failed");
    }

    [Test]
    public async Task StartAsync_ClaimTerminalizesDirectly_RecordsTerminalFailedWithoutPublishing()
    {
        await using var fixture = new OutboxMetricsFixture(5);
        // WithMaxRetries(5) is configured, so an AttemptCount of 6 is the claim's own terminal branch.
        fixture.Collection.Claim = message => new OutboxMessage
        {
            Id = message.Id,
            State = OutboxMessageState.Failed,
            Type = message.Type,
            Payload = message.Payload,
            AttemptCount = 6
        };
        var published = false;
        fixture.Publisher.Handler = (_, _) =>
        {
            published = true;
            return Task.CompletedTask;
        };

        await fixture.RunUntilAsync(fixture.Collection.ClaimAttempted.Task);

        published.Should().BeFalse();
        fixture.Collection.UpdateCalls.Should().Be(0);
        var terminal = fixture.Measurements.Should().ContainSingle().Subject;
        terminal.InstrumentName.Should().Be("chaos.mongo.outbox.publish.terminal_failed");
        terminal.Value.Should().Be(1);
        terminal.Unit.Should().BeNull();
        terminal.Tags.Should().Equal(new Dictionary<String, String?>
        {
            ["outbox.destination"] = fixture.Destination
        });
    }

    [Test]
    public async Task StartAsync_FinalizationThrows_RecordsPublishFailedWithoutPublished()
    {
        await using var fixture = new OutboxMetricsFixture();
        fixture.Collection.ExpectedUpdates = 2;
        fixture.Collection.Update = (call, _) => call == 1
            ? throw new InvalidOperationException("Finalization failed")
            : Task.FromResult(MetricsOutboxCollection.Modified(1));

        await fixture.RunUntilAsync(fixture.Collection.UpdatesReached.Task);

        fixture.Measurements.Should().ContainSingle().Which.InstrumentName.Should().Be("chaos.mongo.outbox.publish.failed");
    }

    [Test]
    public async Task StartAsync_FinalizationLosesOwnership_RecordsNeitherPublishedNorFailed()
    {
        await using var fixture = new OutboxMetricsFixture();
        fixture.Collection.Update = (_, _) => Task.FromResult(MetricsOutboxCollection.Modified(0));

        await fixture.RunUntilAsync(fixture.Collection.UpdatesReached.Task);

        fixture.Measurements.Should().BeEmpty();
    }

    [Test]
    public async Task StartAsync_ShutdownDuringPublish_RecordsNeitherPublishedNorFailed()
    {
        await using var fixture = new OutboxMetricsFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Publisher.Handler = async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };

        // Stopping cancels the publisher, which releases the lock instead of resolving the attempt.
        await fixture.RunUntilAsync(entered.Task);

        fixture.Collection.UpdateCalls.Should().Be(1, "the lock is released");
        fixture.Measurements.Should().BeEmpty();
    }

    [Test]
    public async Task StartAsync_FinalizationTimesOutAfterShutdown_RecordsNeitherPublishedNorFailed()
    {
        await using var fixture = new OutboxMetricsFixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Publisher.Handler = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        fixture.Collection.ExpectedUpdates = 2;
        // The finalization hangs until the shutdown grace period elapses; the following lock release succeeds.
        fixture.Collection.Update = async (call, token) =>
        {
            if (call == 1)
            {
                await Task.Delay(Timeout.Infinite, token);
            }

            return MetricsOutboxCollection.Modified(1);
        };

        await fixture.Processor.StartAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stopping = fixture.Processor.StopAsync();
        release.SetResult();
        await fixture.Collection.UpdateStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The stop arms the grace period from a cancellation callback that runs off the caller's stack, so advance in steps
        // until the stop has settled instead of racing a single advance against the registration.
        for (var i = 0; i < 100 && !stopping.IsCompleted; i++)
        {
            await Task.Delay(10);
            fixture.Time.Advance(TimeSpan.FromSeconds(1));
        }

        await stopping.WaitAsync(TimeSpan.FromSeconds(10));

        fixture.Collection.UpdateCalls.Should().Be(2, "the timed-out finalization is followed by a lock release");
        fixture.Measurements.Should().BeEmpty();
    }
}
