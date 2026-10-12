// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using System.Formats.Asn1;
using System.Security.Cryptography.X509Certificates;

/// <summary>
/// Reads the HTTP locations of a certificate's CRL distribution points (RFC 5280, section 4.2.1.13).
/// </summary>
internal static class CrlDistributionPoints
{
    private const String ExtensionOid = "2.5.29.31";
    private static readonly Asn1Tag FullNameTag = new(TagClass.ContextSpecific, 0, true);
    private static readonly Asn1Tag NameTag = new(TagClass.ContextSpecific, 0, true);
    private static readonly Asn1Tag UriTag = new(TagClass.ContextSpecific, 6);

    /// <summary>
    /// Gets the HTTP and HTTPS locations of every distribution point that names at least one.
    /// </summary>
    /// <param name="certificate">The certificate.</param>
    /// <returns>One list of alternative locations per distribution point; empty without the extension.</returns>
    /// <exception cref="AsnContentException">Thrown when the extension is malformed.</exception>
    public static IReadOnlyList<IReadOnlyList<Uri>> GetHttpUris(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (certificate.Extensions[ExtensionOid] is not { } extension)
        {
            return [];
        }

        var reader = new AsnReader(extension.RawData, AsnEncodingRules.DER);
        var points = reader.ReadSequence();
        reader.ThrowIfNotEmpty();

        var result = new List<IReadOnlyList<Uri>>();
        while (points.HasData)
        {
            var uris = ReadPointUris(points.ReadSequence());
            if (uris.Count > 0)
            {
                result.Add(uris);
            }
        }

        return result;
    }

    /// <summary>
    /// Gets a value indicating whether a certificate names CRL distribution points at all.
    /// </summary>
    /// <param name="certificate">The certificate.</param>
    /// <returns><c>true</c> when the certificate carries the extension.</returns>
    public static Boolean NamesDistributionPoints(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return certificate.Extensions[ExtensionOid] is not null;
    }

    private static IReadOnlyList<Uri> ReadFullNameUris(AsnReader fullName)
    {
        var uris = new List<Uri>();
        while (fullName.HasData)
        {
            if (!fullName.PeekTag().HasSameClassAndValue(UriTag))
            {
                fullName.ReadEncodedValue();
                continue;
            }

            var value = fullName.ReadCharacterString(UniversalTagNumber.IA5String, UriTag);
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && ((uri.Scheme == Uri.UriSchemeHttp) || (uri.Scheme == Uri.UriSchemeHttps)))
            {
                uris.Add(uri);
            }
        }

        return uris;
    }

    private static IReadOnlyList<Uri> ReadPointUris(AsnReader point)
    {
        if (!point.HasData || !point.PeekTag().HasSameClassAndValue(NameTag))
        {
            return [];
        }

        // The CHOICE inside [0] is either a full name [0] or a name relative to the CRL issuer [1], which has no URI.
        var name = point.ReadSequence(NameTag);
        return name.HasData && name.PeekTag().HasSameClassAndValue(FullNameTag)
            ? ReadFullNameUris(name.ReadSequence(FullNameTag))
            : [];
    }
}
