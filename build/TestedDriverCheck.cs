// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace BuildPipeline;

/// <summary>
/// Checks that a test run against the latest tested <c>MongoDB.Driver</c> really uses that version. A restore by another
/// process between Restore and BuildTests resolves the minimum again, and the tests would then pass without testing the
/// requested driver. Has no dependency on Nuke or the file system, so it can be linked into a test project.
/// </summary>
internal static class TestedDriverCheck
{
    /// <summary>
    /// Returns why the test outputs do not all use the expected driver, or an empty list if they do.
    /// </summary>
    /// <param name="expectedVersion">The driver version the run was requested to test.</param>
    /// <param name="outputs">The outputs of all test runs, including those without the driver.</param>
    /// <returns>One failure per output with another version, or one failure when no output contains the driver.</returns>
    public static IReadOnlyList<String> GetFailures(String expectedVersion, IReadOnlyCollection<TestedDriver> outputs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedVersion);
        ArgumentNullException.ThrowIfNull(outputs);

        var drivers = outputs.Where(output => output.ProductVersion is not null).ToList();

        // Without this, a changed output layout would leave nothing to check, and the check would always pass.
        if (drivers.Count == 0)
            return ["No test output contains the driver."];

        return drivers.Where(driver => RemoveBuildMetadata(driver.ProductVersion) != expectedVersion)
                      .Select(driver => $"{driver.TestRun}: found '{driver.ProductVersion}' instead of '{expectedVersion}'.")
                      .ToList();
    }

    // The product version of a driver release carries its commit, as in 1.2.3+abc1234.
    private static String? RemoveBuildMetadata(String? productVersion) => productVersion?.Split('+')[0];
}

/// <summary>
/// The driver in the output of one test run.
/// </summary>
/// <param name="TestRun">The test project and framework, named in failures.</param>
/// <param name="ProductVersion">
/// The product version of the driver assembly, empty if the assembly has none, or <c>null</c> if the output does not
/// contain the driver.
/// </param>
internal sealed record TestedDriver(String TestRun, String? ProductVersion);
