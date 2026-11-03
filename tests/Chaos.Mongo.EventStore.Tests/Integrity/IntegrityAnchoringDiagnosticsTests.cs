// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity;

using Chaos.Mongo.EventStore.Integrity;
using Chaos.Mongo.EventStore.Tests.Integration;
using FluentAssertions;
using NUnit.Framework;

public class IntegrityAnchoringDiagnosticsTests
{
    [Test]
    public void RecordAnchorDelay_WithDelay_RecordsSecondsWithSecondsUnit()
    {
        // Arrange
        using var metrics = new AnchoringMetricsCollector();
        var delay = TimeSpan.FromSeconds(42);

        // Act
        IntegrityAnchoringDiagnostics.RecordAnchorDelay(delay, false);

        // Assert
        var measurement = metrics.Measurements.Should()
                                 .ContainSingle(x => x.Instrument == IntegrityMetrics.Instruments.AnchorDelay)
                                 .Subject;
        measurement.Unit.Should().Be("s");
        measurement.Value.Should().Be(42.0);
    }

    [Test]
    public void RecordMalformedCandidateSkipped_WithReason_RecordsCounterTaggedWithTheReason()
    {
        // Arrange
        using var metrics = new AnchoringMetricsCollector();

        // Act
        IntegrityAnchoringDiagnostics.RecordMalformedCandidateSkipped(MalformedCandidateReason.Version);

        // Assert
        var measurement = metrics.Measurements.Should()
                                 .ContainSingle(x => x.Instrument == IntegrityMetrics.Instruments.MalformedCandidatesSkipped)
                                 .Subject;
        measurement.Value.Should().Be(1);
        measurement.Tags.Should().Contain(IntegrityMetrics.Tags.MalformedReason, nameof(MalformedCandidateReason.Version));
    }

    [Test]
    public void RecordRevocationConfirmationDelay_WithDelay_RecordsSecondsWithSecondsUnit()
    {
        // Arrange
        using var metrics = new AnchoringMetricsCollector();
        var delay = TimeSpan.FromSeconds(17);

        // Act
        IntegrityAnchoringDiagnostics.RecordRevocationConfirmationDelay(delay, false);

        // Assert
        var measurement = metrics.Measurements.Should()
                                 .ContainSingle(x => x.Instrument == IntegrityMetrics.Instruments.RevocationConfirmationDelay)
                                 .Subject;
        measurement.Unit.Should().Be("s");
        measurement.Value.Should().Be(17.0);
    }
}
