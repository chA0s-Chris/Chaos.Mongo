// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace BuildPipeline;

/// <summary>
/// Names the JUnit result file of one test project running on one target framework. The Test target writes the
/// files and the report reads them back through <see cref="Pattern"/>, so both agree on one naming contract. Has
/// no dependency on Nuke, so it can be linked into a test project.
/// </summary>
internal static class TestResultFiles
{
    /// <summary>
    /// The glob that matches every result file <see cref="GetFileName"/> returns.
    /// </summary>
    public const String Pattern = "*.Tests.xml";

    private const String TestProjectSuffix = ".Tests";

    /// <summary>
    /// Returns <c>&lt;project&gt;.&lt;framework&gt;.Tests.xml</c>, with a trailing <c>.Tests</c> removed from the project
    /// name, so that <c>Chaos.Mongo.Tests</c> on <c>net10.0</c> becomes <c>Chaos.Mongo.net10.0.Tests.xml</c>. Each
    /// project and framework pair gets its own file, because a shared file would hold only the last run.
    /// </summary>
    public static String GetFileName(String projectName, String framework)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        ArgumentException.ThrowIfNullOrWhiteSpace(framework);

        var name = projectName.EndsWith(TestProjectSuffix, StringComparison.Ordinal) && projectName.Length > TestProjectSuffix.Length
            ? projectName[..^TestProjectSuffix.Length]
            : projectName;

        return $"{name}.{framework}.Tests.xml";
    }

    /// <summary>
    /// Splits the value of MSBuild's <c>TargetFrameworks</c> property into its frameworks.
    /// </summary>
    public static IReadOnlyList<String> ParseFrameworks(String? targetFrameworks)
    {
        return (targetFrameworks ?? String.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
