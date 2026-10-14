// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity;

using Chaos.Mongo.EventStore.Integrity;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
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
    public void ComputeHash_WithPersonalData_DependsOnRecordedPathsAndTheirOrder()
    {
        Byte[] canonical = [1, 2, 3];
        var previousHash = new Byte[32];
        var name = new PersonalDataCommitment
        {
            Path = "Name",
            Commitment = new Byte[32]
        };
        var city = new PersonalDataCommitment
        {
            Path = "Address.City",
            Commitment = new Byte[32]
        };

        var plain = EventIntegrityChain.ComputeHash(canonical, previousHash);
        var withName = EventIntegrityChain.ComputeHash(canonical, [name], previousHash);
        var withBoth = EventIntegrityChain.ComputeHash(canonical, [name, city], previousHash);
        var reordered = EventIntegrityChain.ComputeHash(canonical, [city, name], previousHash);

        EventIntegrityChain.ComputeHash(canonical, null, previousHash).Should().Equal(plain);
        withName.Should().NotEqual(plain);
        withBoth.Should().NotEqual(withName);
        reordered.Should().NotEqual(withBoth);
        EventIntegrityChain.ComputeHash(canonical, [name, city], previousHash).Should().Equal(withBoth);
    }

    [OneTimeSetUp]
    public void RegisterClassMaps()
        => new MongoBuilder(new ServiceCollection()).WithEventStore<ChainFormatAggregate>(es => es.WithEvent<ChainFormatEvent>());

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
    public void Seal_UnresolvedPersonalDataPaths_RecordsNoPersonalData()
    {
        var document = new BsonDocument("Name", "Alice").ToBson();
        var previousHash = EventIntegrityChain.ComputeGenesis("Ledger", Guid.NewGuid());

        var integrity = EventIntegrityChain.Seal(document, ["Missing"], previousHash, IntegritySealMode.Append);

        integrity.PersonalData.Should().BeNull();
        integrity.Hash.Should().Equal(EventIntegrityChain.ComputeHash(document, previousHash));
        integrity.ToBsonDocument().Names.Should().BeEquivalentTo("FormatVersion", "Algorithm", "PreviousHash", "Hash", "SealMode");
    }

    [Test]
    public void Seal_WithPersonalDataPaths_RecordsCommitmentsAndHashesCanonicalForm()
    {
        var document = new BsonDocument
        {
            { "Name", "Alice" },
            { "Address", new BsonDocument("City", "Berlin") }
        }.ToBson();
        var previousHash = EventIntegrityChain.ComputeGenesis("Ledger", Guid.NewGuid());

        var integrity = EventIntegrityChain.Seal(document, ["Name", "Address.City", "Missing"], previousHash, IntegritySealMode.Retroactive);

        integrity.PersonalData.Should().HaveCount(2);
        integrity.PersonalData!.Select(commitment => commitment.Path).Should().Equal("Name", "Address.City");
        integrity.SealMode.Should().Be(IntegritySealMode.Retroactive);
        var canonical = PersonalDataCommitments.Canonicalize(document, integrity.PersonalData);
        integrity.Hash.Should().Equal(EventIntegrityChain.ComputeHash(canonical, integrity.PersonalData, previousHash));
        integrity.Hash.Should().NotEqual(EventIntegrityChain.ComputeHash(canonical, previousHash), "the recorded paths are bound into the hash");
        integrity.Hash.Should().NotEqual(EventIntegrityChain.ComputeHash(document, previousHash));
    }

    [Test]
    public void TryRead_ErasedCommitment_ReadsEntryWithoutSalt()
    {
        var document = EventIntegrityChain.Seal([1, 2, 3], new Byte[32], IntegritySealMode.Append).ToBsonDocument();
        document["PersonalData"] = new BsonArray
        {
            new BsonDocument
            {
                { "Path", "Name" },
                { "Commitment", new BsonBinaryData(new Byte[32]) }
            }
        };

        var result = EventIntegrityChain.TryRead(document, out var integrity);

        result.Should().BeTrue();
        integrity!.PersonalData.Should().ContainSingle();
        integrity.PersonalData![0].Path.Should().Be("Name");
        integrity.PersonalData[0].Salt.Should().BeNull();
        integrity.PersonalData[0].Commitment.Should().Equal(new Byte[32]);
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

public sealed class ChainFormatAggregate : Aggregate;

public sealed class ChainFormatEvent : Event<ChainFormatAggregate>
{
    public override void Execute(ChainFormatAggregate aggregate) { }
}
