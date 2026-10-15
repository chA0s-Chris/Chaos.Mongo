// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using System.Formats.Asn1;

/// <summary>
/// The fields of a DER-encoded CRL (RFC 5280, section 5.1) that decide whether a downloaded CRL is usable evidence
/// for a certificate. The CRL's signature is not verified, and its entries and extensions are not decoded.
/// </summary>
/// <param name="Issuer">The encoded name of the CRL issuer.</param>
/// <param name="ThisUpdate">The time the CRL was issued.</param>
/// <param name="NextUpdate">The time by which the next CRL will be issued, or <c>null</c> when the CRL states none.</param>
internal sealed record CertificateRevocationListHeader(ReadOnlyMemory<Byte> Issuer, DateTimeOffset ThisUpdate, DateTimeOffset? NextUpdate)
{
    /// <summary>
    /// Decodes the header of exactly one DER-encoded <c>CertificateList</c>.
    /// </summary>
    /// <param name="crl">The encoded CRL.</param>
    /// <returns>The header.</returns>
    /// <exception cref="AsnContentException">Thrown when the data is not exactly one DER-encoded CRL.</exception>
    public static CertificateRevocationListHeader Decode(ReadOnlyMemory<Byte> crl) => Decode(crl, out _);

    /// <summary>
    /// Decodes the header of exactly one DER-encoded <c>CertificateList</c> and returns the rest of its
    /// <c>TBSCertList</c>, which starts with the revoked certificates if there are any.
    /// </summary>
    /// <param name="crl">The encoded CRL.</param>
    /// <param name="remainder">The reader positioned after the header fields of the <c>TBSCertList</c>.</param>
    /// <returns>The header.</returns>
    /// <exception cref="AsnContentException">Thrown when the data is not exactly one DER-encoded CRL.</exception>
    internal static CertificateRevocationListHeader Decode(ReadOnlyMemory<Byte> crl, out AsnReader remainder)
    {
        // CertificateList ::= SEQUENCE { tbsCertList TBSCertList, signatureAlgorithm AlgorithmIdentifier, signatureValue BIT STRING }
        var reader = new AsnReader(crl, AsnEncodingRules.DER);
        var certificateList = reader.ReadSequence();
        reader.ThrowIfNotEmpty();
        var tbsCertList = certificateList.ReadSequence();
        certificateList.ReadSequence();
        certificateList.ReadBitString(out _);
        certificateList.ThrowIfNotEmpty();

        // TBSCertList ::= SEQUENCE { version Version OPTIONAL, signature AlgorithmIdentifier, issuer Name,
        //                            thisUpdate Time, nextUpdate Time OPTIONAL, ... }
        if (tbsCertList.PeekTag().HasSameClassAndValue(Asn1Tag.Integer) && (!tbsCertList.TryReadInt32(out var version) || version != 1))
        {
            throw new AsnContentException("A CRL that states its version must be a version 2 CRL.");
        }

        tbsCertList.ReadSequence();
        if (!tbsCertList.PeekTag().HasSameClassAndValue(Asn1Tag.Sequence))
        {
            throw new AsnContentException("A CRL issuer must be a distinguished name.");
        }

        var issuer = tbsCertList.ReadEncodedValue();
        var thisUpdate = ReadTime(tbsCertList) ?? throw new AsnContentException("A CRL must state when it was issued.");
        var nextUpdate = tbsCertList.HasData ? ReadTime(tbsCertList) : null;
        remainder = tbsCertList;
        return new CertificateRevocationListHeader(issuer, thisUpdate, nextUpdate);
    }

    /// <summary>
    /// Reads a <c>Time</c>, which is a UTCTime or a GeneralizedTime; <c>null</c> when the next value is neither.
    /// </summary>
    internal static DateTimeOffset? ReadTime(AsnReader reader)
    {
        var tag = reader.PeekTag();
        if (tag.HasSameClassAndValue(Asn1Tag.UtcTime))
        {
            return reader.ReadUtcTime();
        }

        return tag.HasSameClassAndValue(Asn1Tag.GeneralizedTime) ? reader.ReadGeneralizedTime() : null;
    }
}
