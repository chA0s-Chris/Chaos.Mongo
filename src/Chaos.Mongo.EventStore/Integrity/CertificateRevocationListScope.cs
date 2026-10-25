// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using System.Formats.Asn1;

/// <summary>
/// Decides whether a CRL is a complete CRL for a distribution point, from its CRL extensions (RFC 5280, section 5.2).
/// </summary>
/// <remarks>
/// A CRL is complete for a point when it is not a delta CRL, carries no critical extension other than the issuing
/// distribution point and the CRL number, and, when it carries an issuing distribution point, that extension covers all
/// reasons, is neither restricted to CA nor to attribute certificates nor indirect, is not restricted to end-entity
/// certificates when a CA certificate names the point (RFC 5280, section 6.3.3), and its full name, if present, names
/// one of the point's locations, compared as <see cref="CrlLocation"/> normalizes them. Any other CRL, such as a delta
/// CRL or a CRL partitioned by reason or by distribution point, cannot show that a certificate is not revoked.
/// </remarks>
internal static class CertificateRevocationListScope
{
    private static readonly Asn1Tag CrlExtensionsTag = new(TagClass.ContextSpecific, 0, true);
    private const String CrlNumberOid = "2.5.29.20";
    private const String DeltaCrlIndicatorOid = "2.5.29.27";
    private static readonly Asn1Tag DistributionPointTag = new(TagClass.ContextSpecific, 0, true);
    private static readonly Asn1Tag FullNameTag = new(TagClass.ContextSpecific, 0, true);
    private static readonly Asn1Tag IndirectCrlTag = new(TagClass.ContextSpecific, 4);
    private const String IssuingDistributionPointOid = "2.5.29.28";
    private static readonly Asn1Tag OnlyAttributeCertificatesTag = new(TagClass.ContextSpecific, 5);
    private static readonly Asn1Tag OnlyCaCertificatesTag = new(TagClass.ContextSpecific, 2);
    private static readonly Asn1Tag OnlySomeReasonsTag = new(TagClass.ContextSpecific, 3);
    private static readonly Asn1Tag OnlyUserCertificatesTag = new(TagClass.ContextSpecific, 1);
    private static readonly Asn1Tag UriTag = new(TagClass.ContextSpecific, 6);

