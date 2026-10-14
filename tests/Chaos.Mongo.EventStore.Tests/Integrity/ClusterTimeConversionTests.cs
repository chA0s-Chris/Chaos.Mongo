// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity;

using Chaos.Mongo.EventStore.Integrity;
using FluentAssertions;
using MongoDB.Bson;
using NUnit.Framework;

public class ClusterTimeConversionTests
{
    [Test]
    public void ToCommittedUtc_AfterSignedOverflow_DoesNotWrapAround()
    {
        var committed = ChangeStreamAnchorCandidateSource.ToCommittedUtc(new BsonTimestamp(unchecked((Int32)0x80000001u), 1));

        committed.Should().Be(new DateTime(2038, 1, 19, 3, 14, 9, DateTimeKind.Utc));
    }

    [Test]
    public void ToCommittedUtc_BeforeSignedOverflow_ReturnsUtcTime()
    {
        var committed = ChangeStreamAnchorCandidateSource.ToCommittedUtc(new BsonTimestamp(Int32.MaxValue, 1));

        committed.Should().Be(new DateTime(2038, 1, 19, 3, 14, 7, DateTimeKind.Utc));
        committed.Kind.Should().Be(DateTimeKind.Utc);
    }
}
