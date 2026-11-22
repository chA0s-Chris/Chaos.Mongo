// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity;

using Chaos.Mongo.EventStore.Integrity;
using Chaos.Mongo.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

/// <summary>
/// Stops of the sealing sweep that overlap each other or fail. The loop's only reach into the outside world is the lock
/// acquisition at the start of every pass, which a hand-crafted <see cref="IMongoHelper"/> controls.
/// </summary>
public class EventStreamSealingSweepStopTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(5);

    private static readonly MongoEventStoreOptions<RegistrationSweepAggregate> Options = new()
    {
        SealingSweepInterval = TimeSpan.FromHours(24),
        SealingSweepRetryDelay = TimeSpan.FromMinutes(1)
    };

    [Test]
    public async Task StopAsync_CallbackThrows_LogsTheFailureAndStillStops()
    {
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var helper = new LockAcquisitionStub(async token =>
        {
            token.Register(() => throw new InvalidOperationException("callback failed"));
            running.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return null;
        });
        var logger = new RecordingLogger<EventStreamSealingSweep<RegistrationSweepAggregate>>();
        var sut = CreateSut(helper, logger);
        await sut.StartAsync();
        await running.Task.WaitAsync(Generous);

        var act = () => sut.StopAsync();

        await act.Should().NotThrowAsync();
        var warning = logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Warning).Subject;
        warning.Exception.Should().BeOfType<AggregateException>().Which.InnerException.Should().BeOfType<InvalidOperationException>()
               .Which.Message.Should().Be("callback failed");
        await sut.StartAsync();
        await sut.StopAsync();
        helper.Acquisitions.Should().Be(2, "the sweep can be started again after the stop");
    }

    [Test]
    public async Task StopAsync_LoggingTheFailingCallbackThrows_StillJoinsTheLoopAndSettlesTheStop()
    {
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loopEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLoop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var helper = new LockAcquisitionStub(async token =>
        {
            token.Register(() => throw new InvalidOperationException("callback failed"));
            running.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                await releaseLoop.Task;
                loopEnded.TrySetResult();
            }

            return null;
        });
        var failuresLeft = 1;
        var logger = new RecordingLogger<EventStreamSealingSweep<RegistrationSweepAggregate>>
        {
            OnLog = entry =>
            {
                if (entry.Level == LogLevel.Warning && Interlocked.Decrement(ref failuresLeft) >= 0)
                {
                    throw new InvalidOperationException("logger failed");
                }
            }
        };
        var sut = CreateSut(helper, logger);
        await sut.StartAsync();
        await running.Task.WaitAsync(Generous);

        var stop = sut.StopAsync();
        Boolean stillPending;
        try
        {
            stillPending = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromMilliseconds(200))) != stop;
        }
        finally
        {
            releaseLoop.TrySetResult();
        }

        // The loop is joined and the state cleared although logging the callback failure failed; the failure surfaces afterwards.
        stillPending.Should().BeTrue("the stop must join the loop, which is still running, before it settles");
        var act = async () => await stop.WaitAsync(Generous);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("logger failed");
        loopEnded.Task.IsCompleted.Should().BeTrue();
        await sut.StartAsync();
        await sut.StopAsync();
        helper.Acquisitions.Should().Be(2, "the state was cleared, so a new run can start");
    }

    [Test]
    public async Task StopAsync_OverlappingStopWithCancelledToken_DoesNotDropCallbacksOrThrow()
    {
        var barrier = new CancellationCallbackBarrier();
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var helper = new LockAcquisitionStub(async token =>
        {
            barrier.RegisterOn(token);
            running.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return null;
        });
        var sut = CreateSut(helper);
        await sut.StartAsync();
        await running.Task.WaitAsync(Generous);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        try
        {
            var first = sut.StopAsync();
            await barrier.BlockerEntered.WaitAsync(Generous);
            var second = sut.StopAsync(cancelled.Token);

            await second.WaitAsync(Generous);
            first.IsCompleted.Should().BeFalse("the first stop is held by its callback");
            barrier.Release();
            await first.WaitAsync(Generous);
            await barrier.MarkerRan.WaitAsync(Generous);
        }
        finally
        {
            barrier.Release();
        }

        await sut.StartAsync();
        await sut.StopAsync();
        helper.Acquisitions.Should().Be(2, "the sweep can be started again after the stops");
    }

    [Test]
    public async Task StopAsync_LoopTokenCallbackStillRunningAfterTheLoopEnded_ReturnsWithTheCallersTokenAndSettlesWhenItEnds()
    {
        var barrier = new CancellationCallbackBarrier();
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loopEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var helper = new LockAcquisitionStub(async token =>
        {
            barrier.RegisterOn(token);
            running.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                loopEnded.TrySetResult();
            }

            return null;
        });
        var sut = CreateSut(helper);
        using var startup = new CancellationTokenSource();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var cancelledToken = cancelled.Token;
        Task joiner;

        try
        {
            await sut.StartAsync(startup.Token);
            await running.Task.WaitAsync(Generous);

            // Cancelling the startup token cancels the loop's linked token. The blocker callback holds that cancellation
            // after the loop has ended, and the first stop then has nothing left to cancel or join, only to release.
            _ = Task.Run(startup.Cancel);
            await barrier.BlockerEntered.WaitAsync(Generous);
            await loopEnded.Task.WaitAsync(Generous);

            var first = Task.Run(() => sut.StopAsync(cancelledToken));
            await first.WaitAsync(Generous);
            joiner = Task.Run(() => sut.StopAsync());
            var stillPending = await Task.WhenAny(joiner, Task.Delay(TimeSpan.FromMilliseconds(200))) != joiner;

            stillPending.Should().BeTrue("the shared stop cannot settle while the callback holds the source");
        }
        finally
        {
            barrier.Release();
        }

        await joiner.WaitAsync(Generous);
        await barrier.MarkerRan.WaitAsync(Generous);
        await sut.StartAsync();
        await sut.StopAsync();
        helper.Acquisitions.Should().Be(2, "the sweep can be started again once the callback has ended");
    }

    [Test]
    public async Task StopAsync_StartedAgainAfterSynchronouslyCompletedStop_StopsTheNewRun()
    {
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var helper = new LockAcquisitionStub(async token =>
        {
            running.TrySetResult();
            token.Register(() => runCancelled.TrySetResult());
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return null;
        });
        var sut = CreateSut(helper);

        // A source that is already cancelled and a loop that never runs let the first stop complete synchronously.
        using var cancelledAtStart = new CancellationTokenSource();
        await cancelledAtStart.CancelAsync();
        await sut.StartAsync(cancelledAtStart.Token);
        await sut.StopAsync();
        await sut.StartAsync();
        await running.Task.WaitAsync(Generous);

        await sut.StopAsync();

        runCancelled.Task.IsCompleted.Should().BeTrue("the stop of the second run must cancel its loop instead of reusing the first run's completed stop");
    }

    [Test]
    public async Task StopAsync_TimedOutStopWhileLoopIgnoresCancellation_KeepsTheSweepStoppingUntilTheLoopEnds()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var markerRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var helper = new LockAcquisitionStub(async token =>
        {
            token.Register(() => markerRan.TrySetResult());
            running.TrySetResult();
            await release.Task;
            return null;
        });
        var sut = CreateSut(helper);
        await sut.StartAsync();
        await running.Task.WaitAsync(Generous);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        Task uncancelledStop;

        try
        {
            await sut.StopAsync(cancelled.Token).WaitAsync(Generous);
            await markerRan.Task.WaitAsync(Generous);

            await sut.StartAsync();
            uncancelledStop = sut.StopAsync();
            var stillPending = await Task.WhenAny(uncancelledStop, Task.Delay(TimeSpan.FromMilliseconds(200))) != uncancelledStop;

            stillPending.Should().BeTrue("the loop is still running, so the sweep is still stopping");
            helper.Acquisitions.Should().Be(1, "a start during the stop must not run a second loop beside the old one");
        }
        finally
        {
            release.TrySetResult();
        }

        await uncancelledStop.WaitAsync(Generous);
        await sut.StartAsync();
        await sut.StopAsync();
        helper.Acquisitions.Should().Be(2, "the sweep can be started again once the loop has ended");
    }

    private static EventStreamSealingSweep<RegistrationSweepAggregate> CreateSut(IMongoHelper helper,
                                                                                 ILogger<EventStreamSealingSweep<RegistrationSweepAggregate>>? logger = null)
        => new(helper,
               Options,
               new FakeTimeProvider(DateTimeOffset.UtcNow),
               logger ?? new RecordingLogger<EventStreamSealingSweep<RegistrationSweepAggregate>>());
}