    /// <summary>
    /// Decides whether a CRL is a complete CRL for a distribution point.
    /// </summary>
    /// <param name="crl">The DER-encoded CRL.</param>
    /// <param name="locations">The locations of the distribution point the CRL was captured for.</param>
    /// <param name="fromCaCertificate">Whether a CA certificate names the point.</param>
    /// <returns><c>true</c> when the CRL is complete for the point.</returns>
    /// <exception cref="AsnContentException">Thrown when the data is not exactly one DER-encoded CRL.</exception>
    public static Boolean IsCompleteFor(ReadOnlyMemory<Byte> crl, IReadOnlyList<Uri> locations, Boolean fromCaCertificate)
    {
        ArgumentNullException.ThrowIfNull(locations);

        // TBSCertList continues with revokedCertificates SEQUENCE OF ... OPTIONAL, crlExtensions [0] EXPLICIT Extensions OPTIONAL.
        CertificateRevocationListHeader.Decode(crl, out var tbsCertList);
        if (tbsCertList.HasData && tbsCertList.PeekTag().HasSameClassAndValue(Asn1Tag.Sequence))
        {
            tbsCertList.ReadEncodedValue();
        }

        if (!tbsCertList.HasData)
        {
            return true;
        }

        var extensionsWrapper = tbsCertList.ReadSequence(CrlExtensionsTag);
        tbsCertList.ThrowIfNotEmpty();
        var extensions = extensionsWrapper.ReadSequence();
        extensionsWrapper.ThrowIfNotEmpty();

        var seen = new HashSet<String>(StringComparer.Ordinal);
        while (extensions.HasData)
        {
            var (oid, critical, value) = ReadExtension(extensions);
            if (!seen.Add(oid))
            {
                return false;
            }

            var complete = oid switch
            {
                DeltaCrlIndicatorOid => false,
                IssuingDistributionPointOid => IssuingDistributionPointCovers(value, locations, fromCaCertificate),
                CrlNumberOid => true,
                _ => !critical
            };
            if (!complete)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Decides whether an issuing distribution point leaves the CRL complete for one of the locations:
    /// <c>
    /// SEQUENCE { distributionPoint [0] OPTIONAL, onlyContainsUserCerts [1] BOOLEAN DEFAULT FALSE,
    /// onlyContainsCACerts [2] BOOLEAN DEFAULT FALSE, onlySomeReasons [3] ReasonFlags OPTIONAL,
    /// indirectCRL [4] BOOLEAN DEFAULT FALSE, onlyContainsAttributeCerts [5] BOOLEAN DEFAULT FALSE }
    /// </c>
    /// .
    /// </summary>
    private static Boolean IssuingDistributionPointCovers(ReadOnlyMemory<Byte> value, IReadOnlyList<Uri> locations, Boolean fromCaCertificate)
    {
        var reader = new AsnReader(value, AsnEncodingRules.DER);
        var point = reader.ReadSequence();
        reader.ThrowIfNotEmpty();

        var namesLocation = true;
        if (point.HasData && point.PeekTag().HasSameClassAndValue(DistributionPointTag))
        {
            namesLocation = NamesLocation(point.ReadSequence(DistributionPointTag), locations);
        }

        var restricted = ReadFlag(point, OnlyUserCertificatesTag) && fromCaCertificate;
        restricted |= ReadFlag(point, OnlyCaCertificatesTag);
        if (point.HasData && point.PeekTag().HasSameClassAndValue(OnlySomeReasonsTag))
        {
            point.ReadBitString(out _, OnlySomeReasonsTag);
            restricted = true;
        }

        restricted |= ReadFlag(point, IndirectCrlTag);
        restricted |= ReadFlag(point, OnlyAttributeCertificatesTag);
        point.ThrowIfNotEmpty();
        return namesLocation && !restricted;
    }

    /// <summary>
    /// Decides whether a distribution point name is a full name that names one of the locations; a name relative to
    /// the CRL issuer names no location.
    /// </summary>
    private static Boolean NamesLocation(AsnReader name, IReadOnlyList<Uri> locations)
    {
        if (!name.PeekTag().HasSameClassAndValue(FullNameTag))
        {
            name.ReadEncodedValue();
            name.ThrowIfNotEmpty();
            return false;
        }

        var fullName = name.ReadSequence(FullNameTag);
        name.ThrowIfNotEmpty();
        var normalizedLocations = locations.Select(location => CrlLocation.Normalize(location.OriginalString)).OfType<String>().ToHashSet(StringComparer.Ordinal);
        var named = false;
        while (fullName.HasData)
        {
            if (!fullName.PeekTag().HasSameClassAndValue(UriTag))
            {
                fullName.ReadEncodedValue();
                continue;
            }

            var value = fullName.ReadCharacterString(UniversalTagNumber.IA5String, UriTag);
            named |= CrlLocation.Normalize(value) is { } normalized && normalizedLocations.Contains(normalized);
        }

        return named;
    }

    /// <summary>
    /// Reads an <c>Extension</c>: its identifier, its criticality and its value.
    /// </summary>
    private static (String Oid, Boolean Critical, ReadOnlyMemory<Byte> Value) ReadExtension(AsnReader extensions)
    {
        var extension = extensions.ReadSequence();
        var oid = extension.ReadObjectIdentifier();
        var critical = extension.PeekTag().HasSameClassAndValue(Asn1Tag.Boolean) && extension.ReadBoolean();
        var value = extension.ReadOctetString();
        extension.ThrowIfNotEmpty();
        return (oid, critical, value);
    }

    private static Boolean ReadFlag(AsnReader point, Asn1Tag tag)
        => point.HasData && point.PeekTag().HasSameClassAndValue(tag) && point.ReadBoolean(tag);
}
