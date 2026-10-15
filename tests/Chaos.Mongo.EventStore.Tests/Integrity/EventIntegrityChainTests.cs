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

        var first = EventIntegrityChain.ComputeHash(document, new Byte[32], IntegritySealMode.Append);
        var second = EventIntegrityChain.ComputeHash(document, Enumerable.Repeat((Byte)1, 32).ToArray(), IntegritySealMode.Append);

        first.Should().NotEqual(second);
    }

    [Test]
    public void ComputeHash_DifferentSealModes_ReturnsDifferentValues()
    {
        Byte[] document = [1, 2, 3];
        var previousHash = new Byte[32];

        var appended = EventIntegrityChain.ComputeHash(document, previousHash, IntegritySealMode.Append);
        var retroactive = EventIntegrityChain.ComputeHash(document, previousHash, IntegritySealMode.Retroactive);

        appended.Should().NotEqual(retroactive);
    }

    [Test]
    public void ComputeHash_KnownInputs_ReturnsIndependentlyComputedValues()
    {
        // Expected values computed outside .NET: SHA-256(canonical ‖ previous ‖ mode) and
        // SHA-256(canonical ‖ tag ‖ "Name\0" ‖ "Address.City\0" ‖ previous ‖ mode), with mode 0x01 for Append
        // and 0x02 for Retroactive.
        Byte[] canonical = [1, 2, 3];
        var previousHash = new Byte[32];
        List<PersonalDataCommitment> personalData =
        [
            new()
            {
                Path = "Name",
                Commitment = new Byte[32]
            },
            new()
            {
                Path = "Address.City",
                Commitment = new Byte[32]
            }
        ];

        ToHex(EventIntegrityChain.ComputeHash(canonical, previousHash, IntegritySealMode.Append))
            .Should().Be("a12e9198a440ff040655cf2f69ece7e8fa403690a3b27cffa3cf1786a753a77f");
        ToHex(EventIntegrityChain.ComputeHash(canonical, previousHash, IntegritySealMode.Retroactive))
            .Should().Be("439f7d3c5be939848e577f7213c2ec1fa6aaa8344ce69347c9d7262202c827f3");
        ToHex(EventIntegrityChain.ComputeHash(canonical, personalData, previousHash, IntegritySealMode.Append))
            .Should().Be("b9e569762cbdab3b5ac254d6513a37766ed2fb321fe988df738eeff4099a0d69");
        ToHex(EventIntegrityChain.ComputeHash(canonical, personalData, previousHash, IntegritySealMode.Retroactive))
            .Should().Be("806fe704863e4e124cdf52cf5c363bf1f7a9e00548b324a9e01b1472012827ba");
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

        const IntegritySealMode mode = IntegritySealMode.Append;
        var plain = EventIntegrityChain.ComputeHash(canonical, previousHash, mode);
        var withName = EventIntegrityChain.ComputeHash(canonical, [name], previousHash, mode);
        var withBoth = EventIntegrityChain.ComputeHash(canonical, [name, city], previousHash, mode);
        var reordered = EventIntegrityChain.ComputeHash(canonical, [city, name], previousHash, mode);

        EventIntegrityChain.ComputeHash(canonical, null, previousHash, mode).Should().Equal(plain);
        withName.Should().NotEqual(plain);
        withBoth.Should().NotEqual(withName);
        reordered.Should().NotEqual(withBoth);
        EventIntegrityChain.ComputeHash(canonical, [name, city], previousHash, mode).Should().Equal(withBoth);
    }

    [Test]
    public void EncodeSealMode_UndefinedMode_ThrowsArgumentOutOfRangeException()
    {
        var act = () => EventIntegrityChain.EncodeSealMode((IntegritySealMode)7);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("sealMode");
    }

    [OneTimeSetUp]
    public void RegisterClassMaps()
        => new MongoBuilder(new ServiceCollection()).WithEventStore<ChainFormatAggregate>(es => es.WithEvent<ChainFormatEvent>());

    [Test]
    public void Seal_DottedSiblingName_HashCoversTheSiblingValue()
    {
        var document = new BsonDocument
        {
            { "Recipient.Name", "alias" },
            { "Recipient", new BsonDocument("Name", "real") }
        };
        var previousHash = EventIntegrityChain.ComputeGenesis("Ledger", Guid.NewGuid());

        var integrity = EventIntegrityChain.Seal(document.ToBson(), ["Recipient.Name"], previousHash, IntegritySealMode.Append);

        var changed = document.DeepClone().AsBsonDocument;
        changed["Recipient.Name"] = "forged";
        var changedCanonical = PersonalDataCommitments.Canonicalize(changed.ToBson(), integrity.PersonalData!);
        EventIntegrityChain.ComputeHash(changedCanonical, integrity.PersonalData, previousHash, IntegritySealMode.Append).Should().NotEqual(integrity.Hash);
        var erased = document.DeepClone().AsBsonDocument;
        erased["Recipient"]["Name"] = BsonNull.Value;
        var erasedCanonical = PersonalDataCommitments.Canonicalize(erased.ToBson(), integrity.PersonalData!);
        EventIntegrityChain.ComputeHash(erasedCanonical, integrity.PersonalData, previousHash, IntegritySealMode.Append).Should().Equal(integrity.Hash);
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
        integrity.Hash.Should().Equal(EventIntegrityChain.ComputeHash(document, previousHash, IntegritySealMode.Retroactive));
        integrity.SealMode.Should().Be(IntegritySealMode.Retroactive);
    }

    [Test]
    public void Seal_UnresolvedPersonalDataPaths_RecordsNoPersonalData()
    {
        var document = new BsonDocument("Name", "Alice").ToBson();
        var previousHash = EventIntegrityChain.ComputeGenesis("Ledger", Guid.NewGuid());

        var integrity = EventIntegrityChain.Seal(document, ["Missing"], previousHash, IntegritySealMode.Append);

        integrity.PersonalData.Should().BeNull();
        integrity.Hash.Should().Equal(EventIntegrityChain.ComputeHash(document, previousHash, IntegritySealMode.Append));
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
        const IntegritySealMode mode = IntegritySealMode.Retroactive;
        integrity.Hash.Should().Equal(EventIntegrityChain.ComputeHash(canonical, integrity.PersonalData, previousHash, mode));
        integrity.Hash.Should().NotEqual(EventIntegrityChain.ComputeHash(canonical, previousHash, mode), "the recorded paths are bound into the hash");
        integrity.Hash.Should().NotEqual(EventIntegrityChain.ComputeHash(document, previousHash, mode));
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

    private static String ToHex(Byte[] value) => Convert.ToHexString(value).ToLowerInvariant();
}

public sealed class ChainFormatAggregate : Aggregate;

public sealed class ChainFormatEvent : Event<ChainFormatAggregate>
{
    public override void Execute(ChainFormatAggregate aggregate) { }
}
