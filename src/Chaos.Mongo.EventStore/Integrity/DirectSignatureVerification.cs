// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

/// <summary>
/// Verifies a DER-encoded signed value's signature against a candidate signer's public key, without building or
/// validating a certificate chain. Used both for CRLs (<see cref="CertificateRevocationListSignature"/>) and to test
/// direct cryptographic issuer adjacency between two certificates (<see cref="TimestampTokenValidation"/>'s path
/// search for <see cref="TimestampAuthorityTrust"/>), so neither carries the final-target-dependent semantics a full
/// chain validation call would.
/// </summary>
/// <remarks>
/// The accepted algorithms follow the policy for timestamp tokens: RSA with PKCS #1 v1.5 or PSS padding and ECDSA,
/// each with SHA-256, SHA-384 or SHA-512, and keys of at least 2048 bits for RSA and 256 bits for ECDSA. MD5 and
/// SHA-1 are rejected.
/// </remarks>
internal static class DirectSignatureVerification
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
    /// Gets a value indicating whether a certificate is directly, cryptographically issued by a candidate: the
    /// candidate's subject matches the certificate's issuer name (<see cref="TimestampTokenValidation.NamesMatch"/>),
    /// and the certificate's signature verifies under the candidate's public key. This is a single signature check,
    /// not a chain-validation call, so it carries none of a full validation's constraint propagation or
    /// final-target-dependent semantics.
    /// </summary>
    /// <param name="certificate">The certificate to test.</param>
    /// <param name="candidateIssuer">The candidate issuer.</param>
    /// <returns><c>true</c> when the candidate directly, cryptographically issued the certificate.</returns>
    public static Boolean IsDirectIssuer(X509Certificate2 certificate, X509Certificate2 candidateIssuer)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(candidateIssuer);
        if (!TimestampTokenValidation.NamesMatch(certificate.IssuerName, candidateIssuer.SubjectName))
        {
            return false;
        }

        try
        {
            // Certificate ::= SEQUENCE { tbsCertificate TBSCertificate, signatureAlgorithm AlgorithmIdentifier, signatureValue BIT STRING }
            var reader = new AsnReader(certificate.RawData, AsnEncodingRules.DER);
            var outer = reader.ReadSequence();
            var tbsCertificate = outer.ReadEncodedValue();
            var signatureAlgorithm = outer.ReadEncodedValue();
            var signature = outer.ReadBitString(out var unusedBits);
            return unusedBits == 0 && VerifySignedData(tbsCertificate.Span, signatureAlgorithm, signature, candidateIssuer) is null;
        }
        catch (AsnContentException)
        {
            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// Verifies a signed value's signature against a signer's public key.
    /// </summary>
    /// <param name="signedData">The signed data, exactly as covered by the signature.</param>
    /// <param name="signatureAlgorithm">The encoded <c>AlgorithmIdentifier</c> of the signature.</param>
    /// <param name="signature">The raw signature bytes.</param>
    /// <param name="signer">The signer's certificate.</param>
    /// <returns><c>null</c> when the signature is valid; otherwise why it is not accepted.</returns>
    /// <exception cref="AsnContentException">Thrown when <paramref name="signatureAlgorithm"/> is malformed.</exception>
    public static String? VerifySignedData(ReadOnlySpan<Byte> signedData, ReadOnlyMemory<Byte> signatureAlgorithm, Byte[] signature, X509Certificate2 signer)
    {
        ArgumentNullException.ThrowIfNull(signer);
        var algorithm = new AsnReader(signatureAlgorithm, AsnEncodingRules.DER).ReadSequence();
        var oid = algorithm.ReadObjectIdentifier();
        if (TimestampTokenValidation.IsWeakSignatureAlgorithm(oid))
        {
            return $"the signature uses the weak algorithm {oid}; MD5 and SHA-1 are not accepted.";
        }

        var (keySize, minimum) = TimestampTokenValidation.GetKeySize(signer);
        if (keySize is null || keySize < minimum)
        {
            return "the signer's key is not an RSA or ECDSA key of the required size.";
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
                using var key = signer.GetRSAPublicKey();
                return key is not null && key.VerifyData(signedData, signature, hash, RSASignaturePadding.Pkcs1) ? null : InvalidSignature();
            }
            case RsaPssOid:
            {
                if (ReadPssParameters(algorithm) is not { } hash)
                {
                    return "the RSASSA-PSS parameters are not supported.";
                }

                using var key = signer.GetRSAPublicKey();
                return key is not null && key.VerifyData(signedData, signature, hash, RSASignaturePadding.Pss) ? null : InvalidSignature();
            }
            case EcdsaWithSha256Oid or EcdsaWithSha384Oid or EcdsaWithSha512Oid:
            {
                algorithm.ThrowIfNotEmpty();
                var hash = oid == EcdsaWithSha256Oid ? HashAlgorithmName.SHA256 : oid == EcdsaWithSha384Oid ? HashAlgorithmName.SHA384 : HashAlgorithmName.SHA512;
                using var key = signer.GetECDsaPublicKey();
                return key is not null && key.VerifyData(signedData, signature, hash, DSASignatureFormat.Rfc3279DerSequence) ? null : InvalidSignature();
            }
            default:
                return $"the signature algorithm {oid} is not supported.";
        }

        static String InvalidSignature() => "the signature does not verify with the signer's key.";
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
}
