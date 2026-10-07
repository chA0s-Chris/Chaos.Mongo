// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity;

using Chaos.Mongo.EventStore.Integrity;
using FluentAssertions;
using NUnit.Framework;

public class EventStreamSealingSweepTests
{
    private static readonly MongoEventStoreOptions<RegistrationSweepAggregate> Options = new()
    {
        SealingSweepInterval = TimeSpan.FromHours(24),
        SealingSweepRetryDelay = TimeSpan.FromMinutes(1)
    };

    [Test]
    public void GetDelayAfter_LockHeldElsewhere_ReturnsSweepInterval()
    {
        var delay = EventStreamSealingSweep<RegistrationSweepAggregate>.GetDelayAfter(SealingPassOutcome.LockUnavailable, Options);

        delay.Should().Be(Options.SealingSweepInterval);
    }

    [Test]
    public void GetDelayAfter_PassCompleted_ReturnsSweepInterval()
    {
        var delay = EventStreamSealingSweep<RegistrationSweepAggregate>.GetDelayAfter(SealingPassOutcome.Completed, Options);

        delay.Should().Be(Options.SealingSweepInterval);
    }

    [Test]
    public void GetDelayAfter_PassFailed_ReturnsRetryDelay()
    {
        var delay = EventStreamSealingSweep<RegistrationSweepAggregate>.GetDelayAfter(null, Options);

        delay.Should().Be(Options.SealingSweepRetryDelay);
    }

    [Test]
    public void GetDelayAfter_PassLostLock_ReturnsRetryDelay()
    {
        var delay = EventStreamSealingSweep<RegistrationSweepAggregate>.GetDelayAfter(SealingPassOutcome.LockLost, Options);

        delay.Should().Be(Options.SealingSweepRetryDelay);
    }
}
