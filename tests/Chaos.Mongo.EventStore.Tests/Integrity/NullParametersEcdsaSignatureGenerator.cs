// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity;

using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

/// <summary>
/// Signs certificates with ECDSA and SHA-256 but writes <c>NULL</c> parameters into the algorithm identifier, which
/// <c>ecdsa-with-SHA256</c> must not carry, so tests and vectors can hold a certificate with a genuine signature whose
/// algorithm identifier the chain policy rejects.
/// </summary>
internal sealed class NullParametersEcdsaSignatureGenerator : X509SignatureGenerator
{
    private const String EcdsaWithSha256Oid = "1.2.840.10045.4.3.2";
    private readonly ECDsa _key;

    public NullParametersEcdsaSignatureGenerator(ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key);
        _key = key;
    }

    public override Byte[] GetSignatureAlgorithmIdentifier(HashAlgorithmName hashAlgorithm)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier(EcdsaWithSha256Oid);
            writer.WriteNull();
        }

        return writer.Encode();
    }

    public override Byte[] SignData(Byte[] data, HashAlgorithmName hashAlgorithm)
        => _key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

    protected override PublicKey BuildPublicKey() => CreateForECDsa(_key).PublicKey;
}
