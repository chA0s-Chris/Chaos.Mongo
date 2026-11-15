// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox.Tests;

using FluentAssertions;
using NUnit.Framework;

public class OutboxDiagnosticsTests
{
    [Test]
    public void RecordPublished_WithDurationAndAge_RecordsCounterAndBothHistogramsInSeconds()
    {
        using var metrics = new OutboxMetricsCollector();
        var destination = CreateDestination();

        OutboxDiagnostics.RecordPublished(destination, TimeSpan.FromMilliseconds(1500), TimeSpan.FromSeconds(42.5));

        var measurements = metrics.For(destination);
        measurements.Should().HaveCount(3);
        var published = measurements.Should().ContainSingle(m => m.InstrumentName == "chaos.mongo.outbox.published").Subject;
        published.Value.Should().Be(1);
        published.Unit.Should().BeNull();
        published.Tags.Should().Equal(new Dictionary<String, String?>
        {
            ["outbox.destination"] = destination
        });
        var duration = measurements.Should().ContainSingle(m => m.InstrumentName == "chaos.mongo.outbox.publish.duration").Subject;
        duration.Value.Should().Be(1.5);
        duration.Unit.Should().Be("s");
        duration.Tags.Should().Equal(new Dictionary<String, String?>
        {
            ["outbox.destination"] = destination
        });
        var age = measurements.Should().ContainSingle(m => m.InstrumentName == "chaos.mongo.outbox.message.age").Subject;
        age.Value.Should().Be(42.5);
        age.Unit.Should().Be("s");
        age.Tags.Should().Equal(new Dictionary<String, String?>
        {
            ["outbox.destination"] = destination
        });
    }

    [Test]
    public void RecordPublishFailed_Always_RecordsUnitlessCounter()
    {
        using var metrics = new OutboxMetricsCollector();
        var destination = CreateDestination();

        OutboxDiagnostics.RecordPublishFailed(destination);

        var measurement = metrics.For(destination).Should().ContainSingle().Subject;
        measurement.InstrumentName.Should().Be("chaos.mongo.outbox.publish.failed");
        measurement.Value.Should().Be(1);
        measurement.Unit.Should().BeNull();
        measurement.Tags.Should().Equal(new Dictionary<String, String?>
        {
            ["outbox.destination"] = destination
        });
    }

    [Test]
    public void RecordPublishTerminalFailed_Always_RecordsUnitlessCounter()
    {
        using var metrics = new OutboxMetricsCollector();
        var destination = CreateDestination();

        OutboxDiagnostics.RecordPublishTerminalFailed(destination);

        var measurement = metrics.For(destination).Should().ContainSingle().Subject;
        measurement.InstrumentName.Should().Be("chaos.mongo.outbox.publish.terminal_failed");
        measurement.Value.Should().Be(1);
        measurement.Unit.Should().BeNull();
        measurement.Tags.Should().Equal(new Dictionary<String, String?>
        {
            ["outbox.destination"] = destination
        });
    }

    [Test]
    public void OutboxMetrics_PublicContract_MatchesFrozenNames()
    {
        OutboxMetrics.MeterName.Should().Be("Chaos.Mongo.Outbox");
        OutboxMetrics.Instruments.Published.Should().Be("chaos.mongo.outbox.published");
        OutboxMetrics.Instruments.PublishFailed.Should().Be("chaos.mongo.outbox.publish.failed");
        OutboxMetrics.Instruments.PublishTerminalFailed.Should().Be("chaos.mongo.outbox.publish.terminal_failed");
        OutboxMetrics.Instruments.PublishDuration.Should().Be("chaos.mongo.outbox.publish.duration");
        OutboxMetrics.Instruments.MessageAge.Should().Be("chaos.mongo.outbox.message.age");
        OutboxMetrics.Tags.Destination.Should().Be("outbox.destination");
    }

    private static String CreateDestination() => $"diagnostics-{Guid.NewGuid():N}";
}
