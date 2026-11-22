// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using Microsoft.Extensions.Logging;

/// <summary>
/// Cancels a <see cref="CancellationTokenSource"/> without letting a failing cancellation callback escape.
/// </summary>
internal static class LoggedCancellation
{
    /// <summary>
    /// Cancels <paramref name="source"/>. <see cref="CancellationTokenSource.CancelAsync"/> runs every registered callback
    /// and then faults when one of them threw, although cancellation has been requested either way. The failure is
    /// logged instead of replacing the outcome of the code that cancels, for example a stop or the result of a hook.
    /// </summary>
    /// <param name="source">The source to cancel.</param>
    /// <param name="logger">The logger that receives the warning.</param>
    /// <param name="operation">What is being cancelled, for example <c>stopping the sealing sweep</c>.</param>
    /// <returns>A task that completes once every callback has run; it never faults because of a callback.</returns>
    public static async Task CancelLoggingFailuresAsync(this CancellationTokenSource source, ILogger logger, String operation)
    {
        try
        {
            await source.CancelAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A cancellation callback failed while {Operation}", operation);
        }
    }
}
