// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using Microsoft.Extensions.Logging;

/// <summary>
/// Runs one background loop at a time and stops it safely however many stops overlap.
/// </summary>
/// <remarks>
/// The first caller of <see cref="StopAsync"/> starts a single stop operation for the current run: it cancels the loop's
/// token, joins the loop, and only then disposes the token source and clears the state. The work is dispatched off the
/// caller's stack, so the first caller is bound by its own token like every later one. Later callers await that same
/// operation, each bounded by its own token. A caller whose token ends first returns without ending the operation, so the
/// source is never disposed while the loop runs and no cancellation callback is dropped. All state transitions happen
/// under one gate that is never held across an await or while the loop's own code runs.
/// </remarks>
internal sealed class StoppableLoop
{
    private readonly Object _gate = new();
    private readonly ILogger _logger;
    private readonly String _stopDescription;
    private Task _loop = Task.CompletedTask;
    private CancellationTokenSource? _source;
    private Task? _stopOperation;

    /// <summary>
    /// Initializes a new instance of the <see cref="StoppableLoop"/> class.
    /// </summary>
    /// <param name="logger">The logger for a failing cancellation callback.</param>
    /// <param name="stopDescription">Names what is stopped, for example <c>stopping the integrity sealing sweep</c>.</param>
    public StoppableLoop(ILogger logger, String stopDescription)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(stopDescription);
        _logger = logger;
        _stopDescription = stopDescription;
    }

    /// <summary>
    /// Stops the loop and waits for it to observe cancellation. Does nothing when no loop runs.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token that limits this caller's wait, not the stop itself.</param>
    /// <returns>A task that completes when the loop has stopped or the token ended the wait.</returns>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource? created = null;
        Task operation;
        CancellationTokenSource source;
        Task loop;
        lock (_gate)
        {
            if (_source is null)
            {
                return;
            }

            if (_stopOperation is null)
            {
                created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _stopOperation = created.Task;
            }

            operation = _stopOperation;
            source = _source;
            loop = _loop;
        }

        // The operation is published above and its work is dispatched off this caller's stack. Cancelling a source whose
        // token is already cancelled, and disposing a linked source, complete synchronously and the latter waits for a
        // running callback, so inline work could keep the first caller from ever reaching its own bounded wait below.
        if (created is not null)
        {
            _ = Task.Run(() => RunStopAsync(created, source, loop), CancellationToken.None);
        }

        try
        {
            await operation.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
    }

    /// <summary>
    /// Starts the loop unless one is already running or still stopping.
    /// </summary>
    /// <param name="run">Runs the loop until its token is cancelled.</param>
    /// <param name="cancellationToken">A cancellation token linked to the loop.</param>
    /// <returns><c>true</c> when the loop was started; <c>false</c> when a loop is already running or stopping.</returns>
    public Boolean TryStart(Func<CancellationToken, Task> run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        TaskCompletionSource completion;
        CancellationToken loopToken;
        lock (_gate)
        {
            if (_source is not null)
            {
                return false;
            }

            _source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _loop = completion.Task;
            loopToken = _source.Token;
        }

        _ = TrackAsync(run, loopToken, completion);
        return true;
    }

    /// <summary>
    /// Runs the loop and mirrors its outcome into <paramref name="completion"/>, which a stop awaits. The loop starts
    /// synchronously on the caller's thread, outside the gate.
    /// </summary>
    private static async Task TrackAsync(Func<CancellationToken, Task> run, CancellationToken cancellationToken, TaskCompletionSource completion)
    {
        try
        {
            await run(cancellationToken);
            completion.SetResult();
        }
        catch (Exception ex)
        {
            completion.SetException(ex);
        }
    }

    private async Task RunStopAsync(TaskCompletionSource operation, CancellationTokenSource source, Task loop)
    {
        Exception? failure = null;
        try
        {
            try
            {
                await source.CancelLoggingFailuresAsync(_logger, _stopDescription);
            }
            catch (Exception ex)
            {
                // Logging the failing callback may itself fail. Cancellation has been requested either way, so the loop is
                // still joined below and the failure surfaces after the cleanup.
                failure = ex;
            }

            try
            {
                await loop;
            }
            catch (Exception ex)
            {
                failure ??= ex;
            }
        }
        finally
        {
            // The source is released while the stop is still published, so an overlapping stop keeps waiting for this
            // operation and a start still sees the run as stopping until the state is cleared and the operation settled.
            source.Dispose();

            // Only the state of the run this operation stops is cleared; a later run has its own source. Clearing and
            // settling are one transition, so no caller or start can observe the run as stopped before the operation ends.
            lock (_gate)
            {
                if (ReferenceEquals(_source, source))
                {
                    _source = null;
                    _loop = Task.CompletedTask;
                    _stopOperation = null;
                }

                if (failure is null)
                {
                    operation.SetResult();
                }
                else
                {
                    operation.SetException(failure);
                }
            }

            // Every caller may have abandoned its wait, so the operation observes its own fault.
            _ = operation.Task.Exception;
        }
    }
}
