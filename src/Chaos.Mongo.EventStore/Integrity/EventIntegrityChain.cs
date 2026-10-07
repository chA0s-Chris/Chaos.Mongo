// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Defines the hash chain that seals the events of a stream.
/// </summary>
/// <remarks>
/// Each event's hash covers its canonical form, the stored BSON without the <c>_integrity</c> element,
/// followed by the hash of its predecessor. Version 1 chains from a genesis value derived from the
/// aggregate type and identifier.
/// </remarks>
internal static class EventIntegrityChain
{
    /// <summary>
    /// The name of the hash algorithm used by the current integrity format.
    /// </summary>
    public const String Algorithm = "SHA-256";

    /// <summary>
    /// The reserved element name that stores the integrity data of an event.
    /// </summary>
    public const String ElementName = "_integrity";

    /// <summary>
    /// The version of the current integrity format.
    /// </summary>
    public const Int32 FormatVersion = 1;

    private static readonly Byte[] GenesisTag = "Chaos.Mongo.EventStore/integrity/genesis/v1"u8.ToArray();

    /// <summary>
    /// Computes the genesis value that version 1 of a stream chains from.
    /// </summary>
    /// <param name="aggregateType">The aggregate type name stored in the events.</param>
    /// <param name="aggregateId">The aggregate identifier.</param>
    /// <returns>The genesis hash.</returns>
    public static Byte[] ComputeGenesis(String aggregateType, Guid aggregateId)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(GenesisTag);
        hash.AppendData(Encoding.UTF8.GetBytes(aggregateType));
        hash.AppendData([0]);
        hash.AppendData(aggregateId.ToByteArray(true));
        return hash.GetHashAndReset();
    }

    /// <summary>
    /// Computes the chain hash of an event.
    /// </summary>
    /// <param name="canonicalDocument">The event's BSON without the <c>_integrity</c> element.</param>
    /// <param name="previousHash">The predecessor's hash, or the genesis value for version 1.</param>
    /// <returns>The chain hash.</returns>
    public static Byte[] ComputeHash(ReadOnlySpan<Byte> canonicalDocument, ReadOnlySpan<Byte> previousHash)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(canonicalDocument);
        hash.AppendData(previousHash);
        return hash.GetHashAndReset();
    }

    /// <summary>
    /// Creates the integrity data that seals an event into its stream.
    /// </summary>
    /// <param name="canonicalDocument">The event's BSON without the <c>_integrity</c> element.</param>
    /// <param name="previousHash">The predecessor's hash, or the genesis value for version 1.</param>
    /// <param name="sealMode">How the event is sealed.</param>
    /// <returns>The integrity data.</returns>
    public static EventIntegrity Seal(ReadOnlySpan<Byte> canonicalDocument, Byte[] previousHash, IntegritySealMode sealMode)
        => new()
        {
            Algorithm = Algorithm,
            FormatVersion = FormatVersion,
            Hash = ComputeHash(canonicalDocument, previousHash),
            PreviousHash = previousHash,
            SealMode = sealMode
        };
}
