// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace BuildPipeline.Tests;

using FluentAssertions;
using NUnit.Framework;

public class CoverageThresholdsTests
{
    [Test]
    public void GetViolations_AggregateAtThreshold_ReturnsEmpty()
    {
        var violations = CoverageThresholds.GetViolations(0.95, new Dictionary<String, Double>());

        violations.Should().BeEmpty();
    }

    [Test]
    public void GetViolations_AggregateImmediatelyBelowThreshold_ReturnsAggregateViolation()
    {
        var violations = CoverageThresholds.GetViolations(0.9499, new Dictionary<String, Double>());

        violations.Should().HaveCount(1);
        violations[0].Should().Contain("Aggregate");
    }

    [Test]
    public void GetViolations_EverythingAboveBothThresholds_ReturnsEmpty()
    {
        var violations = CoverageThresholds.GetViolations(0.97, new Dictionary<String, Double>
        {
            ["Chaos.Mongo"] = 0.91,
            ["Chaos.Mongo.EventStore"] = 0.98,
            ["Chaos.Mongo.Outbox"] = 0.95
        });

        violations.Should().BeEmpty();
    }

    [Test]
    public void GetViolations_PackageAtThreshold_ReturnsEmpty()
    {
        var violations = CoverageThresholds.GetViolations(1.0, new Dictionary<String, Double>
        {
            ["Chaos.Mongo"] = 0.90
        });

        violations.Should().BeEmpty();
    }

    [Test]
    public void GetViolations_PackageFailureWithPassingAggregate_ReturnsOnlyThePackageViolation()
    {
        var violations = CoverageThresholds.GetViolations(0.96, new Dictionary<String, Double>
        {
            ["Chaos.Mongo"] = 0.80,
            ["Chaos.Mongo.EventStore"] = 0.99,
            ["Chaos.Mongo.Outbox"] = 0.99
        });

        violations.Should().HaveCount(1);
        violations[0].Should().Contain("Chaos.Mongo").And.NotContain("Aggregate");
    }

    [Test]
    public void GetViolations_PackageImmediatelyBelowThreshold_ReturnsPackageViolation()
    {
        var violations = CoverageThresholds.GetViolations(1.0, new Dictionary<String, Double>
        {
            ["Chaos.Mongo"] = 0.8999
        });

        violations.Should().HaveCount(1);
        violations[0].Should().Contain("Chaos.Mongo");
    }

    [TestCase("MinimumSupported")]
    [TestCase("Latest")]
    public void ShouldEnforceGate_RecognizedCompatibilityRole_ReturnsFalse(String mongoImageSelector)
        => CoverageThresholds.ShouldEnforceGate(mongoImageSelector).Should().BeFalse();

    [Test]
    public void ShouldEnforceGate_UnrecognizedValue_StillCatchesBelowThresholdViolation()
    {
        // An unrecognized/misspelled selector falls back to the Primary image (MongoDbTestImages.Selected),
        // so the gate must stay enforced for it too, exactly as it does for "Primary" itself.
        CoverageThresholds.ShouldEnforceGate("primary").Should().BeTrue();

        var violations = CoverageThresholds.GetViolations(0.9499, new Dictionary<String, Double>());

        violations.Should().HaveCount(1);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("Primary")]
    [TestCase("primary")]
    [TestCase("unknown-value")]
    public void ShouldEnforceGate_UnsetEmptyPrimaryOrUnrecognizedValue_ReturnsTrue(String? mongoImageSelector)
        => CoverageThresholds.ShouldEnforceGate(mongoImageSelector).Should().BeTrue();
}
