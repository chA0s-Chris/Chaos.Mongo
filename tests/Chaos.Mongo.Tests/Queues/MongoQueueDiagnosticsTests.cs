// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests.Queues;

using Chaos.Mongo.Queues;
using FluentAssertions;
using NUnit.Framework;
using MongoDefaults = MongoDefaults;

public class MongoQueueDiagnosticsTests
{
    [Test]
    public void RecordProcessingSucceeded_WithProcessingDuration_RecordsSecondsWithSecondsUnit()
    {
        // Arrange
        using var metricsCollector = new QueueMetricsCollector();
        var queueDefinition = new MongoQueueDefinition
        {
            CollectionName = "test-queue",
            LockLeaseTime = MongoDefaults.QueueLockLeaseTime,
            PayloadType = typeof(TestPayload),
            BatchSize = 1,
            PayloadHandlerType = typeof(IMongoQueuePayloadHandler<TestPayload>),
            AutoStartSubscription = false
        };
        var processingDuration = TimeSpan.FromSeconds(2.5);

        // Act
        MongoQueueDiagnostics.RecordProcessingSucceeded(queueDefinition, processingDuration, TimeSpan.Zero, "ttl-retention");

        // Assert
        var measurement = metricsCollector.Measurements.Should()
                                          .ContainSingle(x => x.InstrumentName == MongoQueueMetrics.Instruments.ProcessingDuration)
                                          .Subject;
        measurement.Unit.Should().Be("s");
        measurement.Value.Should().Be(2.5);
    }

    public class TestPayload;
}
