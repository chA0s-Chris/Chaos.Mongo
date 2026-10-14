// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity;

using Chaos.Mongo.EventStore.Integrity;
using FluentAssertions;
using NUnit.Framework;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// The expected values were computed with an independent implementation of the byte layouts.
/// </summary>
public class AnchorEncodingTests
{
    private static readonly Byte[] ChainHash = Enumerable.Range(0, 32).Select(value => (Byte)value).ToArray();

    [Test]
    public void ComputeAnchorDigest_FirstAnchor_ReturnsExpectedDigest()
    {
        var root = SHA256.HashData(Encoding.ASCII.GetBytes("root"));

        var digest = AnchorEncoding.ComputeAnchorDigest(1, 1, new Byte[32], root);

        ToHex(digest).Should().Be("748e4602a9ae6ba6d8a5a9aca59fac42277d9ae7a01bc52ad8f471f91cc1fab2");
    }

    [Test]
    public void ComputeAnchorDigest_LaterAnchor_ReturnsExpectedDigest()
    {
        var root = SHA256.HashData(Encoding.ASCII.GetBytes("root"));

        var digest = AnchorEncoding.ComputeAnchorDigest(1, 258, Enumerable.Repeat((Byte)0xAB, 32).ToArray(), root);

        ToHex(digest).Should().Be("b5ea72bca60501cd5a3eacbebb65808750521fe17c4ffcfdcca7dc5c22c8b3b0");
    }

    [TestCase(31, 32)]
    [TestCase(32, 33)]
    public void ComputeAnchorDigest_WrongHashLength_ThrowsArgumentException(Int32 previousLength, Int32 rootLength)
    {
        var act = () => AnchorEncoding.ComputeAnchorDigest(1, 1, new Byte[previousLength], new Byte[rootLength]);

        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void EncodeLeaf_StreamHead_ReturnsExpectedLayoutAndLeafHash()
    {
        var candidate = new AnchorCandidate("Ledger", Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), 7, ChainHash, DateTime.UnixEpoch);

        var leaf = AnchorEncoding.EncodeLeaf(candidate);

        ToHex(leaf).Should().Be("000000064c656467657200112233445566778899aabbccddeeff0000000000000007" +
                                "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
        ToHex(MerkleTree.HashLeaf(leaf)).Should().Be("7c47f0da5592fdaa3234fbfffba3e01e6ccfdee69df341c70783d853e036c2fb");
    }

    [Test]
    public void EncodeLeaf_WrongHashLength_ThrowsArgumentException()
    {
        var act = () => AnchorEncoding.EncodeLeaf(new AnchorCandidate("Ledger", Guid.NewGuid(), 1, [1, 2, 3], DateTime.UnixEpoch));

        act.Should().Throw<ArgumentException>();
    }

    private static String ToHex(Byte[] value) => Convert.ToHexString(value).ToLowerInvariant();
}
