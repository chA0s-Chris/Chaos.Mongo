// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity;

using Chaos.Mongo.EventStore.Integrity;
using FluentAssertions;
using NUnit.Framework;

public class CompletedAnchorTests
{
    [TestCase(nameof(AnchorStatus.AwaitingTimestamp))]
    [TestCase(nameof(AnchorStatus.Completed))]
    public void From_AnchorNotCompleted_ThrowsArgumentException(String statusName)
    {
        var status = Enum.Parse<AnchorStatus>(statusName);
        var act = () => CompletedAnchor.From(CreateAnchor(status, status == AnchorStatus.Completed ? null : DateTime.UnixEpoch));

        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void From_CompletedAnchor_CarriesItsStoredFieldsAndTokens()
    {
        var anchor = CreateAnchor(AnchorStatus.Completed, DateTime.UnixEpoch.AddMinutes(6));

        var completed = CompletedAnchor.From(anchor);

        completed.Should().BeEquivalentTo(anchor, options => options.ExcludingMissingMembers());
        completed.CompletedUtc.Should().Be(DateTime.UnixEpoch.AddMinutes(6));
    }

    private static AnchorRecord CreateAnchor(AnchorStatus status, DateTime? completedUtc)
        => new(Guid.NewGuid(),
               status,
               3,
               AnchorEncoding.FormatVersion,
               AnchorEncoding.HashAlgorithmName,
               new Byte[32],
               Enumerable.Repeat((Byte)1, 32).ToArray(),
               Enumerable.Repeat((Byte)2, 32).ToArray(),
               7,
               DateTime.UnixEpoch,
               DateTime.UnixEpoch.AddMinutes(5),
               DateTime.UnixEpoch,
               [new AnchorTimestamp("tsa", [1], [[2]], [[3]], DateTime.UnixEpoch, DateTime.UnixEpoch)],
               [],
               DateTime.UnixEpoch,
               completedUtc);
}
