// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity;

using Chaos.Mongo.EventStore.Integrity;
using FluentAssertions;
using MongoDB.Bson;
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

    [Test]
    public void TryRead_MalformedDocument_ReturnsFalse()
    {
        var document = new BsonDocument
        {
            { "FormatVersion", 1 },
            { "Hash", "not-binary" }
        };

        var result = EventIntegrityChain.TryRead(document, out var integrity);

        result.Should().BeFalse();
        integrity.Should().BeNull();
    }

    [Test]
    public void TryRead_NonDocumentValue_ReturnsFalse()
    {
        var result = EventIntegrityChain.TryRead(new BsonString("tampered"), out var integrity);

        result.Should().BeFalse();
        integrity.Should().BeNull();
    }

    [Test]
    public void TryRead_SealedIntegrity_ReturnsIntegrity()
    {
        var sealedIntegrity = EventIntegrityChain.Seal([1, 2, 3], new Byte[32], IntegritySealMode.Append);

        var result = EventIntegrityChain.TryRead(sealedIntegrity.ToBsonDocument(), out var integrity);

        result.Should().BeTrue();
        integrity.Should().BeEquivalentTo(sealedIntegrity);
    }
}
