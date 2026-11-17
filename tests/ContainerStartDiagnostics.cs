// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.TestSupport;

using DotNet.Testcontainers.Containers;

/// <summary>
/// Starts test containers so that a container that stops during its start reports why. Docker keeps no trace of a
/// removed container, and the error of a failed start, such as a <c>Conflict ... is not running</c> response to the
/// readiness check, does not name the cause.
/// </summary>
public static class ContainerStartDiagnostics
{
    private const Int32 LogLines = 40;

    /// <summary>
    /// Starts <paramref name="container"/>. When the start fails, throws an exception that carries the original
    /// failure together with the container's exit code and the last lines of its output.
    /// </summary>
    public static async Task StartWithDiagnosticsAsync(this IContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);

        try
        {
            await container.StartAsync();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"The container {container.Name} of image {container.Image.FullName} failed to start. {await DescribeAsync(container)}",
                exception);
        }
    }

    private static async Task<String> DescribeAsync(IContainer container)
    {
        try
        {
            var exitCode = await container.GetExitCodeAsync();
            var (standardOutput, standardError) = await container.GetLogsAsync();
            return $"Exit code: {exitCode}.{Environment.NewLine}" +
                   $"Last output:{Environment.NewLine}{Tail(standardOutput)}{Environment.NewLine}" +
                   $"Last errors:{Environment.NewLine}{Tail(standardError)}";
        }
        catch (Exception exception)
        {
            return $"Its state could not be read: {exception.Message}";
        }
    }

    private static String Tail(String output)
        => String.Join(Environment.NewLine, output.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(LogLines));
}
