// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

/// <summary>
/// Verifies the signature of a DER-encoded CRL (RFC 5280, section 5.1) against its issuer's certificate.
/// </summary>
/// <remarks>
/// The CRL is only taken apart with <see cref="AsnReader"/>; the signature itself is verified by the BCL's
/// <see cref="RSA"/> and <see cref="ECDsa"/>. The accepted algorithms follow the policy for timestamp tokens: RSA with
/// PKCS #1 v1.5 or PSS padding and ECDSA, each with SHA-256, SHA-384 or SHA-512, and keys of at least 2048 bits for
/// RSA and 256 bits for ECDSA. MD5 and SHA-1 are rejected.
/// </remarks>
internal static class CertificateRevocationListSignature
{
    /// <summary>
    /// Verifies a CRL's signature against the public key of its issuer's certificate.
    /// </summary>
    /// <param name="crl">The DER-encoded CRL.</param>
    /// <param name="issuer">The certificate of the CRL's issuer.</param>
    /// <returns><c>null</c> when the signature is valid; otherwise why it is not accepted.</returns>
    public static String? Verify(ReadOnlyMemory<Byte> crl, X509Certificate2 issuer)
    {
        ArgumentNullException.ThrowIfNull(issuer);

        ReadOnlyMemory<Byte> tbsCertList;
        ReadOnlyMemory<Byte> signatureAlgorithm;
        Byte[] signature;
        try
        {
            // CertificateList ::= SEQUENCE { tbsCertList TBSCertList, signatureAlgorithm AlgorithmIdentifier, signatureValue BIT STRING }
            var reader = new AsnReader(crl, AsnEncodingRules.DER);
            var certificateList = reader.ReadSequence();
            reader.ThrowIfNotEmpty();
            tbsCertList = certificateList.ReadEncodedValue();
            signatureAlgorithm = certificateList.ReadEncodedValue();
            signature = certificateList.ReadBitString(out var unusedBits);
            certificateList.ThrowIfNotEmpty();
            if (unusedBits != 0)
            {
                return "the CRL's signature is not a whole number of bytes.";
            }

            // TBSCertList ::= SEQUENCE { version Version OPTIONAL, signature AlgorithmIdentifier, ... }; RFC 5280,
            // section 5.1.1.2, requires it to equal the outer signature algorithm.
            var tbs = new AsnReader(tbsCertList, AsnEncodingRules.DER).ReadSequence();
            if (tbs.PeekTag().HasSameClassAndValue(Asn1Tag.Integer))
            {
                tbs.ReadInteger();
            }

            if (!tbs.ReadEncodedValue().Span.SequenceEqual(signatureAlgorithm.Span))
            {
                return "the CRL's signature algorithm differs from the one in its signed part.";
            }

            return DirectSignatureVerification.VerifySignedData(tbsCertList.Span, signatureAlgorithm, signature, issuer);
        }
        catch (AsnContentException)
        {
            return "the CRL is not a single DER-encoded CRL.";
        }
        catch (CryptographicException)
        {
            return "the CRL's signature could not be verified with its issuer's key.";
        }
    }
}
