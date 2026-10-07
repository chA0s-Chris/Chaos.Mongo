// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

/// <summary>
/// Logger test double that records the level and message of every log entry.
/// </summary>
/// <typeparam name="TCategory">The logger category.</typeparam>
internal sealed class CapturingLogger<TCategory> : ILogger<TCategory>
{
    public ConcurrentQueue<(LogLevel Level, String Message)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public Boolean IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel,
                            EventId eventId,
                            TState state,
                            Exception? exception,
                            Func<TState, Exception?, String> formatter)
        => Entries.Enqueue((logLevel, formatter(state, exception)));
}
