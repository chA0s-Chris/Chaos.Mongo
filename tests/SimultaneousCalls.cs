// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.TestSupport;

using System.Collections.Concurrent;

/// <summary>
/// Calls an action on several threads at once, so tests can show that a registration tolerates concurrent callers,
/// for example hosts built in parallel.
/// </summary>
public static class SimultaneousCalls
{
    /// <summary>
    /// Starts <paramref name="threadCount"/> dedicated threads, releases them together into <paramref name="action"/>,
    /// waits for all of them, and throws an <see cref="AggregateException"/> with every failure.
    /// </summary>
    public static void Run(Int32 threadCount, Action action)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threadCount, 2);
        ArgumentNullException.ThrowIfNull(action);

        var failures = new ConcurrentQueue<Exception>();
        var threads = Enumerable.Range(0, threadCount)
                                .Select(_ => new Thread(start =>
                                {
                                    ((Barrier)start!).SignalAndWait();
                                    try
                                    {
                                        action();
                                    }
                                    catch (Exception exception)
                                    {
                                        failures.Enqueue(exception);
                                    }
                                }))
                                .ToList();

        // Every thread waits on the barrier, which is passed to it rather than captured, until all have started.
        using (var start = new Barrier(threadCount))
        {
            threads.ForEach(thread => thread.Start(start));
            threads.ForEach(thread => thread.Join());
        }

        if (!failures.IsEmpty)
        {
            throw new AggregateException(failures);
        }
    }
}
