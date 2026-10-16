// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// The byte layouts of anchor leaves and anchor digests, as docs/tamper-evidence-specification.md defines them for the
/// stable format version 1.
/// </summary>
internal static class AnchorEncoding
{
    /// <summary>
    /// The current anchor format version.
    /// </summary>
    public const Int32 FormatVersion = 1;

    /// <summary>
    /// The name of the hash algorithm stored on anchors.
    /// </summary>
    public const String HashAlgorithmName = "SHA-256";

    /// <summary>
    /// The length of a SHA-256 hash.
    /// </summary>
    public const Int32 HashLength = 32;

    /// <summary>
    /// Computes the digest that authorities stamp: SHA-256 over the 4-byte big-endian format version, the 8-byte
    /// big-endian sequence number, the previous anchor's digest and the Merkle root.
    /// </summary>
    /// <param name="formatVersion">The anchor format version.</param>
    /// <param name="sequence">The anchor sequence number.</param>
    /// <param name="previousDigest">The previous anchor's digest, or 32 zero bytes for the first anchor.</param>
    /// <param name="merkleRoot">The Merkle root over the anchor's leaves.</param>
    /// <returns>The anchor digest.</returns>
    public static Byte[] ComputeAnchorDigest(Int32 formatVersion, Int64 sequence, ReadOnlySpan<Byte> previousDigest, ReadOnlySpan<Byte> merkleRoot)
    {
        RequireHash(previousDigest, nameof(previousDigest));
        RequireHash(merkleRoot, nameof(merkleRoot));

        Span<Byte> input = stackalloc Byte[4 + 8 + HashLength + HashLength];
        BinaryPrimitives.WriteInt32BigEndian(input, formatVersion);
        BinaryPrimitives.WriteInt64BigEndian(input[4..], sequence);
        previousDigest.CopyTo(input[12..]);
        merkleRoot.CopyTo(input[(12 + HashLength)..]);
        return SHA256.HashData(input);
    }

    /// <summary>
    /// Encodes a leaf: the UTF-8 aggregate type name with a 4-byte big-endian length prefix, the 16 aggregate
    /// identifier bytes in RFC 9562 order, the 8-byte big-endian version and the 32-byte chain hash.
    /// </summary>
    /// <param name="candidate">The stream head.</param>
    /// <returns>The encoded leaf.</returns>
    public static Byte[] EncodeLeaf(AnchorCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        RequireHash(candidate.Hash, nameof(candidate));

        var typeLength = Encoding.UTF8.GetByteCount(candidate.AggregateType);
        var leaf = new Byte[4 + typeLength + 16 + 8 + HashLength];
        BinaryPrimitives.WriteInt32BigEndian(leaf, typeLength);
        Encoding.UTF8.GetBytes(candidate.AggregateType, leaf.AsSpan(4));
        candidate.AggregateId.TryWriteBytes(leaf.AsSpan(4 + typeLength), true, out _);
        BinaryPrimitives.WriteInt64BigEndian(leaf.AsSpan(4 + typeLength + 16), candidate.Version);
        candidate.Hash.CopyTo(leaf.AsSpan(4 + typeLength + 16 + 8));
        return leaf;
    }

    private static void RequireHash(ReadOnlySpan<Byte> hash, String parameterName)
    {
        if (hash.Length != HashLength)
        {
            throw new ArgumentException($"A SHA-256 hash has {HashLength} bytes.", parameterName);
        }
    }
}
