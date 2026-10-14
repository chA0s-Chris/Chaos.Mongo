// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity;

using Chaos.Mongo.EventStore.Integrity;
using FluentAssertions;
using MongoDB.Bson;
using NUnit.Framework;

public class PersonalDataCommitmentsTests
{
    private static readonly Byte[] Salt = Enumerable.Range(1, 32).Select(value => (Byte)value).ToArray();

    [Test]
    public void Canonicalize_ReplacesEveryCommittedValueByItsCommitment()
    {
        var document = CreateDocument();
        var commitments = PersonalDataCommitments.Commit(document, ["Name", "Address.City"]);
        commitments.Should().NotBeNull();

        var canonical = PersonalDataCommitments.Canonicalize(document, commitments);

        var expected = CreateDocumentValue();
        expected["Name"] = new BsonBinaryData(commitments[0].Commitment);
        expected["Address"]["City"] = new BsonBinaryData(commitments[1].Commitment);
        canonical.Should().Equal(expected.ToBson());
    }

    [Test]
    public void Commit_NoPathResolves_ReturnsNull()
    {
        var commitments = PersonalDataCommitments.Commit(CreateDocument(), ["Missing", "Address.Zip", "Name.Length"]);

        commitments.Should().BeNull();
    }

    [Test]
    public void Commit_NullValue_RecordsCommitmentToNullType()
    {
        var document = CreateDocument();

        var commitments = PersonalDataCommitments.Commit(document, ["Nickname"]);

        commitments.Should().ContainSingle();
        commitments[0].Path.Should().Be("Nickname");
        commitments[0].Commitment.Should().Equal(PersonalDataCommitments.ComputeCommitment(commitments[0].Salt, BsonType.Null, []));
    }

    [Test]
    public void Commit_ResolvedPaths_RecordsFreshSaltedCommitments()
    {
        var document = CreateDocument();

        var commitments = PersonalDataCommitments.Commit(document, ["Name", "Address.City", "Address.Zip"]);

        commitments.Should().HaveCount(2);
        commitments.Select(commitment => commitment.Path).Should().Equal("Name", "Address.City");
        commitments.Should().AllSatisfy(commitment => commitment.Salt.Should().HaveCount(PersonalDataCommitments.SaltLength));
        commitments[0].Salt.Should().NotEqual(commitments[1].Salt);
        foreach (var commitment in commitments)
        {
            RawBsonElements.TryLocate(document, commitment.Path, out var type, out var value).Should().BeTrue();
            commitment.Commitment.Should().Equal(PersonalDataCommitments.ComputeCommitment(commitment.Salt, type, document.AsSpan(value)));
        }
    }

    [Test]
    public void ComputeBinding_RecordedPaths_EncodesTagAndNulTerminatedPathsInOrder()
    {
        var binding = PersonalDataCommitments.ComputeBinding([CreateCommitment("Name"), CreateCommitment("Address.City")]);

        var expected = "Chaos.Mongo.EventStore/integrity/personal-data/v1"u8.ToArray()
                                                                            .Concat("Name"u8.ToArray()).Append((Byte)0)
                                                                            .Concat("Address.City"u8.ToArray()).Append((Byte)0)
                                                                            .ToArray();
        binding.Should().Equal(expected);
        PersonalDataCommitments.ComputeBinding([CreateCommitment("Address.City"), CreateCommitment("Name")]).Should().NotEqual(binding);
    }

    [Test]
    public void ComputeCommitment_DifferentInputs_ReturnDifferentValues()
    {
        Byte[] value = [1, 2, 3];
        var reference = PersonalDataCommitments.ComputeCommitment(Salt, BsonType.String, value);

        PersonalDataCommitments.ComputeCommitment(Enumerable.Reverse(Salt).ToArray(), BsonType.String, value).Should().NotEqual(reference);
        PersonalDataCommitments.ComputeCommitment(Salt, BsonType.Binary, value).Should().NotEqual(reference);
        PersonalDataCommitments.ComputeCommitment(Salt, BsonType.String, [1, 2, 4]).Should().NotEqual(reference);
    }

    [Test]
    public void ComputeCommitment_SameInput_ReturnsSameSha256Value()
    {
        Byte[] value = [1, 2, 3];

        var first = PersonalDataCommitments.ComputeCommitment(Salt, BsonType.String, value);
        var second = PersonalDataCommitments.ComputeCommitment(Salt, BsonType.String, value);

        first.Should().Equal(second).And.HaveCount(PersonalDataCommitments.CommitmentLength);
    }

    [Test]
    public void IsWellFormed_DuplicatePath_ReturnsFalse()
    {
        var commitments = new List<PersonalDataCommitment>
        {
            CreateCommitment("Name"),
            CreateCommitment("Name")
        };

        PersonalDataCommitments.IsWellFormed(commitments).Should().BeFalse();
    }

