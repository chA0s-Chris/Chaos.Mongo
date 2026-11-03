// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.TestSupport;

using System.Diagnostics;
using System.Reflection;

/// <summary>
/// Runs a single <c>[Explicit]</c> test method of the calling test assembly in a freshly started child process,
/// so it observes none of the <c>BsonClassMap</c>/<c>ConventionRegistry</c> state any other test in the shared
/// process may already have registered.
/// </summary>
/// <remarks>
/// Class map and convention registration is process-global and irreversible for the lifetime of a process (see
/// the isolation note on <c>CheckpointClassMapRegistrationTests</c>), so a test that needs a convention pack
/// registered before a specific production type is first used can only be reliable in a process where nothing
/// else has touched that type yet. Marking such a test <c>[Explicit]</c> excludes it from the ordinary test run
/// (avoiding that pollution for every other test sharing the process), while this helper launches a child
/// <c>dotnet test</c> process that explicitly selects it by its full name, which NUnit runs despite the
/// <c>[Explicit]</c> attribute because it was individually, explicitly selected rather than picked up by a
/// broader run.
/// </remarks>
public static class IsolatedConventionTestProcess
{
    /// <summary>
    /// Launches the calling assembly's test host in a child process, filtered to exactly
    /// <paramref name="fullyQualifiedTestName"/>, waits for it to exit, and returns its result.
    /// </summary>
    /// <param name="fullyQualifiedTestName">
    /// The full name (namespace, type, and method) of the single <c>[Explicit]</c> test method to run in
    /// isolation.
    /// </param>
    /// <returns>The child process's exit code together with its captured standard output and error streams.</returns>
    public static IsolatedTestResult Run(String fullyQualifiedTestName)
    {
        var assemblyPath = Assembly.GetExecutingAssembly().Location;
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(assemblyPath),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("test");
        startInfo.ArgumentList.Add(assemblyPath);
        startInfo.ArgumentList.Add("--filter");
        startInfo.ArgumentList.Add($"FullyQualifiedName={fullyQualifiedTestName}");

        using var process = Process.Start(startInfo) ??
                            throw new InvalidOperationException("Failed to start the isolated test process.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return new IsolatedTestResult(process.ExitCode, standardOutput, standardError);
    }

    /// <summary>
    /// The result of running a single test method in an isolated child process.
    /// </summary>
    /// <param name="ExitCode">The child process's exit code; <c>0</c> means the isolated test passed.</param>
    /// <param name="StandardOutput">The child process's captured standard output, for diagnosing a failure.</param>
    /// <param name="StandardError">The child process's captured standard error, for diagnosing a failure.</param>
    public sealed record IsolatedTestResult(Int32 ExitCode, String StandardOutput, String StandardError);
}
