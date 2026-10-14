// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity;

using Chaos.Mongo.EventStore.Integrity;
using FluentAssertions;
using NUnit.Framework;

/// <summary>
/// The expected values were computed with an independent recursive implementation of RFC 9162, section 2.1, over the
/// leaf data used by the Certificate Transparency test vectors.
/// </summary>
public class MerkleTreeTests
{
    private static readonly Byte[][] LeafData =
    [
        [],
        [0x00],
        [0x10],
        [0x20, 0x21],
        [0x30, 0x31],
        [0x40, 0x41, 0x42, 0x43],
        [0x50, 0x51, 0x52, 0x53, 0x54, 0x55, 0x56, 0x57],
        [0x60, 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6a, 0x6b, 0x6c, 0x6d, 0x6e, 0x6f]
    ];

    [Test]
    public void Build_NoLeaves_ThrowsArgumentException()
    {
        var act = () => MerkleTree.Build([]);

        act.Should().Throw<ArgumentException>();
    }

    [TestCase(1, "6e340b9cffb37a989ca544e6bb780a2c78901d3fb33738768511a30617afa01d")]
    [TestCase(2, "fac54203e7cc696cf0dfcb42c92a1d9dbaf70ad9e621f4bd8d98662f00e3c125")]
    [TestCase(3, "aeb6bcfe274b70a14fb067a5e5578264db0fa9b51af5e0ba159158f329e06e77")]
    [TestCase(5, "4e3bbb1f7b478dcfe71fb631631519a3bca12c9aefca1612bfce4c13a86264d4")]
    [TestCase(7, "ddb89be403809e325750d3d263cd78929c2942b7942a34b77e122c9594a74c8c")]
    [TestCase(8, "5dc9da79a70659a9ad559cb701ded9a2ab9d823aad2f4960cfe370eff4604328")]
    public void Build_TestVectorLeaves_ReturnsExpectedRoot(Int32 leafCount, String expectedRoot)
    {
        var tree = BuildOver(leafCount);

        tree.LeafCount.Should().Be(leafCount);
        ToHex(tree.Root).Should().Be(expectedRoot);
    }

    [TestCase(-1)]
    [TestCase(3)]
    public void GetInclusionProof_IndexOutsideTree_ThrowsArgumentOutOfRangeException(Int32 leafIndex)
    {
        var act = () => BuildOver(3).GetInclusionProof(leafIndex);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void GetInclusionProof_SingleLeaf_ReturnsEmptyProof()
        => BuildOver(1).GetInclusionProof(0).Should().BeEmpty();

    [TestCase(7, 6, "0ebc5d3437fbe2db158b9f126a1d118e308181031d0a949f8dededebc558ef6a", "d37ee418976dd95753c1c73862b9398fa2a2cf9b4ff0fdfe8b30cd95209614b7")]
    [TestCase(5, 4, "d37ee418976dd95753c1c73862b9398fa2a2cf9b4ff0fdfe8b30cd95209614b7")]
    [TestCase(8,
              0,
              "96a296d224f285c67bee93c30f8a309157f0daa35dc5b87e410b78630a09cfc7",
              "5f083f0a1a33ca076a95279832580db3e0ef4584bdff1f54c8a360f50de3031e",
              "6b47aaf29ee3c2af9af889bc1fb9254dabd31177f16232dd6aab035ca39bf6e4")]
    public void GetInclusionProof_TestVectorLeaves_ReturnsExpectedPath(Int32 leafCount, Int32 leafIndex, params String[] expectedPath)
    {
        var proof = BuildOver(leafCount).GetInclusionProof(leafIndex);

        proof.Select(ToHex).Should().Equal(expectedPath);
    }

    [Test]
    public void VerifyInclusion_EveryLeafOfTreesUpTo33Leaves_Succeeds()
    {
        for (var size = 1; size <= 33; size++)
        {
            var leaves = CreateLeafHashes(size);
            var tree = MerkleTree.Build(leaves);
            for (var index = 0; index < size; index++)
            {
                MerkleTree.VerifyInclusion(leaves[index], index, size, tree.GetInclusionProof(index), tree.Root)
                          .Should().BeTrue($"leaf {index} of a tree with {size} leaves is included");
            }
        }
    }

    [Test]
    public void VerifyInclusion_TamperedInput_Fails()
    {
        var leaves = CreateLeafHashes(11);
        var tree = MerkleTree.Build(leaves);
        var proof = tree.GetInclusionProof(6);

        MerkleTree.VerifyInclusion(leaves[5], 6, 11, proof, tree.Root).Should().BeFalse();
        MerkleTree.VerifyInclusion(leaves[6], 5, 11, proof, tree.Root).Should().BeFalse();
        MerkleTree.VerifyInclusion(leaves[6], 6, 7, proof, tree.Root).Should().BeFalse();
        MerkleTree.VerifyInclusion(leaves[6], 6, 11, proof, leaves[0]).Should().BeFalse();
        MerkleTree.VerifyInclusion(leaves[6], 6, 11, [.. proof, leaves[0]], tree.Root).Should().BeFalse();
        MerkleTree.VerifyInclusion(leaves[6], 11, 11, proof, tree.Root).Should().BeFalse();
        MerkleTree.VerifyInclusion(leaves[6], -1, 11, proof, tree.Root).Should().BeFalse();
    }

    private static MerkleTree BuildOver(Int32 leafCount)
        => MerkleTree.Build(LeafData.Take(leafCount).Select(data => MerkleTree.HashLeaf(data)).ToList());

    private static IReadOnlyList<Byte[]> CreateLeafHashes(Int32 count)
        => Enumerable.Range(0, count).Select(index => MerkleTree.HashLeaf(BitConverter.GetBytes(index))).ToList();

    private static String ToHex(Byte[] value) => Convert.ToHexString(value).ToLowerInvariant();
}
