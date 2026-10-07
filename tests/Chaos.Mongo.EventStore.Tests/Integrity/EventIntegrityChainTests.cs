// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity;

using Chaos.Mongo.EventStore.Integrity;
using FluentAssertions;
using NUnit.Framework;

public class EventIntegrityChainTests
{
    [Test]
    public void ComputeGenesis_DifferentAggregateIds_ReturnsDifferentValues()
    {
        var first = EventIntegrityChain.ComputeGenesis("Ledger", Guid.NewGuid());
        var second = EventIntegrityChain.ComputeGenesis("Ledger", Guid.NewGuid());

        first.Should().NotEqual(second);
    }

    [Test]
    public void ComputeGenesis_DifferentAggregateTypes_ReturnsDifferentValues()
    {
        var aggregateId = Guid.NewGuid();

        var first = EventIntegrityChain.ComputeGenesis("Ledger", aggregateId);
        var second = EventIntegrityChain.ComputeGenesis("Order", aggregateId);

        first.Should().NotEqual(second);
    }

    [Test]
    public void ComputeGenesis_SameInput_ReturnsSameSha256Value()
    {
        var aggregateId = Guid.NewGuid();

        var first = EventIntegrityChain.ComputeGenesis("Ledger", aggregateId);
        var second = EventIntegrityChain.ComputeGenesis("Ledger", aggregateId);

        first.Should().Equal(second).And.HaveCount(32);
    }

    [Test]
    public void ComputeHash_DifferentPreviousHash_ReturnsDifferentValues()
    {
        Byte[] document = [1, 2, 3];

        var first = EventIntegrityChain.ComputeHash(document, new Byte[32]);
        var second = EventIntegrityChain.ComputeHash(document, Enumerable.Repeat((Byte)1, 32).ToArray());

        first.Should().NotEqual(second);
    }

    [Test]
    public void Seal_ReturnsCurrentFormatChainedFromPreviousHash()
    {
        Byte[] document = [1, 2, 3];
        var previousHash = EventIntegrityChain.ComputeGenesis("Ledger", Guid.NewGuid());

        var integrity = EventIntegrityChain.Seal(document, previousHash, IntegritySealMode.Retroactive);

        integrity.FormatVersion.Should().Be(1);
        integrity.Algorithm.Should().Be("SHA-256");
        integrity.PreviousHash.Should().Equal(previousHash);
        integrity.Hash.Should().Equal(EventIntegrityChain.ComputeHash(document, previousHash));
        integrity.SealMode.Should().Be(IntegritySealMode.Retroactive);
    }
}
