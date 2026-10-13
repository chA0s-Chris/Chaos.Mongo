// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using System.Formats.Asn1;
using System.Security.Cryptography.X509Certificates;

/// <summary>
/// Decides whether the <c>tsa</c> name of a timestamp token corresponds to a subject name of the signing certificate
/// (RFC 3161, section 2.4.2).
/// </summary>
internal static class TimestampAuthorityName
{
    private static readonly Asn1Tag DirectoryNameTag = new(TagClass.ContextSpecific, 4, true);
    private const String SubjectAlternativeNameOid = "2.5.29.17";

    /// <summary>
    /// Gets a value indicating whether a <c>GeneralName</c> equals the certificate's subject, for a directory name, or
    /// one of its subject alternative names, compared by their DER encoding.
    /// </summary>
    /// <param name="generalName">The encoded <c>GeneralName</c>.</param>
    /// <param name="certificate">The signing certificate.</param>
    /// <returns><c>true</c> when the name corresponds to a subject name of the certificate.</returns>
    /// <exception cref="AsnContentException">Thrown when the name or the certificate's alternative names are malformed.</exception>
    public static Boolean Matches(ReadOnlyMemory<Byte> generalName, X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var reader = new AsnReader(generalName, AsnEncodingRules.DER);
        var isDirectoryName = reader.PeekTag().HasSameClassAndValue(DirectoryNameTag);
        var name = reader.ReadEncodedValue();
        reader.ThrowIfNotEmpty();

        if (isDirectoryName)
        {
            var directoryName = new AsnReader(name, AsnEncodingRules.DER).ReadSequence(DirectoryNameTag);
            var subject = directoryName.ReadEncodedValue();
            directoryName.ThrowIfNotEmpty();
            if (subject.Span.SequenceEqual(certificate.SubjectName.RawData))
            {
                return true;
            }
        }

        return EnumerateAlternativeNames(certificate).Any(alternative => alternative.Span.SequenceEqual(name.Span));
    }

    private static IEnumerable<ReadOnlyMemory<Byte>> EnumerateAlternativeNames(X509Certificate2 certificate)
    {
        if (certificate.Extensions[SubjectAlternativeNameOid] is not { } extension)
        {
            yield break;
        }

        var names = new AsnReader(extension.RawData, AsnEncodingRules.DER).ReadSequence();
        while (names.HasData)
        {
            yield return names.ReadEncodedValue();
        }
    }
}
