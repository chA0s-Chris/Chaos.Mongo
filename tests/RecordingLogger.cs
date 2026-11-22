// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.TestSupport;

using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

/// <summary>
/// A logger that records level, message and exception of every entry, so tests can assert on the exception a warning
/// carries.
/// </summary>
/// <typeparam name="TCategory">The logger category.</typeparam>
public sealed class RecordingLogger<TCategory> : ILogger<TCategory>
{
    /// <summary>
    /// Gets the recorded entries in the order they were logged.
    /// </summary>
    public ConcurrentQueue<RecordedLogEntry> Entries { get; } = new();

    /// <summary>
    /// Gets or sets what <see cref="BeginScope{TState}"/> returns, for example a scope that counts its disposals.
    /// </summary>
    public Func<IDisposable?>? OnBeginScope { get; init; }

    /// <summary>
    /// Gets or sets an action that runs after an entry is recorded, for example to make the logger itself fail.
    /// </summary>
    public Action<RecordedLogEntry>? OnLog { get; init; }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => OnBeginScope?.Invoke();

    public Boolean IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel,
                            EventId eventId,
                            TState state,
                            Exception? exception,
                            Func<TState, Exception?, String> formatter)
    {
        var entry = new RecordedLogEntry(logLevel, formatter(state, exception), exception);
        Entries.Enqueue(entry);
        OnLog?.Invoke(entry);
    }
}

/// <summary>
/// One entry a <see cref="RecordingLogger{TCategory}"/> recorded.
/// </summary>
/// <param name="Level">The log level.</param>
/// <param name="Message">The formatted message.</param>
/// <param name="Exception">The exception the entry carries, if any.</param>
public sealed record RecordedLogEntry(LogLevel Level, String Message, Exception? Exception);
