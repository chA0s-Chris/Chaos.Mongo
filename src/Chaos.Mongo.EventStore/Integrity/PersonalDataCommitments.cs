// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using MongoDB.Bson;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Computes, substitutes and verifies the commitments that stand in for personal data values in the chain hash.
/// </summary>
/// <remarks>
/// A commitment is <c>SHA-256(Salt ‖ element type byte ‖ value bytes)</c> over the bytes of the stored
/// document. Including the type byte binds the BSON type, so a value whose type is changed while its bytes
/// stay the same is detected. The canonical form of an event replaces every committed value by its commitment,
/// so the chain hash stays verifiable after the value is set to <c>null</c> and its salt is discarded.
/// </remarks>
internal static class PersonalDataCommitments
{
    /// <summary>
    /// The length of a commitment in bytes.
    /// </summary>
    public const Int32 CommitmentLength = 32;

    /// <summary>
    /// The length of a salt in bytes.
    /// </summary>
    public const Int32 SaltLength = 32;

    private static readonly Byte[] BindingTag = "Chaos.Mongo.EventStore/integrity/personal-data/v1"u8.ToArray();

    /// <summary>
    /// Replaces every committed value in the document by its commitment.
    /// </summary>
    /// <param name="document">The event's BSON without the <c>_integrity</c> element.</param>
    /// <param name="commitments">The recorded commitments; every path must resolve in the document.</param>
    /// <returns>The canonical form of the event.</returns>
    public static Byte[] Canonicalize(Byte[] document, IReadOnlyList<PersonalDataCommitment> commitments)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(commitments);

        var values = new Dictionary<String, Byte[]>(commitments.Count, StringComparer.Ordinal);
        foreach (var commitment in commitments)
        {
            values.Add(commitment.Path, commitment.Commitment);
        }

        return RawBsonElements.ReplaceValuesWithBinary(document, values);
    }

    /// <summary>
    /// Records a fresh commitment for every path that resolves to an element of the document. A stored
    /// <c>null</c> value is committed too; a path that does not resolve is not recorded.
    /// </summary>
    /// <param name="document">The event's BSON without the <c>_integrity</c> element.</param>
    /// <param name="paths">The dotted element paths marked as personal data.</param>
    /// <returns>The commitments, or <c>null</c> when no path resolves.</returns>
    public static List<PersonalDataCommitment>? Commit(Byte[] document, IReadOnlyList<String> paths)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(paths);

        List<PersonalDataCommitment>? commitments = null;
        foreach (var path in paths)
        {
            if (!RawBsonElements.TryLocate(document, path, out var type, out var value))
            {
                continue;
            }

            var salt = RandomNumberGenerator.GetBytes(SaltLength);
            commitments ??= new List<PersonalDataCommitment>(paths.Count);
            commitments.Add(new PersonalDataCommitment
            {
                Path = path,
                Salt = salt,
                Commitment = ComputeCommitment(salt, type, document.AsSpan(value))
            });
        }

        return commitments;
    }

    /// <summary>
    /// Encodes which paths are recorded, for the chain hash: a domain tag, then every recorded path in recorded
    /// order, each terminated by a zero byte.
    /// </summary>
    /// <param name="commitments">The recorded commitments.</param>
    /// <returns>The binding bytes.</returns>
    public static Byte[] ComputeBinding(IReadOnlyList<PersonalDataCommitment> commitments)
    {
        ArgumentNullException.ThrowIfNull(commitments);

        using var binding = new MemoryStream();
        binding.Write(BindingTag);
        foreach (var commitment in commitments)
        {
            binding.Write(Encoding.UTF8.GetBytes(commitment.Path));
            binding.WriteByte(0);
        }

        return binding.ToArray();
    }

    /// <summary>
    /// Computes the commitment to a stored value.
    /// </summary>
    /// <param name="salt">The salt.</param>
    /// <param name="type">The BSON type of the value.</param>
    /// <param name="value">The stored bytes of the value.</param>
    /// <returns>The commitment.</returns>
    public static Byte[] ComputeCommitment(ReadOnlySpan<Byte> salt, BsonType type, ReadOnlySpan<Byte> value)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(salt);
        hash.AppendData([(Byte)type]);
        hash.AppendData(value);
        return hash.GetHashAndReset();
    }

    /// <summary>
    /// Checks that the recorded commitments have the expected lengths and distinct paths, none of which is a
    /// prefix of another. Overlapping paths cannot be substituted independently, so they are malformed.
    /// </summary>
    /// <param name="commitments">The recorded commitments.</param>
    /// <returns><c>true</c> when every commitment is well-formed; otherwise <c>false</c>.</returns>
    public static Boolean IsWellFormed(IReadOnlyList<PersonalDataCommitment> commitments)
    {
        ArgumentNullException.ThrowIfNull(commitments);

        var paths = new HashSet<String>(StringComparer.Ordinal);
        foreach (var commitment in commitments)
        {
            if (commitment.Commitment.Length != CommitmentLength ||
                commitment.Salt is { Length: not SaltLength } ||
                String.IsNullOrEmpty(commitment.Path) ||
                !paths.Add(commitment.Path))
            {
                return false;
            }
        }

        foreach (var path in paths)
        {
            if (paths.Any(other => other.Length > path.Length && other[path.Length] == '.' && other.StartsWith(path, StringComparison.Ordinal)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Verifies the stored value of a recorded path against its commitment.
    /// </summary>
    /// <param name="document">The event's BSON without the <c>_integrity</c> element.</param>
    /// <param name="commitment">The recorded commitment.</param>
    /// <returns>The outcome.</returns>
    public static PersonalDataVerification Verify(Byte[] document, PersonalDataCommitment commitment)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(commitment);

        if (!RawBsonElements.TryLocate(document, commitment.Path, out var type, out var value))
        {
            return PersonalDataVerification.Unresolved;
        }

        if (commitment.Salt is { } salt)
        {
            return ComputeCommitment(salt, type, document.AsSpan(value)).AsSpan().SequenceEqual(commitment.Commitment)
                ? PersonalDataVerification.Committed
                : PersonalDataVerification.Mismatch;
        }

        return type == BsonType.Null
            ? PersonalDataVerification.Erased
            : PersonalDataVerification.NotErased;
    }
}
