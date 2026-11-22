// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace BuildPipeline;

/// <summary>
/// Decides which target frameworks of a test project the pipeline builds and tests. Has no dependency on Fallout, so it
/// can be linked into a test project.
/// </summary>
internal static class TestFrameworkSelection
{
    /// <summary>
    /// Returns every declared framework when no framework is requested, or only the requested one.
    /// </summary>
    /// <param name="projectName">The test project, named in the error message.</param>
    /// <param name="declaredFrameworks">The frameworks the project declares in <c>TargetFrameworks</c>.</param>
    /// <param name="requestedFramework">The framework to restrict the run to, or <c>null</c> or blank for all of them.</param>
    /// <returns>The frameworks to run, in declaration order.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the project does not declare <paramref name="requestedFramework"/>, so a typo or a dropped target
    /// fails the run instead of silently testing fewer frameworks.
    /// </exception>
    public static IReadOnlyList<String> Select(String projectName,
                                               IReadOnlyList<String> declaredFrameworks,
                                               String? requestedFramework)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        ArgumentNullException.ThrowIfNull(declaredFrameworks);

        if (String.IsNullOrWhiteSpace(requestedFramework))
            return declaredFrameworks;

        var framework = requestedFramework.Trim();
        if (!declaredFrameworks.Contains(framework, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"{projectName} does not declare the requested framework {framework}; it declares {String.Join(", ", declaredFrameworks)}.");
        }

        return [framework];
    }
}
