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
    private const String EcdsaWithSha256Oid = "1.2.840.10045.4.3.2";
    private const String EcdsaWithSha384Oid = "1.2.840.10045.4.3.3";
    private const String EcdsaWithSha512Oid = "1.2.840.10045.4.3.4";
    private const String Mgf1Oid = "1.2.840.113549.1.1.8";
    private const String RsaPssOid = "1.2.840.113549.1.1.10";
    private const String Sha256Oid = "2.16.840.1.101.3.4.2.1";
    private const String Sha256WithRsaOid = "1.2.840.113549.1.1.11";
    private const String Sha384Oid = "2.16.840.1.101.3.4.2.2";
    private const String Sha384WithRsaOid = "1.2.840.113549.1.1.12";
    private const String Sha512Oid = "2.16.840.1.101.3.4.2.3";
    private const String Sha512WithRsaOid = "1.2.840.113549.1.1.13";

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

            return VerifyWithAlgorithm(tbsCertList.Span, signatureAlgorithm, signature, issuer);
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

    private static Int32 HashLength(HashAlgorithmName hashAlgorithm)
        => hashAlgorithm == HashAlgorithmName.SHA256 ? 32 : hashAlgorithm == HashAlgorithmName.SHA384 ? 48 : 64;

    private static HashAlgorithmName? ReadHashAlgorithm(AsnReader algorithmIdentifier)
    {
        var hash = algorithmIdentifier.ReadSequence();
        var oid = hash.ReadObjectIdentifier();
        if (hash.HasData)
        {
            hash.ReadNull();
        }

        hash.ThrowIfNotEmpty();
        return oid switch
        {
            Sha256Oid => HashAlgorithmName.SHA256,
            Sha384Oid => HashAlgorithmName.SHA384,
            Sha512Oid => HashAlgorithmName.SHA512,
            _ => null
        };
    }

    /// <summary>
    /// Reads RSASSA-PSS parameters (RFC 4055, section 3.1). Only the form the BCL can verify is accepted: an explicit
    /// SHA-2 hash, MGF1 with the same hash, a salt as long as the hash and the default trailer. The defaults name SHA-1.
    /// </summary>
    /// <returns>The hash algorithm, or <c>null</c> when the parameters are not accepted.</returns>
    private static HashAlgorithmName? ReadPssParameters(AsnReader parameters)
    {
        var sequence = parameters.ReadSequence();
        parameters.ThrowIfNotEmpty();
        var hashTag = new Asn1Tag(TagClass.ContextSpecific, 0, true);
        var maskTag = new Asn1Tag(TagClass.ContextSpecific, 1, true);
        var saltTag = new Asn1Tag(TagClass.ContextSpecific, 2, true);
        var trailerTag = new Asn1Tag(TagClass.ContextSpecific, 3, true);
        if (!sequence.HasData || sequence.PeekTag() != hashTag)
        {
            return null;
        }

        var hashField = sequence.ReadSequence(hashTag);
        var hash = ReadHashAlgorithm(hashField);
        hashField.ThrowIfNotEmpty();
        if (hash is not { } hashAlgorithm || !sequence.HasData || sequence.PeekTag() != maskTag)
        {
            return null;
        }

        var maskField = sequence.ReadSequence(maskTag);
        var mask = maskField.ReadSequence();
        maskField.ThrowIfNotEmpty();
        if (mask.ReadObjectIdentifier() != Mgf1Oid || ReadHashAlgorithm(mask) != hashAlgorithm)
        {
            return null;
        }

        mask.ThrowIfNotEmpty();
        if (!sequence.HasData || sequence.PeekTag() != saltTag)
        {
            return null;
        }

        var saltField = sequence.ReadSequence(saltTag);
        if (!saltField.TryReadInt32(out var saltLength) || saltLength != HashLength(hashAlgorithm))
        {
            return null;
        }

        saltField.ThrowIfNotEmpty();
        if (sequence.HasData)
        {
            var trailerField = sequence.ReadSequence(trailerTag);
            if (!trailerField.TryReadInt32(out var trailer) || trailer != 1)
            {
                return null;
            }

            trailerField.ThrowIfNotEmpty();
        }

        sequence.ThrowIfNotEmpty();
        return hashAlgorithm;
    }

    private static String? VerifyWithAlgorithm(ReadOnlySpan<Byte> tbsCertList,
                                               ReadOnlyMemory<Byte> signatureAlgorithm,
                                               Byte[] signature,
                                               X509Certificate2 issuer)
    {
        var algorithm = new AsnReader(signatureAlgorithm, AsnEncodingRules.DER).ReadSequence();
        var oid = algorithm.ReadObjectIdentifier();
        if (TimestampTokenValidation.IsWeakSignatureAlgorithm(oid))
        {
            return $"the CRL is signed with the weak algorithm {oid}; MD5 and SHA-1 are not accepted.";
        }

        var (keySize, minimum) = TimestampTokenValidation.GetKeySize(issuer);
        if (keySize is null || keySize < minimum)
        {
            return "the CRL issuer's key is not an RSA or ECDSA key of the required size.";
        }

        switch (oid)
        {
            case Sha256WithRsaOid or Sha384WithRsaOid or Sha512WithRsaOid:
            {
                if (algorithm.HasData)
                {
                    algorithm.ReadNull();
                }

                algorithm.ThrowIfNotEmpty();
                var hash = oid == Sha256WithRsaOid ? HashAlgorithmName.SHA256 : oid == Sha384WithRsaOid ? HashAlgorithmName.SHA384 : HashAlgorithmName.SHA512;
                using var key = issuer.GetRSAPublicKey();
                return key is not null && key.VerifyData(tbsCertList, signature, hash, RSASignaturePadding.Pkcs1) ? null : InvalidSignature();
            }
            case RsaPssOid:
            {
                if (ReadPssParameters(algorithm) is not { } hash)
                {
                    return "the CRL's RSASSA-PSS parameters are not supported.";
                }

                using var key = issuer.GetRSAPublicKey();
                return key is not null && key.VerifyData(tbsCertList, signature, hash, RSASignaturePadding.Pss) ? null : InvalidSignature();
            }
            case EcdsaWithSha256Oid or EcdsaWithSha384Oid or EcdsaWithSha512Oid:
            {
                algorithm.ThrowIfNotEmpty();
                var hash = oid == EcdsaWithSha256Oid ? HashAlgorithmName.SHA256 : oid == EcdsaWithSha384Oid ? HashAlgorithmName.SHA384 : HashAlgorithmName.SHA512;
                using var key = issuer.GetECDsaPublicKey();
                return key is not null && key.VerifyData(tbsCertList, signature, hash, DSASignatureFormat.Rfc3279DerSequence) ? null : InvalidSignature();
            }
            default:
                return $"the CRL's signature algorithm {oid} is not supported.";
        }

        static String InvalidSignature() => "the CRL's signature does not verify with its issuer's key.";
    }
}
