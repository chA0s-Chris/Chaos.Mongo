// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using System.Diagnostics.CodeAnalysis;
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
        => ComputeHash(canonicalDocument, null, previousHash);

    /// <summary>
    /// Computes the chain hash of an event with recorded personal data. The hash covers the canonical form, then
    /// the binding of the recorded paths, then the predecessor's hash. Binding the paths distinguishes the canonical
    /// form from a document that genuinely stores a commitment's bytes in place of a value without recording the
    /// path, which would otherwise verify after the value was destroyed.
    /// </summary>
    /// <param name="canonicalDocument">
    /// The event's BSON without the <c>_integrity</c> element, with every recorded value replaced by its commitment.
    /// </param>
    /// <param name="personalData">The recorded commitments, or <c>null</c> when none is recorded.</param>
    /// <param name="previousHash">The predecessor's hash, or the genesis value for version 1.</param>
    /// <returns>The chain hash.</returns>
    public static Byte[] ComputeHash(ReadOnlySpan<Byte> canonicalDocument,
                                     IReadOnlyList<PersonalDataCommitment>? personalData,
                                     ReadOnlySpan<Byte> previousHash)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(canonicalDocument);
        if (personalData is not null)
        {
            hash.AppendData(PersonalDataCommitments.ComputeBinding(personalData));
        }

        hash.AppendData(previousHash);
        return hash.GetHashAndReset();
    }

    /// <summary>
    /// Creates the integrity data that seals an event without personal data into its stream.
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

    /// <summary>
    /// Creates the integrity data that seals an event into its stream, committing to the values of the given
    /// personal data paths. The hash covers the canonical form, in which every recorded value is replaced by its
    /// commitment, followed by the binding of the recorded paths. Paths that do not resolve in the document are not
    /// recorded.
    /// </summary>
    /// <param name="document">The event's BSON without the <c>_integrity</c> element.</param>
    /// <param name="personalDataPaths">The dotted element paths marked as personal data for the event's type.</param>
    /// <param name="previousHash">The predecessor's hash, or the genesis value for version 1.</param>
    /// <param name="sealMode">How the event is sealed.</param>
    /// <returns>The integrity data.</returns>
    public static EventIntegrity Seal(Byte[] document, IReadOnlyList<String> personalDataPaths, Byte[] previousHash, IntegritySealMode sealMode)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(personalDataPaths);

        var commitments = personalDataPaths.Count == 0 ? null : PersonalDataCommitments.Commit(document, personalDataPaths);
        var canonicalDocument = commitments is null ? document : PersonalDataCommitments.Canonicalize(document, commitments);
        return new EventIntegrity
        {
            Algorithm = Algorithm,
            FormatVersion = FormatVersion,
            Hash = ComputeHash(canonicalDocument, commitments, previousHash),
            PersonalData = commitments,
            PreviousHash = previousHash,
            SealMode = sealMode
        };
    }

    /// <summary>
    /// Reads stored integrity data. Malformed data, for example after tampering, yields <c>false</c>
    /// instead of an exception, so callers can report it like any other integrity failure.
    /// </summary>
    /// <param name="value">The value of the <c>_integrity</c> element.</param>
    /// <param name="integrity">The integrity data when it could be read.</param>
    /// <returns><c>true</c> when the value is well-formed integrity data; otherwise <c>false</c>.</returns>
    public static Boolean TryRead(BsonValue value, [NotNullWhen(true)] out EventIntegrity? integrity)
    {
        integrity = null;
        if (value is not BsonDocument document)
        {
            return false;
        }

        try
        {
            integrity = BsonSerializer.Deserialize<EventIntegrity>(document);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or BsonException)
        {
            return false;
        }
    }
}
