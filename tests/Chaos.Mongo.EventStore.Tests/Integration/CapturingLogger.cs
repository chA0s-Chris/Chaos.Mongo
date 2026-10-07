// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

/// <summary>
/// Logger test double that records the level and message of every log entry and optionally lets a test
/// react synchronously to an entry while the logging code waits.
/// </summary>
/// <typeparam name="TCategory">The logger category.</typeparam>
internal sealed class CapturingLogger<TCategory> : ILogger<TCategory>
{
    public ConcurrentQueue<(LogLevel Level, String Message)> Entries { get; } = new();

    public Action<LogLevel, String>? OnLog { get; set; }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public Boolean IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel,
                            EventId eventId,
                            TState state,
                            Exception? exception,
                            Func<TState, Exception?, String> formatter)
    {
        var message = formatter(state, exception);
        Entries.Enqueue((logLevel, message));
        OnLog?.Invoke(logLevel, message);
    }
}
