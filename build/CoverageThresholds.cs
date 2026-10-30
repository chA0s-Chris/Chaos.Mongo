// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace BuildPipeline;

/// <summary>
/// The merged-coverage gate: 95% aggregate line coverage, 90% for each individual package. Has no
/// dependency on Nuke or the cobertura file format, so it can be linked into a test project and exercised
/// with synthetic values instead of a real coverage run.
/// </summary>
internal static class CoverageThresholds
{
    public const Double AggregateThreshold = 0.95;
    public const Double PackageThreshold = 0.90;

    /// <summary>
    /// Returns one description per threshold violation, comparing the unrounded rates. Empty when every
    /// rate meets its threshold.
    /// </summary>
    public static IReadOnlyList<String> GetViolations(Double aggregateLineRate, IReadOnlyDictionary<String, Double> packageLineRates)
    {
        var violations = new List<String>();

        if (aggregateLineRate < AggregateThreshold)
        {
            violations.Add($"Aggregate line coverage {aggregateLineRate:P2} is below the {AggregateThreshold:P0} threshold.");
        }

        foreach (var (package, lineRate) in packageLineRates)
        {
            if (lineRate < PackageThreshold)
            {
                violations.Add($"{package} line coverage {lineRate:P2} is below the {PackageThreshold:P0} threshold.");
            }
        }

        return violations;
    }

    /// <summary>
    /// Whether the merged-coverage gate should run for the given <c>CHAOSMONGO_MONGODB_IMAGE</c> selector
    /// value. Mirrors <c>MongoDbTestImages.Selected</c>'s fallback-to-Primary semantics: only the two
    /// recognized compatibility roles (<c>MinimumSupported</c>, <c>Latest</c>) skip the gate; an unset,
    /// empty, or unrecognized value resolves to Primary and keeps it enforced.
    /// </summary>
    public static Boolean ShouldEnforceGate(String? mongoImageSelector)
        => mongoImageSelector is not ("MinimumSupported" or "Latest");
}