    [Test]
    public void IsWellFormed_EmptyPath_ReturnsFalse()
        => PersonalDataCommitments.IsWellFormed([CreateCommitment(String.Empty)]).Should().BeFalse();

    [Test]
    public void IsWellFormed_ErasedCommitment_ReturnsTrue()
        => PersonalDataCommitments.IsWellFormed([CreateCommitment("Name", null)]).Should().BeTrue();

    [Test]
    [TestCase("Address", "Address.City")]
    [TestCase("Address.City", "Address")]
    public void IsWellFormed_OverlappingPaths_ReturnsFalse(String first, String second)
        => PersonalDataCommitments.IsWellFormed([CreateCommitment(first), CreateCommitment(second)]).Should().BeFalse();

    [Test]
    public void IsWellFormed_SharedPrefixWithoutSeparator_ReturnsTrue()
        => PersonalDataCommitments.IsWellFormed([CreateCommitment("Address"), CreateCommitment("AddressLine")]).Should().BeTrue();

    [Test]
    public void IsWellFormed_ValidCommitments_ReturnsTrue()
        => PersonalDataCommitments.IsWellFormed([CreateCommitment("Name"), CreateCommitment("Address.City")]).Should().BeTrue();

    [Test]
    public void IsWellFormed_WrongCommitmentLength_ReturnsFalse()
        => PersonalDataCommitments.IsWellFormed([CreateCommitment("Name", Salt, new Byte[31])]).Should().BeFalse();

    [Test]
    public void IsWellFormed_WrongSaltLength_ReturnsFalse()
        => PersonalDataCommitments.IsWellFormed([CreateCommitment("Name", new Byte[16])]).Should().BeFalse();

    [Test]
    public void Verify_ChangedTypeWithSameBytes_ReturnsMismatch()
    {
        var document = new BsonDocument("Code", new BsonBinaryData([5, 0, 0, 0, 97, 98, 99, 100, 0]));
        var commitment = CommitSingle(document.ToBson(), "Code");
        var retyped = new BsonDocument("Code", "abcd");

        PersonalDataCommitments.Verify(retyped.ToBson(), commitment).Should().Be(PersonalDataVerification.Mismatch);
    }

    [Test]
    public void Verify_ChangedValueWithSalt_ReturnsMismatch()
    {
        var commitment = CommitSingle(CreateDocument(), "Name");
        var changed = CreateDocumentValue();
        changed["Name"] = "Mallory";

        PersonalDataCommitments.Verify(changed.ToBson(), commitment).Should().Be(PersonalDataVerification.Mismatch);
    }

    [Test]
    public void Verify_NullValueWithoutSalt_ReturnsErased()
    {
        var commitment = CommitSingle(CreateDocument(), "Name");
        commitment.Salt = null;
        var erased = CreateDocumentValue();
        erased["Name"] = BsonNull.Value;

        PersonalDataCommitments.Verify(erased.ToBson(), commitment).Should().Be(PersonalDataVerification.Erased);
    }

    [Test]
    public void Verify_PresentValueWithoutSalt_ReturnsNotErased()
    {
        var document = CreateDocument();
        var commitment = CommitSingle(document, "Name");
        commitment.Salt = null;

        PersonalDataCommitments.Verify(document, commitment).Should().Be(PersonalDataVerification.NotErased);
    }

    [Test]
    public void Verify_UnchangedValueWithSalt_ReturnsCommitted()
    {
        var document = CreateDocument();
        var commitment = CommitSingle(document, "Address.City");

        PersonalDataCommitments.Verify(document, commitment).Should().Be(PersonalDataVerification.Committed);
    }

    [Test]
    public void Verify_UnresolvedPath_ReturnsUnresolved()
    {
        var commitment = CreateCommitment("Missing");

        PersonalDataCommitments.Verify(CreateDocument(), commitment).Should().Be(PersonalDataVerification.Unresolved);
    }

    private static PersonalDataCommitment CommitSingle(Byte[] document, String path)
    {
        var commitments = PersonalDataCommitments.Commit(document, [path]);
        commitments.Should().ContainSingle();
        return commitments[0];
    }

    private static PersonalDataCommitment CreateCommitment(String path, Byte[]? salt = null, Byte[]? commitment = null)
        => new()
        {
            Path = path,
            Salt = salt,
            Commitment = commitment ?? new Byte[PersonalDataCommitments.CommitmentLength]
        };

    private static Byte[] CreateDocument() => CreateDocumentValue().ToBson();

    private static BsonDocument CreateDocumentValue()
        => new()
        {
            { "Name", "Alice" },
            { "Nickname", BsonNull.Value },
            {
                "Address", new BsonDocument
                {
                    { "City", "Berlin" },
                    { "Street", "Main" }
                }
            },
            { "Age", 42 }
        };
}
