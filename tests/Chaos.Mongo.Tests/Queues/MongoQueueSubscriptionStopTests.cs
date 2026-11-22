// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests.Queues;

using Chaos.Mongo.Queues;
using Chaos.Mongo.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;
using NUnit.Framework;
using System.Reflection;

/// <summary>
/// Stops and disposals of the subscription that overlap each other, overlap the startup, or meet a failing
/// cancellation callback. The handler's token is the subscription's stopping token, so a hand-crafted handler is
/// where the tests register their callbacks and decide whether the processing loop notices the cancellation.
/// </summary>
public partial class MongoQueueSubscriptionTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(5);

    [Test]
    public async Task DisposeAsync_DisposeAndStopWhileACallbackHoldsTheCancellation_ReleaseOnlyAfterTheOperationFinished()
    {
        var barrier = new CancellationCallbackBarrier();
        var (sut, processing) = await StartHeldSubscriptionAsync(barrier);
        Task dispose;
        Task stop;

        try
        {
            dispose = sut.DisposeAsync().AsTask();
            await barrier.BlockerEntered.WaitAsync(Generous);
            await processing.WaitAsync(Generous);
            stop = sut.StopAsync();

            (await IsStillPendingAsync(dispose)).Should().BeTrue("the cancellation is still running its callbacks");
            (await IsStillPendingAsync(stop)).Should().BeTrue("a stop during a disposal joins the disposal");
            IsUsable(sut).Should().BeTrue("nothing is released while the operation runs");
        }
        finally
        {
            barrier.Release();
        }

        await dispose.WaitAsync(Generous);
        await stop.WaitAsync(Generous);
        await barrier.MarkerRan.WaitAsync(Generous);
        IsReleased(sut).Should().BeTrue();
    }

    [Test]
    public async Task DisposeAsync_DisposeWhileAnotherDisposeHoldsTheCancellation_BothWaitForTheSameOperation()
    {
        var barrier = new CancellationCallbackBarrier();
        var (sut, processing) = await StartHeldSubscriptionAsync(barrier);
        Task first;
        Task second;

        try
        {
            first = sut.DisposeAsync().AsTask();
            await barrier.BlockerEntered.WaitAsync(Generous);
            await processing.WaitAsync(Generous);
            second = sut.DisposeAsync().AsTask();

            (await IsStillPendingAsync(first)).Should().BeTrue();
            (await IsStillPendingAsync(second)).Should().BeTrue("the second disposal must not return before the first has finished");
            IsUsable(sut).Should().BeTrue();
        }
        finally
        {
            barrier.Release();
        }

        await Task.WhenAll(first, second).WaitAsync(Generous);
        await barrier.MarkerRan.WaitAsync(Generous);
        IsReleased(sut).Should().BeTrue();
    }

    [Test]
    public async Task DisposeAsync_FaultedLoop_LogsWarningAndDoesNotThrow()
    {
        var logger = new RecordingLogger<MongoQueueSubscription<TestPayload>>
        {
            OnLog = entry =>
            {
                if (entry.Level == LogLevel.Error)
                {
                    throw new InvalidOperationException("logger failed");
                }
            }
        };
        var instance = DispatchProxy.Create(typeof(IMongoDatabase), typeof(SingleCollectionDatabase));
        ((SingleCollectionDatabase)instance).Collection = new FailingPollingCollection();
        var sut = CreateSubscription(mongoHelper: new SingleDatabaseMongoHelper((IMongoDatabase)instance),
                                     payloadPrioritizer: new MongoQueuePayloadPrioritizer(),
                                     timeProvider: new FakeTimeProvider(),
                                     logger: logger);
        await sut.StartAsync();
        await GetProcessingTask(sut).Invoking(task => task.WaitAsync(Generous)).Should().ThrowAsync<InvalidOperationException>("logging the failure faults the loop");

        var act = async () => await sut.DisposeAsync();

        await act.Should().NotThrowAsync();
        logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Warning && entry.Exception is InvalidOperationException)
              .Which.Exception!.Message.Should().Be("logger failed");
        IsReleased(sut).Should().BeTrue();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DisposeAsync_HandlerIgnoresCancellation_StaysPendingUntilTheLoopHasFinished(Boolean disposeFirst)
    {
        var releaseHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var collection = new ObservedPollingCollection
        {
            OpenFailure = _ => CreateStandaloneException()
        };
        collection.Publish();
        var sut = CreateStoppableSubscription(collection, async token =>
        {
            token.Register(() => cancellationSeen.TrySetResult());
            entered.TrySetResult();
            await releaseHandler.Task;
        });
        await sut.StartAsync();
        await entered.Task.WaitAsync(Generous);
        Task dispose;
        Task stop;

        try
        {
            // Every cancellation callback completes, yet the handler keeps the processing task running.
            if (disposeFirst)
            {
                dispose = sut.DisposeAsync().AsTask();
                await cancellationSeen.Task.WaitAsync(Generous);
                stop = sut.StopAsync();
            }
            else
            {
                stop = sut.StopAsync();
                await cancellationSeen.Task.WaitAsync(Generous);
                dispose = sut.DisposeAsync().AsTask();
            }

            (await IsStillPendingAsync(dispose)).Should().BeTrue("the processing task is still running");
            (await IsStillPendingAsync(stop)).Should().BeTrue("an uncancelled stop waits for the tasks, also during a disposal");
            IsUsable(sut).Should().BeTrue("the handler is still using the subscription");
        }
        finally
        {
            releaseHandler.TrySetResult();
        }

        await Task.WhenAll(dispose, stop).WaitAsync(Generous);
        IsReleased(sut).Should().BeTrue();
    }

    [Test]
    public async Task DisposeAsync_WhileStartupOpensTheChangeStream_WaitsForTheStartupAndDisposesTheCursor()
    {
        var collection = new ObservedPollingCollection
        {
            HoldOpen = true
        };
        var sut = CreatePollingSubscription(collection, new FakeTimeProvider());
        var starting = sut.StartAsync();
        await collection.OpenStarted.Task.WaitAsync(Generous);
        Task dispose;

        try
        {
            dispose = sut.DisposeAsync().AsTask();

            (await IsStillPendingAsync(dispose)).Should().BeTrue("startup still uses the token source, the semaphore and the cursor it opens");
            IsUsable(sut).Should().BeTrue();
        }
        finally
        {
            collection.AllowOpen.TrySetResult();
        }

        await starting.WaitAsync(Generous);
        await dispose.WaitAsync(Generous);
        GetTask(sut, "_monitorTask").Should().NotBeSameAs(Task.CompletedTask, "the monitor was published for the disposal to join");
        GetTask(sut, "_processingTask").Should().NotBeSameAs(Task.CompletedTask);
        collection.WatchCount.Should().Be(1);
        collection.DisposedCursorCount.Should().Be(1, "the monitor owns the pre-opened cursor and disposes it when it stops");
        IsReleased(sut).Should().BeTrue();
    }

    [Test]
    public async Task StopAsync_WhileStartupOpensTheChangeStream_WaitsForTheStartup()
    {
        var collection = new ObservedPollingCollection
        {
            HoldOpen = true
        };
        await using var sut = CreatePollingSubscription(collection, new FakeTimeProvider());
        var starting = sut.StartAsync();
        await collection.OpenStarted.Task.WaitAsync(Generous);
        Task stop;

        try
        {
            stop = sut.StopAsync();

            (await IsStillPendingAsync(stop)).Should().BeTrue("the tasks to join do not exist before startup has published them");
        }
        finally
        {
            collection.AllowOpen.TrySetResult();
        }

        await starting.WaitAsync(Generous);
        await stop.WaitAsync(Generous);
        collection.DisposedCursorCount.Should().Be(1);
    }

    [Test]
    public async Task StopAsync_CallbackThrows_LogsTheFailureAndStillStopsAndDisposes()
    {
        var logger = new RecordingLogger<MongoQueueSubscription<TestPayload>>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var collection = new ObservedPollingCollection
        {
            OpenFailure = _ => CreateStandaloneException()
        };
        collection.Publish();
        var sut = CreateStoppableSubscription(collection, async token =>
        {
            token.Register(() => throw new InvalidOperationException("callback failed"));
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, logger);
        await sut.StartAsync();
        await entered.Task.WaitAsync(Generous);

        var stop = async () => await sut.StopAsync();
        var dispose = async () => await sut.DisposeAsync();

        await stop.Should().NotThrowAsync();
        await dispose.Should().NotThrowAsync();
        var failure = logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Warning && entry.Exception is AggregateException).Subject;
        failure.Exception!.InnerException.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("callback failed");
        sut.IsActive.Should().BeFalse();
        IsReleased(sut).Should().BeTrue();
    }

    [Test]
    public async Task StopAsync_OverlappingStopsWhileACallbackHoldsTheCancellation_SecondWaitsUnlessItsTokenEnds()
    {
        var barrier = new CancellationCallbackBarrier();
        var logger = new CapturingLogger();
        var (sut, processing) = await StartHeldSubscriptionAsync(barrier, logger);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        Task first;
        Task second;

        try
        {
            first = sut.StopAsync();
            await barrier.BlockerEntered.WaitAsync(Generous);
            await processing.WaitAsync(Generous);

            second = sut.StopAsync();
            await sut.StopAsync(cancelled.Token).WaitAsync(Generous);

            (await IsStillPendingAsync(second)).Should().BeTrue("the second stop shares the first, which is still running its callbacks");
            logger.Count(LogLevel.Warning, "has been cancelled").Should().Be(1, "the caller whose token ended logs the existing warning");
        }
        finally
        {
            barrier.Release();
        }

        await Task.WhenAll(first, second).WaitAsync(Generous);
        await barrier.MarkerRan.WaitAsync(Generous);
        await sut.DisposeAsync();
    }

    [Test]
    public async Task StopAsync_StopThenDisposeWhileACallbackHoldsTheCancellation_DisposeWaitsForTheStop()
    {
        var barrier = new CancellationCallbackBarrier();
        var (sut, processing) = await StartHeldSubscriptionAsync(barrier);
        Task stop;
        Task dispose;

        try
        {
            stop = sut.StopAsync();
            await barrier.BlockerEntered.WaitAsync(Generous);
            await processing.WaitAsync(Generous);
            dispose = sut.DisposeAsync().AsTask();

            (await IsStillPendingAsync(dispose)).Should().BeTrue("the stop it joins is still running its callbacks");
            IsUsable(sut).Should().BeTrue();
        }
        finally
        {
            barrier.Release();
        }

        await Task.WhenAll(stop, dispose).WaitAsync(Generous);
        await barrier.MarkerRan.WaitAsync(Generous);
        IsReleased(sut).Should().BeTrue();
    }

    [Test]
    public async Task DisposeAsync_LoggingTheFailedLoopThrows_ReleasesTheResourcesAndSettlesTheDisposalSuccessfully()
    {
        var logger = new RecordingLogger<MongoQueueSubscription<TestPayload>>
        {
            OnLog = entry =>
            {
                if (entry.Level is LogLevel.Error or LogLevel.Warning)
                {
                    throw new InvalidOperationException("logger failed");
                }
            }
        };
        var instance = DispatchProxy.Create(typeof(IMongoDatabase), typeof(SingleCollectionDatabase));
        ((SingleCollectionDatabase)instance).Collection = new FailingPollingCollection();
        var sut = CreateSubscription(mongoHelper: new SingleDatabaseMongoHelper((IMongoDatabase)instance),
                                     payloadPrioritizer: new MongoQueuePayloadPrioritizer(),
                                     timeProvider: new FakeTimeProvider(),
                                     logger: logger);
        await sut.StartAsync();
        await GetProcessingTask(sut).Invoking(task => task.WaitAsync(Generous)).Should().ThrowAsync<InvalidOperationException>();

        var first = async () => await sut.DisposeAsync().AsTask().WaitAsync(Generous);
        var second = async () => await sut.DisposeAsync().AsTask().WaitAsync(Generous);

        // The failed loop is reported on a best-effort basis: a failing logging provider neither strands the disposal nor
        // faults it once both resources are released, because a faulted disposal could never be cleared by a queue.
        await first.Should().NotThrowAsync();
        await second.Should().NotThrowAsync();
        IsSourceReleased(sut).Should().BeTrue();
        IsSemaphoreReleased(sut).Should().BeTrue();
    }

    [Test]
    public async Task StartSubscriptionAsync_LoggingTheFailedLoopOfTheOldSubscriptionThrowsOnce_ReplacesTheReleasedSubscription()
    {
        // The loop fails on its error log, which throws. The disposal of the stopped subscription then fails to log the
        // loop's failure once, after which the logging provider works again.
        var failuresLeft = 2;
        var logger = new RecordingLogger<MongoQueueSubscription<TestPayload>>
        {
            OnLog = entry =>
            {
                if (entry.Level is LogLevel.Error or LogLevel.Warning && Interlocked.Decrement(ref failuresLeft) >= 0)
                {
                    throw new InvalidOperationException("logger failed");
                }
            }
        };
        var (queue, created) = CreateQueueOverFailingSubscriptions(logger);
        await using var _ = queue;
        await queue.StartSubscriptionAsync();
        await GetProcessingTask(created[0]).Invoking(task => task.WaitAsync(Generous)).Should().ThrowAsync<InvalidOperationException>();

        // The stop reports the failed loop, and the subscription is then inactive but not yet released.
        await queue.Invoking(q => q.StopSubscriptionAsync()).Should().ThrowAsync<Exception>();
        queue.IsRunning.Should().BeFalse();

        // The disposal of the old subscription cannot fail because of a diagnostic, so the replacement is installed.
        await queue.StartSubscriptionAsync().WaitAsync(Generous);

        created.Should().HaveCount(2, "exactly one replacement is created");
        IsReleased(created[0]).Should().BeTrue();
        queue.IsRunning.Should().BeTrue();
        await queue.StopSubscriptionAsync().WaitAsync(Generous);
        IsReleased(created[1]).Should().BeTrue();
    }

    [Test]
    public async Task StartSubscriptionAsync_LoggingTheFailedLoopOfTheOldSubscriptionBlocks_TheStartEndsWithItsTokenAndKeepsTheOldSubscription()
    {
        // The loop fails on its error log, which throws once. The disposal of the stopped subscription then reports the
        // failed loop through a logging provider that is slow, so the warning is held on the disposal's own work.
        var warningEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWarning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failedOnce = 0;
        var logger = new RecordingLogger<MongoQueueSubscription<TestPayload>>
        {
            OnLog = entry =>
            {
                if (entry.Level == LogLevel.Error && Interlocked.Exchange(ref failedOnce, 1) == 0)
                {
                    throw new InvalidOperationException("logger failed");
                }

                if (entry.Level == LogLevel.Warning && entry.Message.Contains("while it was being disposed"))
                {
                    warningEntered.TrySetResult();
                    releaseWarning.Task.Wait(Generous);
                }
            }
        };
        var (queue, created) = CreateQueueOverFailingSubscriptions(logger);
        await using var _ = queue;
        await queue.StartSubscriptionAsync();
        await GetProcessingTask(created[0]).Invoking(task => task.WaitAsync(Generous)).Should().ThrowAsync<InvalidOperationException>();
        await queue.Invoking(q => q.StopSubscriptionAsync()).Should().ThrowAsync<Exception>();
        queue.IsRunning.Should().BeFalse();

        using var startTimeout = new CancellationTokenSource();
        Task start;
        try
        {
            // Started on its own thread, so a start that runs the held warning on its caller's stack cannot stall the test.
            var startToken = startTimeout.Token;
            start = Task.Run(() => queue.StartSubscriptionAsync(startToken));
            await warningEntered.Task.WaitAsync(Generous);
            await startTimeout.CancelAsync();

            await start.Invoking(t => t.WaitAsync(Generous)).Should().ThrowAsync<OperationCanceledException>(
                "the caller's token bounds the wait for the disposal, whatever the logging provider is doing");
            created.Should().HaveCount(1, "no replacement is created before the old subscription is disposed");
            IsReleased(created[0]).Should().BeFalse("the disposal is still held in its warning");
        }
        finally
        {
            releaseWarning.TrySetResult();
        }

        await queue.StartSubscriptionAsync().WaitAsync(Generous);

        created.Should().HaveCount(2, "exactly one replacement is created once the disposal has settled");
        IsReleased(created[0]).Should().BeTrue();
        queue.IsRunning.Should().BeTrue();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task StopSubscriptionAsync_LoggingTheTimeoutWarningBlocksOrThrows_ReturnsWithItsTokenAndTheDisposalContinues(Boolean warningThrows)
    {
        // A handler that ignores the stopping token keeps the stop operation pending, so the caller's token ends the wait. The
        // warning about that timeout runs through a logging provider that is slow or fails.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var warningEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWarning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new RecordingLogger<MongoQueueSubscription<TestPayload>>
        {
            OnLog = entry =>
            {
                if (entry.Level != LogLevel.Warning || !entry.Message.Contains("background tasks may still be running"))
                {
                    return;
                }

                warningEntered.TrySetResult();
                if (warningThrows)
                {
                    throw new InvalidOperationException("logger failed");
                }

                // Held for longer than the caller's bounded wait, so only a warning off the caller's stack can end that wait.
                releaseWarning.Task.Wait(Generous + Generous);
            }
        };
        var collection = new ObservedPollingCollection
        {
            OpenFailure = _ => CreateStandaloneException()
        };
        collection.Publish();
        var created = new List<MongoQueueSubscription<TestPayload>>();
        var factory = new DelegateSubscriptionFactory(async token =>
        {
            var subscription = CreateStoppableSubscription(collection, handlerToken =>
            {
                handlerToken.Register(() => cancellationObserved.TrySetResult());
                entered.TrySetResult();
                return releaseHandler.Task;
            }, logger);
            created.Add(subscription);
            await subscription.StartAsync(token);
            return subscription;
        });
        var queue = new MongoQueue<TestPayload>(new MongoQueueDefinition
                                                {
                                                    AutoStartSubscription = false,
                                                    CollectionName = "test-queue",
                                                    LockLeaseTime = TimeSpan.FromSeconds(1),
                                                    PayloadType = typeof(TestPayload),
                                                    BatchSize = 1
                                                },
                                                factory,
                                                new NoOpPublisher());
        await using var _ = queue;
        await queue.StartSubscriptionAsync();
        await entered.Task.WaitAsync(Generous);

        using var stopTimeout = new CancellationTokenSource();
        try
        {
            var stopToken = stopTimeout.Token;
            var stop = Task.Run(() => queue.StopSubscriptionAsync(stopToken));

            // The stop operation is under way once the handler's token is cancelled, which is after the queue acquired its
            // semaphore and published the stop. The caller's timeout then ends.
            // The timeout is cancelled by a timer thread, because the continuation of the stop's cancelled wait can run inline on
            // the thread that cancels, which a warning held on the caller's stack would block.
            await cancellationObserved.Task.WaitAsync(Generous);
            stopTimeout.CancelAfter(TimeSpan.FromMilliseconds(10));

            await stop.Invoking(t => t.WaitAsync(Generous)).Should().NotThrowAsync(
                "the caller's token bounds the wait, whatever the logging provider does with the warning about the timeout");
            await warningEntered.Task.WaitAsync(Generous);
            IsReleased(created[0]).Should().BeFalse("the handler still runs, so the disposal has not released anything");
        }
        finally
        {
            releaseWarning.TrySetResult();
            releaseHandler.TrySetResult();
        }

        // The semaphore of the queue was released by the timed-out stop, and the original subscription is still the one that is
        // disposed, without a second stop.
        await queue.StopSubscriptionAsync().WaitAsync(Generous);

        created.Should().HaveCount(1);
        IsReleased(created[0]).Should().BeTrue();
        queue.IsRunning.Should().BeFalse();
    }

    [Test]
    public async Task StartAsync_StopAndSecondStartWhileStartupOpensTheChangeStream_OnlyTheFirstStartupRunsAndEverythingIsJoined()
    {
        var collection = new ObservedPollingCollection
        {
            HoldOpen = true
        };
        var sut = CreatePollingSubscription(collection, new FakeTimeProvider());
        var firstStart = sut.StartAsync();
        await collection.OpenStarted.Task.WaitAsync(Generous);
        Task stop;
        Task dispose;
        Task secondStart;

        try
        {
            stop = sut.StopAsync();

            // The stop owns the stopping token source and the tasks it joins, so it admits no second startup.
            secondStart = sut.StartAsync();
            await secondStart.WaitAsync(Generous);
            dispose = sut.DisposeAsync().AsTask();

            (await IsStillPendingAsync(stop)).Should().BeTrue("startup A still has to publish its tasks");
            (await IsStillPendingAsync(dispose)).Should().BeTrue("the disposal joins the stop, which waits for startup A");
            IsUsable(sut).Should().BeTrue("startup A still uses the token source, the semaphore and the cursor it opens");
            collection.WatchCount.Should().Be(1, "the rejected second start never opens a change stream");
        }
        finally
        {
            collection.AllowOpen.TrySetResult();
        }

        await firstStart.WaitAsync(Generous);
        await Task.WhenAll(stop, dispose).WaitAsync(Generous);
        collection.WatchCount.Should().Be(1);
        collection.DisposedCursorCount.Should().Be(1, "the monitor owns the pre-opened cursor and disposes it when it stops");
        GetTask(sut, "_monitorTask").IsCompleted.Should().BeTrue("the stop joined the tasks startup A published");
        GetTask(sut, "_processingTask").IsCompleted.Should().BeTrue();
        IsSourceReleased(sut).Should().BeTrue();
        IsSemaphoreReleased(sut).Should().BeTrue();
    }

    [Test]
    public async Task StopAsync_LoggingTheFailingCallbackThrows_StillJoinsTheLoopAndSettlesTheStop()
    {
        var logger = new RecordingLogger<MongoQueueSubscription<TestPayload>>
        {
            OnLog = entry =>
            {
                if (entry.Message.StartsWith("A cancellation callback failed", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("logger failed");
                }
            }
        };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var collection = new ObservedPollingCollection
        {
            OpenFailure = _ => CreateStandaloneException()
        };
        collection.Publish();
        var releaseHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sut = CreateStoppableSubscription(collection, async token =>
        {
            token.Register(() => throw new InvalidOperationException("callback failed"));
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                await releaseHandler.Task;
            }
        }, logger);
        await sut.StartAsync();
        await entered.Task.WaitAsync(Generous);

        var stop = sut.StopAsync();
        Boolean stillPending;
        try
        {
            stillPending = await IsStillPendingAsync(stop);
        }
        finally
        {
            releaseHandler.TrySetResult();
        }

        stillPending.Should().BeTrue("the stop must join the processing task, which is still running, although logging the callback failure failed");
        var act = async () => await stop.WaitAsync(Generous);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("logger failed");
        GetProcessingTask(sut).IsCompleted.Should().BeTrue();
        IsUsable(sut).Should().BeTrue("a stop does not release anything");
        await sut.DisposeAsync().AsTask().WaitAsync(Generous);
        IsSourceReleased(sut).Should().BeTrue();
        IsSemaphoreReleased(sut).Should().BeTrue();
    }

    private static MongoQueueSubscription<TestPayload> CreateStoppableSubscription(ObservedPollingCollection collection,
                                                                                   Func<CancellationToken, Task> handle,
                                                                                   ILogger<MongoQueueSubscription<TestPayload>>? logger = null)
    {
        var instance = DispatchProxy.Create(typeof(IMongoDatabase), typeof(SingleCollectionDatabase));
        ((SingleCollectionDatabase)instance).Collection = collection;
        return CreateSubscription(new MongoQueueDefinition
                                  {
                                      AutoStartSubscription = false,
                                      CollectionName = "test-queue",
                                      LockLeaseTime = TimeSpan.FromSeconds(1),
                                      PayloadType = typeof(TestPayload),
                                      BatchSize = 1
                                  },
                                  new SingleDatabaseMongoHelper((IMongoDatabase)instance),
                                  new TokenAwarePayloadHandlerFactory(handle),
                                  new MongoQueuePayloadPrioritizer(),
                                  timeProvider: new FakeTimeProvider(),
                                  logger: logger);
    }

    private static Task GetProcessingTask(MongoQueueSubscription<TestPayload> sut) => GetTask(sut, "_processingTask");

    private static Task GetTask(MongoQueueSubscription<TestPayload> sut, String fieldName)
        => (Task)(typeof(MongoQueueSubscription<TestPayload>).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
                                                             ?.GetValue(sut) ?? throw new InvalidOperationException($"Missing field {fieldName}"));

    /// <summary>
    /// Whether the token source and the semaphore are both disposed.
    /// </summary>
    /// <summary>
    /// A queue over real subscriptions whose processing loop fails at once, so each created subscription ends up with a
    /// faulted loop. The created subscriptions are reported in creation order.
    /// </summary>
    private (MongoQueue<TestPayload> Queue, List<MongoQueueSubscription<TestPayload>> Created) CreateQueueOverFailingSubscriptions(
        RecordingLogger<MongoQueueSubscription<TestPayload>> logger)
    {
        var instance = DispatchProxy.Create(typeof(IMongoDatabase), typeof(SingleCollectionDatabase));
        ((SingleCollectionDatabase)instance).Collection = new FailingPollingCollection();
        var helper = new SingleDatabaseMongoHelper((IMongoDatabase)instance);
        var created = new List<MongoQueueSubscription<TestPayload>>();
        var factory = new DelegateSubscriptionFactory(async token =>
        {
            var subscription = CreateSubscription(mongoHelper: helper,
                                                  payloadPrioritizer: new MongoQueuePayloadPrioritizer(),
                                                  timeProvider: new FakeTimeProvider(),
                                                  logger: logger);
            created.Add(subscription);
            await subscription.StartAsync(token);
            return subscription;
        });
        var queue = new MongoQueue<TestPayload>(new MongoQueueDefinition
                                                {
                                                    AutoStartSubscription = false,
                                                    CollectionName = "test-queue",
                                                    LockLeaseTime = TimeSpan.FromSeconds(1),
                                                    PayloadType = typeof(TestPayload),
                                                    BatchSize = 1
                                                },
                                                factory,
                                                new NoOpPublisher());
        return (queue, created);
    }

    private static Boolean IsReleased(MongoQueueSubscription<TestPayload> sut) => IsSourceReleased(sut) && IsSemaphoreReleased(sut);

    /// <summary>
    /// Whether the token source and the semaphore are both still usable. Each is checked on its own, because a half
    /// released subscription is neither released nor usable.
    /// </summary>
    private static Boolean IsUsable(MongoQueueSubscription<TestPayload> sut) => !IsSourceReleased(sut) && !IsSemaphoreReleased(sut);

    private static Boolean IsSemaphoreReleased(MongoQueueSubscription<TestPayload> sut)
    {
        try
        {
            _ = GetSignal(sut).AvailableWaitHandle;
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    private static Boolean IsSourceReleased(MongoQueueSubscription<TestPayload> sut)
    {
        var source = (CancellationTokenSource)(typeof(MongoQueueSubscription<TestPayload>)
                                               .GetField("_cancellationTokenSource", BindingFlags.Instance | BindingFlags.NonPublic)
                                               ?.GetValue(sut) ?? throw new InvalidOperationException("Missing token source"));
        try
        {
            _ = source.Token;
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    /// <summary>
    /// Observes a call through a bounded wait that does not use the tested token, so an unbounded wait inside the call
    /// cannot hang the test while a barrier is held.
    /// </summary>
    private static async Task<Boolean> IsStillPendingAsync(Task call)
        => await Task.WhenAny(call, Task.Delay(TimeSpan.FromMilliseconds(200))) != call;

    /// <summary>
    /// Starts a subscription whose one queue item is held by a handler that registers the barrier's callbacks on its
    /// token and ends when that token is cancelled. The barrier's blocker therefore holds the cancellation while the
    /// processing task has already finished, so the processing task's end does not depend on the held callback.
    /// </summary>
    private static async Task<(MongoQueueSubscription<TestPayload> Subscription, Task Processing)> StartHeldSubscriptionAsync(
        CancellationCallbackBarrier barrier,
        ILogger<MongoQueueSubscription<TestPayload>>? logger = null)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var collection = new ObservedPollingCollection
        {
            OpenFailure = _ => CreateStandaloneException()
        };
        collection.Publish();
        var sut = CreateStoppableSubscription(collection, token =>
        {
            barrier.RegisterOn(token);
            entered.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, logger);
        await sut.StartAsync();
        await entered.Task.WaitAsync(Generous);
        return (sut, GetProcessingTask(sut));
    }

    /// <summary>
    /// A polling collection whose polling query fails, so the processing loop's failure handling runs.
    /// </summary>
    private sealed class FailingPollingCollection : PollingQueueItemCollection
    {
        public FailingPollingCollection() : base(ObjectId.GenerateNewId()) { }

        public override Task<IAsyncCursor<TProjection>> FindAsync<TProjection>(FilterDefinition<MongoQueueItem<TestPayload>> filter,
                                                                               FindOptions<MongoQueueItem<TestPayload>, TProjection> options,
                                                                               CancellationToken cancellationToken)
            => throw new InvalidOperationException("polling failed");
    }

    /// <summary>
    /// Payload handler factory double whose handler runs the given callback with the token it receives.
    /// </summary>
    private sealed class TokenAwarePayloadHandlerFactory : IMongoQueuePayloadHandlerFactory
    {
        private readonly IMongoQueuePayloadHandler<TestPayload> _handler;

        public TokenAwarePayloadHandlerFactory(Func<CancellationToken, Task> handle) => _handler = new TokenAwarePayloadHandler(handle);

        public IMongoQueuePayloadHandler<TPayload> CreateHandler<TPayload>(IServiceProvider serviceProvider) where TPayload : class
            => (IMongoQueuePayloadHandler<TPayload>)_handler;
    }

    private sealed class TokenAwarePayloadHandler : IMongoQueuePayloadHandler<TestPayload>
    {
        private readonly Func<CancellationToken, Task> _handle;

        public TokenAwarePayloadHandler(Func<CancellationToken, Task> handle) => _handle = handle;

        public Task HandlePayloadAsync(TestPayload payload, CancellationToken cancellationToken = default) => _handle(cancellationToken);
    }

    /// <summary>
    /// Subscription factory double whose subscriptions come from the given callback.
    /// </summary>
    private sealed class DelegateSubscriptionFactory : IMongoQueueSubscriptionFactory
    {
        private readonly Func<CancellationToken, Task<MongoQueueSubscription<TestPayload>>> _create;

        public DelegateSubscriptionFactory(Func<CancellationToken, Task<MongoQueueSubscription<TestPayload>>> create) => _create = create;

        public async Task<IMongoQueueSubscription<TPayload>> CreateAndRunAsync<TPayload>(MongoQueueDefinition queueDefinition,
                                                                                         CancellationToken cancellationToken = default)
            where TPayload : class
            => (IMongoQueueSubscription<TPayload>)(Object)await _create(cancellationToken);
    }

    /// <summary>
    /// Publisher double for queues that never publish.
    /// </summary>
    private sealed class NoOpPublisher : IMongoQueuePublisher
    {
        public Task PublishAsync<TPayload>(MongoQueueDefinition queueDefinition, TPayload payload, CancellationToken cancellationToken)
            where TPayload : class
            => Task.CompletedTask;
    }
}
