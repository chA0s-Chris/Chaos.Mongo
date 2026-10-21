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
/// followed by the hash of its predecessor and its seal mode. Version 1 chains from a genesis value derived
/// from the aggregate type stored in the version-1 event and the aggregate identifier.
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
    /// <param name="aggregateType">
    /// The aggregate type name stored in the version-1 event. It is read from the stored bytes rather than taken
    /// from the current class name, so a renamed aggregate class does not change the genesis value.
    /// </param>
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
    /// <param name="sealMode">How the event is sealed.</param>
    /// <returns>The chain hash.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="sealMode"/> is undefined.</exception>
    public static Byte[] ComputeHash(ReadOnlySpan<Byte> canonicalDocument, ReadOnlySpan<Byte> previousHash, IntegritySealMode sealMode)
        => ComputeHash(canonicalDocument, null, previousHash, sealMode);

    /// <summary>
    /// Computes the chain hash of an event with recorded personal data. The hash covers the canonical form, then
    /// the binding of the recorded paths, then the predecessor's hash, then one byte for the seal mode. Binding the
    /// paths distinguishes the canonical form from a document that genuinely stores a commitment's bytes in place of
    /// a value without recording the path, which would otherwise verify after the value was destroyed. Binding the
    /// seal mode lets anchored heads authenticate how every earlier event was sealed.
    /// </summary>
    /// <param name="canonicalDocument">
    /// The event's BSON without the <c>_integrity</c> element, with every recorded value replaced by its commitment.
    /// </param>
    /// <param name="personalData">The recorded commitments, or <c>null</c> when none is recorded.</param>
    /// <param name="previousHash">The predecessor's hash, or the genesis value for version 1.</param>
    /// <param name="sealMode">How the event is sealed.</param>
    /// <returns>The chain hash.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="sealMode"/> is undefined.</exception>
    public static Byte[] ComputeHash(ReadOnlySpan<Byte> canonicalDocument,
                                     IReadOnlyList<PersonalDataCommitment>? personalData,
                                     ReadOnlySpan<Byte> previousHash,
                                     IntegritySealMode sealMode)
    {
        var modeByte = EncodeSealMode(sealMode);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(canonicalDocument);
        if (personalData is not null)
        {
            hash.AppendData(PersonalDataCommitments.ComputeBinding(personalData));
        }

        hash.AppendData(previousHash);
        hash.AppendData([modeByte]);
        return hash.GetHashAndReset();
    }

    /// <summary>
    /// Encodes a seal mode as the byte that the chain hash covers: <c>0x01</c> for
    /// <see cref="IntegritySealMode.Append"/> and <c>0x02</c> for <see cref="IntegritySealMode.Retroactive"/>.
    /// </summary>
    /// <param name="sealMode">The seal mode.</param>
    /// <returns>The encoded seal mode.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="sealMode"/> is undefined.</exception>
    public static Byte EncodeSealMode(IntegritySealMode sealMode)
        => sealMode switch
        {
            IntegritySealMode.Append => 0x01,
            IntegritySealMode.Retroactive => 0x02,
            _ => throw new ArgumentOutOfRangeException(nameof(sealMode), sealMode, "The seal mode is not defined.")
        };

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
            Hash = ComputeHash(canonicalDocument, previousHash, sealMode),
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
            Hash = ComputeHash(canonicalDocument, commitments, previousHash, sealMode),
            PersonalData = commitments,
            PreviousHash = previousHash,
            SealMode = sealMode
        };
    }

    /// <summary>
    /// Reads stored integrity data. Malformed data, for example after tampering, yields <c>false</c>
    /// instead of an exception, so callers can report it like any other integrity failure.
    /// </summary>
    /// <remarks>
    /// Every member must have the BSON type the format writes: <c>FormatVersion</c> a 32-bit integer,
    /// <c>Algorithm</c> and <c>SealMode</c> strings, <c>PreviousHash</c> and <c>Hash</c> generic binary data (subtype
    /// <c>0x00</c>), and <c>PersonalData</c>, when present, an array of documents with a string <c>Path</c>, a generic
    /// binary <c>Commitment</c> and, unless erased, a generic binary <c>Salt</c>. A value the serializer could convert,
    /// such as a numeric seal mode, is malformed, so every verifier reads the same data.
    /// </remarks>
    /// <param name="value">The value of the <c>_integrity</c> element.</param>
    /// <param name="integrity">The integrity data when it could be read.</param>
    /// <returns><c>true</c> when the value is well-formed integrity data; otherwise <c>false</c>.</returns>
    public static Boolean TryRead(BsonValue value, [NotNullWhen(true)] out EventIntegrity? integrity)
    {
        integrity = null;
        if (value is not BsonDocument document || !HasFormatTypes(document))
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

    private static Boolean HasFormatTypes(BsonDocument document)
        => document.GetValue(nameof(EventIntegrity.FormatVersion), BsonNull.Value) is BsonInt32 &&
           document.GetValue(nameof(EventIntegrity.Algorithm), BsonNull.Value) is BsonString &&
           IsGenericBinary(document.GetValue(nameof(EventIntegrity.PreviousHash), BsonNull.Value)) &&
           IsGenericBinary(document.GetValue(nameof(EventIntegrity.Hash), BsonNull.Value)) &&
           document.GetValue(nameof(EventIntegrity.SealMode), BsonNull.Value) is BsonString &&
           (!document.TryGetValue(nameof(EventIntegrity.PersonalData), out var personalData) ||
            (personalData is BsonArray commitments && commitments.All(IsCommitment)));

    private static Boolean IsCommitment(BsonValue value)
        => value is BsonDocument commitment &&
           commitment.GetValue(nameof(PersonalDataCommitment.Path), BsonNull.Value) is BsonString &&
           IsGenericBinary(commitment.GetValue(nameof(PersonalDataCommitment.Commitment), BsonNull.Value)) &&
           (!commitment.TryGetValue(nameof(PersonalDataCommitment.Salt), out var salt) || IsGenericBinary(salt));

    private static Boolean IsGenericBinary(BsonValue value)
        => value is BsonBinaryData { SubType: BsonBinarySubType.Binary };
}
