// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using System.Formats.Asn1;
using System.Security.Cryptography.X509Certificates;

/// <summary>
/// Reads the HTTP locations of a certificate's CRL distribution points (RFC 5280, section 4.2.1.13).
/// </summary>
/// <remarks>
/// The reader locates the CRLs that a timestamp token's evidence must capture; validating the certificate is the job of
/// the <see cref="X509Chain"/> that the timestamp authority builds. A distribution point is therefore decoded only as
/// far as it decides which CRLs are captured: the structure of the extension and of each point, the HTTP and HTTPS
/// locations of a full name, and whether the point covers all revocation reasons. Fields that never yield a location,
/// such as a name relative to the CRL issuer, the CRL issuer itself and general names other than a URI, are checked
/// only for the tags and non-empty sequences RFC 5280 requires; their contents are not validated. Locations other
/// than HTTP and HTTPS, delta CRLs and the certificates of indirect CRL issuers are not captured.
/// </remarks>
internal static class CrlDistributionPoints
{
    private static readonly Asn1Tag CrlIssuerTag = new(TagClass.ContextSpecific, 2, true);
    private const String ExtensionOid = "2.5.29.31";
    private static readonly Asn1Tag FullNameTag = new(TagClass.ContextSpecific, 0, true);
    private const Int32 LastGeneralNameTag = 8;
    private static readonly Asn1Tag NameTag = new(TagClass.ContextSpecific, 0, true);
    private static readonly Asn1Tag ReasonsTag = new(TagClass.ContextSpecific, 1);
    private static readonly Asn1Tag RelativeNameTag = new(TagClass.ContextSpecific, 1, true);
    private static readonly Asn1Tag UriTag = new(TagClass.ContextSpecific, 6);

    /// <summary>
    /// Gets every distribution point that names at least one HTTP or HTTPS location.
    /// </summary>
    /// <param name="certificate">The certificate.</param>
    /// <returns>The points with their alternative locations; empty without the extension.</returns>
    /// <exception cref="AsnContentException">Thrown when the extension is malformed or present more than once.</exception>
    public static IReadOnlyList<HttpDistributionPoint> GetHttpDistributionPoints(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (FindExtension(certificate) is not { } extension)
        {
            return [];
        }

        var reader = new AsnReader(extension.RawData, AsnEncodingRules.DER);
        var points = reader.ReadSequence();
        reader.ThrowIfNotEmpty();

        var result = new List<HttpDistributionPoint>();
        while (points.HasData)
        {
            var point = ReadPoint(points.ReadSequence());
            if (point.Locations.Count > 0)
            {
                result.Add(point);
            }
        }

        return result;
    }

    /// <summary>
    /// Gets a value indicating whether a certificate names CRL distribution points at all.
    /// </summary>
    /// <param name="certificate">The certificate.</param>
    /// <returns><c>true</c> when the certificate carries the extension.</returns>
    /// <exception cref="AsnContentException">Thrown when the extension is present more than once.</exception>
    public static Boolean NamesDistributionPoints(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return FindExtension(certificate) is not null;
    }

    /// <summary>
    /// Gets a value indicating whether <c>ReasonFlags</c> name every revocation reason, bits 1 to 8; bit 0 is unused
    /// (RFC 5280, section 4.2.1.13).
    /// </summary>
    private static Boolean CoversAllReasons(Byte[] reasons)
        => reasons.Length >= 2 && (reasons[0] & 0x7F) == 0x7F && (reasons[1] & 0x80) == 0x80;

    /// <summary>
    /// Finds the extension, which a certificate must not carry more than once (RFC 5280, section 4.2). The extension
    /// indexer would silently pick one instance.
    /// </summary>
    private static X509Extension? FindExtension(X509Certificate2 certificate)
    {
        X509Extension? found = null;
        foreach (var extension in certificate.Extensions)
        {
            if (extension.Oid?.Value != ExtensionOid)
            {
                continue;
            }

            if (found is not null)
            {
                throw new AsnContentException("A certificate must not carry the CRL distribution points extension more than once.");
            }

            found = extension;
        }

        return found;
    }

    private static IReadOnlyList<Uri> ReadFullNameUris(AsnReader fullName)
    {
        if (!fullName.HasData)
        {
            throw new AsnContentException("A distribution point's full name must contain at least one general name.");
        }

        var uris = new List<Uri>();
        while (fullName.HasData)
        {
            if (!fullName.PeekTag().HasSameClassAndValue(UriTag))
            {
                SkipGeneralName(fullName);
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

    /// <summary>
    /// Reads a <c>GeneralNames</c> whose locations are not needed, such as the CRL issuer: it must contain at least one
    /// general name, and every element must be one of the RFC 5280 alternatives.
    /// </summary>
    private static void ReadGeneralNames(AsnReader names)
    {
        if (!names.HasData)
        {
            throw new AsnContentException("A general names sequence must contain at least one general name.");
        }

        while (names.HasData)
        {
            SkipGeneralName(names);
        }
    }

    /// <summary>
    /// Reads the name of a distribution point. The CHOICE is exactly one of a full name [0] or a name relative to the
    /// CRL issuer [1], which has no URI.
    /// </summary>
    private static IReadOnlyList<Uri> ReadNameUris(AsnReader name)
    {
        IReadOnlyList<Uri> uris;
        if (name.PeekTag().HasSameClassAndValue(FullNameTag))
        {
            uris = ReadFullNameUris(name.ReadSequence(FullNameTag));
        }
        else if (name.PeekTag().HasSameClassAndValue(RelativeNameTag))
        {
            name.ReadEncodedValue();
            uris = [];
        }
        else
        {
            throw new AsnContentException("A distribution point name must be a full name or a name relative to the CRL issuer.");
        }

        name.ThrowIfNotEmpty();
        return uris;
    }

    /// <summary>
    /// Reads one distribution point: an optional name [0], optional reasons [1] and an optional CRL issuer [2], in this
    /// order and each at most once. A point must carry a name or a CRL issuer; only the name contributes locations,
    /// and a point without reasons covers all of them.
    /// </summary>
    private static HttpDistributionPoint ReadPoint(AsnReader point)
    {
        IReadOnlyList<Uri> uris = [];
        var named = false;
        var coversAllReasons = true;
        if (point.HasData && point.PeekTag().HasSameClassAndValue(NameTag))
        {
            uris = ReadNameUris(point.ReadSequence(NameTag));
            named = true;
        }

        if (point.HasData && point.PeekTag().HasSameClassAndValue(ReasonsTag))
        {
            // ReasonFlags is an implicitly tagged BIT STRING; decoding it rejects a malformed value.
            coversAllReasons = CoversAllReasons(point.ReadBitString(out _, ReasonsTag));
        }

        if (point.HasData && point.PeekTag().HasSameClassAndValue(CrlIssuerTag))
        {
            ReadGeneralNames(point.ReadSequence(CrlIssuerTag));
            named = true;
        }

        point.ThrowIfNotEmpty();
        if (!named)
        {
            throw new AsnContentException("A distribution point must name a distribution point or a CRL issuer.");
        }

        return new HttpDistributionPoint(uris, coversAllReasons);
    }

    /// <summary>
    /// Skips one general name, which must be one of the context-specific alternatives [0] to [8] of RFC 5280.
    /// </summary>
    private static void SkipGeneralName(AsnReader names)
    {
        var tag = names.PeekTag();
        if (tag.TagClass != TagClass.ContextSpecific || tag.TagValue > LastGeneralNameTag)
        {
            throw new AsnContentException("A general name must be one of the RFC 5280 alternatives.");
        }

        names.ReadEncodedValue();
    }
}
