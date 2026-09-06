// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox.Tests;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

public class OutboxHostedServiceShutdownTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task StoppingAsync_FailureAfterDeadline_LogsEveryCleanupFailure(Boolean cancellationFailure)
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var processor = new DeferredShutdownProcessor();
        var logger = new ShutdownFailureLogger();
        var service = new OutboxHostedService(processor, provider.GetRequiredService<IServiceScopeFactory>(), logger);
        using var cancellation = new CancellationTokenSource();
        var stopping = service.StoppingAsync(cancellation.Token);
        cancellation.Cancel();
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        processor.Completion.Task.IsCompleted.Should().BeFalse();

        Exception firstFailure = cancellationFailure
            ? new OperationCanceledException("Cleanup faulted with a cancellation exception")
            : new InvalidOperationException("First cleanup failed");
        var secondFailure = new InvalidOperationException("Second cleanup failed");
        processor.Completion.SetException([firstFailure, secondFailure]);
        var failure = await logger.Failure.Task.WaitAsync(TimeSpan.FromSeconds(5));
        failure.Should().BeOfType<AggregateException>().Which.InnerExceptions.Should().Equal(firstFailure, secondFailure);
    }

    [Test]
    public async Task StoppingAsync_FailureBeforeDeadline_PropagatesFailure()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var processor = new DeferredShutdownProcessor();
        var logger = new ShutdownFailureLogger();
        var service = new OutboxHostedService(processor, provider.GetRequiredService<IServiceScopeFactory>(), logger);
        var stopping = service.StoppingAsync(CancellationToken.None);
        var failure = new InvalidOperationException("Cleanup failed");
        processor.Completion.SetException(failure);

        var stop = () => stopping;
        (await stop.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
        logger.Failure.Task.IsCompleted.Should().BeFalse();
    }
}

internal sealed class DeferredShutdownProcessor : IOutboxProcessor
{
    public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken = default) => Completion.Task;
}

internal sealed class ShutdownFailureLogger : ILogger<OutboxHostedService>
{
    public TaskCompletionSource<Exception?> Failure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public Boolean IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, String> formatter)
    {
        if (logLevel == LogLevel.Error)
            Failure.TrySetResult(exception);
    }
}
