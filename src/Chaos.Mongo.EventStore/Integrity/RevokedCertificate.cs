// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using System.Formats.Asn1;
using System.Security.Cryptography.X509Certificates;

/// <summary>
/// An entry of a CRL's revoked certificates (RFC 5280, section 5.1.2.6) with the entry extensions that decide whether
/// it revokes a certificate at a time. The CRL's signature is not verified.
/// </summary>
/// <param name="Issuers">
/// The encoded names the certificate's issuer may have: the CRL issuer, unless a <c>certificateIssuer</c> extension of
/// an indirect CRL names others (RFC 5280, section 5.3.3), in which case every directory name it lists; empty when that
/// extension lists no directory name.
/// </param>
/// <param name="SerialNumber">The certificate's serial number as encoded in the CRL.</param>
/// <param name="RevocationDate">The time the CA processed the revocation.</param>
/// <param name="InvalidityDate">The time from which the certificate is known or suspected to be invalid, if stated.</param>
/// <param name="RemovedFromCrl">Whether the entry's reason is <c>removeFromCRL</c>, which lifts an earlier hold.</param>
internal sealed record RevokedCertificate(
    IReadOnlyList<ReadOnlyMemory<Byte>> Issuers,
    ReadOnlyMemory<Byte> SerialNumber,
    DateTimeOffset RevocationDate,
    DateTimeOffset? InvalidityDate,
    Boolean RemovedFromCrl)
{
    private const String CertificateIssuerOid = "2.5.29.29";
    private const String InvalidityDateOid = "2.5.29.24";
    private const String ReasonCodeOid = "2.5.29.21";
    private const Byte RemoveFromCrlReason = 8;

    /// <summary>
    /// Gets the time from which the entry revokes its certificate: the earlier of the revocation date and the
    /// invalidity date.
    /// </summary>
    public DateTimeOffset RevokedFrom => InvalidityDate is { } invalidityDate && (invalidityDate < RevocationDate) ? invalidityDate : RevocationDate;

    /// <summary>
    /// Decodes the revoked certificates of exactly one DER-encoded CRL.
    /// </summary>
    /// <param name="crl">The encoded CRL.</param>
    /// <returns>The entries in CRL order.</returns>
    /// <exception cref="AsnContentException">Thrown when the data is not exactly one DER-encoded CRL.</exception>
    public static IReadOnlyList<RevokedCertificate> DecodeAll(ReadOnlyMemory<Byte> crl)
    {
        // revokedCertificates SEQUENCE OF SEQUENCE { userCertificate CertificateSerialNumber, revocationDate Time,
        //                                           crlEntryExtensions Extensions OPTIONAL } OPTIONAL
        var header = CertificateRevocationListHeader.Decode(crl, out var tbsCertList);
        var entries = new List<RevokedCertificate>();
        if (!tbsCertList.HasData || !tbsCertList.PeekTag().HasSameClassAndValue(Asn1Tag.Sequence))
        {
            return entries;
        }

        IReadOnlyList<ReadOnlyMemory<Byte>> issuers = [header.Issuer];
        var revokedCertificates = tbsCertList.ReadSequence();
        while (revokedCertificates.HasData)
        {
            var entry = revokedCertificates.ReadSequence();
            var serialNumber = entry.ReadIntegerBytes();
            var revocationDate = CertificateRevocationListHeader.ReadTime(entry) ??
                                 throw new AsnContentException("A revoked certificate must state its revocation date.");
            DateTimeOffset? invalidityDate = null;
            var removedFromCrl = false;
            if (entry.HasData)
            {
                var extensions = entry.ReadSequence();
                while (extensions.HasData)
                {
                    var (oid, value) = ReadExtension(extensions);
                    switch (oid)
                    {
                        case ReasonCodeOid:
                            removedFromCrl = ReadSingle(value, reader => reader.ReadEnumeratedBytes().Span.SequenceEqual([RemoveFromCrlReason]));
                            break;
                        case InvalidityDateOid:
                            invalidityDate = ReadSingle(value, reader => reader.ReadGeneralizedTime());
                            break;
                        case CertificateIssuerOid:
                            // Applies to this and every following entry until the next such extension.
                            issuers = ReadSingle(value, ReadDirectoryNames);
                            break;
                    }
                }
            }

            entry.ThrowIfNotEmpty();
            entries.Add(new RevokedCertificate(issuers, serialNumber, revocationDate, invalidityDate, removedFromCrl));
        }

        return entries;
    }

    /// <summary>
    /// Gets a value indicating whether the entry revokes a certificate at or before a time.
    /// </summary>
    /// <param name="certificate">The certificate.</param>
    /// <param name="time">The time.</param>
    /// <returns>
    /// <c>true</c> when the entry names the certificate and revokes it at or before the time. An entry whose issuer names
    /// are unknown matches by serial number alone.
    /// </returns>
    public Boolean Revokes(X509Certificate2 certificate, DateTimeOffset time)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return !RemovedFromCrl &&
               SerialNumber.Span.SequenceEqual(certificate.SerialNumberBytes.Span) &&
               ((Issuers.Count == 0) || Issuers.Any(issuer => issuer.Span.SequenceEqual(certificate.IssuerName.RawData))) &&
               (RevokedFrom <= time);
    }

    /// <summary>
    /// Reads every <c>directoryName</c> of a <c>GeneralNames</c> value, skipping names of other kinds.
    /// </summary>
    private static IReadOnlyList<ReadOnlyMemory<Byte>> ReadDirectoryNames(AsnReader reader)
    {
        var directoryNameTag = new Asn1Tag(TagClass.ContextSpecific, 4, true);
        var names = reader.ReadSequence();
        var directoryNames = new List<ReadOnlyMemory<Byte>>();
        while (names.HasData)
        {
            if (names.PeekTag() == directoryNameTag)
            {
                var directoryName = names.ReadSequence(directoryNameTag);
                directoryNames.Add(directoryName.ReadEncodedValue());
                directoryName.ThrowIfNotEmpty();
                continue;
            }

            names.ReadEncodedValue();
        }

        return directoryNames;
    }

    /// <summary>
    /// Reads an <c>Extension</c>: its identifier and its value, skipping the criticality flag.
    /// </summary>
    private static (String Oid, ReadOnlyMemory<Byte> Value) ReadExtension(AsnReader extensions)
    {
        var extension = extensions.ReadSequence();
        var oid = extension.ReadObjectIdentifier();
        if (extension.PeekTag().HasSameClassAndValue(Asn1Tag.Boolean))
        {
            extension.ReadBoolean();
        }

        var value = extension.ReadOctetString();
        extension.ThrowIfNotEmpty();
        return (oid, value);
    }

    /// <summary>
    /// Reads exactly one DER value from an extension value.
    /// </summary>
    private static T ReadSingle<T>(ReadOnlyMemory<Byte> value, Func<AsnReader, T> read)
    {
        var reader = new AsnReader(value, AsnEncodingRules.DER);
        var result = read(reader);
        reader.ThrowIfNotEmpty();
        return result;
    }
}
