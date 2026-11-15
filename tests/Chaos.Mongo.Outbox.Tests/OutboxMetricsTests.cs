// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox.Tests;

using FluentAssertions;
using NUnit.Framework;

public class OutboxMetricsTests
{
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
}
