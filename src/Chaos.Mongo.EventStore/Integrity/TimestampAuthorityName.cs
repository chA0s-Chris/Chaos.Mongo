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
    private static readonly Asn1Tag DnsNameTag = new(TagClass.ContextSpecific, 2);
    private static readonly Asn1Tag Rfc822NameTag = new(TagClass.ContextSpecific, 1);
    private const String SubjectAlternativeNameOid = "2.5.29.17";

    /// <summary>
    /// Gets a value indicating whether a <c>GeneralName</c> equals the certificate's subject, for a directory name, or
    /// one of its subject alternative names. DNS names compare case-insensitively and email addresses with a
    /// case-insensitive domain (RFC 5280, sections 7.2 and 7.5); every other form compares by its DER encoding.
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

        return EnumerateAlternativeNames(certificate).Any(alternative => NamesMatch(name, alternative));
    }

    private static IEnumerable<ReadOnlyMemory<Byte>> EnumerateAlternativeNames(X509Certificate2 certificate)
    {
        if (certificate.Extensions[SubjectAlternativeNameOid] is not { } extension)
        {
            yield break;
        }

        var reader = new AsnReader(extension.RawData, AsnEncodingRules.DER);
        var names = reader.ReadSequence();
        reader.ThrowIfNotEmpty();
        while (names.HasData)
        {
            yield return names.ReadEncodedValue();
        }
    }

    /// <summary>
    /// Compares mailboxes as RFC 5280, section 7.5 prescribes: the local part is case-sensitive, the domain is not.
    /// </summary>
    private static Boolean MailboxesMatch(String left, String right)
    {
        var leftAt = left.LastIndexOf('@');
        var rightAt = right.LastIndexOf('@');
        if (leftAt < 0 || rightAt < 0)
        {
            return String.Equals(left, right, StringComparison.Ordinal);
        }

        return String.Equals(left[..leftAt], right[..rightAt], StringComparison.Ordinal) &&
               String.Equals(left[(leftAt + 1)..], right[(rightAt + 1)..], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Compares two encoded general names of the same form with the form's matching rule.
    /// </summary>
    private static Boolean NamesMatch(ReadOnlyMemory<Byte> name, ReadOnlyMemory<Byte> alternative)
    {
        var tag = new AsnReader(name, AsnEncodingRules.DER).PeekTag();
        if (!tag.HasSameClassAndValue(new AsnReader(alternative, AsnEncodingRules.DER).PeekTag()))
        {
            return false;
        }

        if (tag.HasSameClassAndValue(DnsNameTag))
        {
            return String.Equals(ReadIa5String(name, DnsNameTag), ReadIa5String(alternative, DnsNameTag), StringComparison.OrdinalIgnoreCase);
        }

        if (tag.HasSameClassAndValue(Rfc822NameTag))
        {
            return MailboxesMatch(ReadIa5String(name, Rfc822NameTag), ReadIa5String(alternative, Rfc822NameTag));
        }

        return name.Span.SequenceEqual(alternative.Span);
    }

    private static String ReadIa5String(ReadOnlyMemory<Byte> encoded, Asn1Tag tag)
    {
        var reader = new AsnReader(encoded, AsnEncodingRules.DER);
        var value = reader.ReadCharacterString(UniversalTagNumber.IA5String, tag);
        reader.ThrowIfNotEmpty();
        return value;
    }
}
