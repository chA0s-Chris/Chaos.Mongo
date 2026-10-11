// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using System.Security.Cryptography;

/// <summary>
/// A SHA-256 Merkle tree following the Merkle Tree Hash of RFC 9162, section 2.1.
/// </summary>
/// <remarks>
/// Leaf hashes are prefixed with <c>0x00</c> and node hashes with <c>0x01</c>. Building the tree level by level and
/// promoting an unpaired last node unchanged yields the same tree as splitting at the largest power of two smaller
/// than the leaf count, without duplicating nodes.
/// </remarks>
internal sealed class MerkleTree
{
    private const Byte LeafPrefix = 0x00;
    private const Byte NodePrefix = 0x01;
    private readonly List<Byte[][]> _levels;

    private MerkleTree(List<Byte[][]> levels)
    {
        _levels = levels;
    }

    /// <summary>
    /// Gets the number of leaves.
    /// </summary>
    public Int32 LeafCount => _levels[0].Length;

    /// <summary>
    /// Gets the root hash.
    /// </summary>
    public Byte[] Root => _levels[^1][0];

    /// <summary>
    /// Builds the tree over leaf hashes.
    /// </summary>
    /// <param name="leafHashes">The leaf hashes, created with <see cref="HashLeaf"/>, in leaf order.</param>
    /// <returns>The tree.</returns>
    /// <exception cref="ArgumentException">Thrown when there are no leaves.</exception>
    public static MerkleTree Build(IReadOnlyList<Byte[]> leafHashes)
    {
        ArgumentNullException.ThrowIfNull(leafHashes);
        if (leafHashes.Count == 0)
        {
            throw new ArgumentException("A Merkle tree needs at least one leaf.", nameof(leafHashes));
        }

        var levels = new List<Byte[][]>
        {
            leafHashes.ToArray()
        };
        while (levels[^1].Length > 1)
        {
            var level = levels[^1];
            var parents = new Byte[(level.Length + 1) / 2][];
            for (var index = 0; index < parents.Length; index++)
            {
                var left = 2 * index;
                parents[index] = left + 1 < level.Length ? HashNode(level[left], level[left + 1]) : level[left];
            }

            levels.Add(parents);
        }

        return new MerkleTree(levels);
    }

    /// <summary>
    /// Hashes the data of a leaf.
    /// </summary>
    /// <param name="leafData">The encoded leaf.</param>
    /// <returns>The leaf hash.</returns>
    public static Byte[] HashLeaf(ReadOnlySpan<Byte> leafData)
    {
        var input = new Byte[leafData.Length + 1];
        input[0] = LeafPrefix;
        leafData.CopyTo(input.AsSpan(1));
        return SHA256.HashData(input);
    }

    /// <summary>
    /// Hashes two child nodes.
    /// </summary>
    /// <param name="left">The left child hash.</param>
    /// <param name="right">The right child hash.</param>
    /// <returns>The node hash.</returns>
    public static Byte[] HashNode(ReadOnlySpan<Byte> left, ReadOnlySpan<Byte> right)
    {
        var input = new Byte[left.Length + right.Length + 1];
        input[0] = NodePrefix;
        left.CopyTo(input.AsSpan(1));
        right.CopyTo(input.AsSpan(1 + left.Length));
        return SHA256.HashData(input);
    }

    /// <summary>
    /// Verifies an inclusion proof as described in RFC 9162, section 2.1.3.2.
    /// </summary>
    /// <param name="leafHash">The hash of the leaf.</param>
    /// <param name="leafIndex">The zero-based index of the leaf.</param>
    /// <param name="treeSize">The number of leaves of the tree.</param>
    /// <param name="proof">The inclusion proof.</param>
    /// <param name="root">The expected root hash.</param>
    /// <returns><c>true</c> when the proof leads from the leaf to the root.</returns>
    public static Boolean VerifyInclusion(ReadOnlySpan<Byte> leafHash, Int64 leafIndex, Int64 treeSize, IReadOnlyList<Byte[]> proof, ReadOnlySpan<Byte> root)
    {
        ArgumentNullException.ThrowIfNull(proof);
        if (leafIndex < 0 || leafIndex >= treeSize)
        {
            return false;
        }

        var fn = leafIndex;
        var sn = treeSize - 1;
        var r = leafHash.ToArray();
        foreach (var p in proof)
        {
            if (sn == 0)
            {
                return false;
            }

            if ((fn & 1) == 1 || fn == sn)
            {
                r = HashNode(p, r);
                if ((fn & 1) == 0)
                {
                    do
                    {
                        fn >>= 1;
                        sn >>= 1;
                    } while ((fn & 1) == 0 && fn != 0);
                }
            }
            else
            {
                r = HashNode(r, p);
            }

            fn >>= 1;
            sn >>= 1;
        }

        return sn == 0 && r.AsSpan().SequenceEqual(root);
    }

    /// <summary>
    /// Gets the inclusion proof of a leaf, as defined in RFC 9162, section 2.1.3.1: the sibling hashes from the leaf
    /// up to the root.
    /// </summary>
    /// <param name="leafIndex">The zero-based index of the leaf.</param>
    /// <returns>The proof.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the index is outside the tree.</exception>
    public IReadOnlyList<Byte[]> GetInclusionProof(Int32 leafIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(leafIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(leafIndex, LeafCount);

        var proof = new List<Byte[]>();
        var index = leafIndex;
        for (var depth = 0; depth < _levels.Count - 1; depth++)
        {
            var level = _levels[depth];
            var sibling = index ^ 1;
            if (sibling < level.Length)
            {
                proof.Add(level[sibling]);
            }

            index >>= 1;
        }

        return proof;
    }
}
