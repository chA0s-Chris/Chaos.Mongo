// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity;

using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

/// <summary>
/// Signs certificates with ECDSA and SHA-1 (<c>ecdsa-with-SHA1</c>), which the built-in generators refuse, so tests
/// and vectors can hold a certificate with a genuine signature of an algorithm the chain policy rejects.
/// </summary>
internal sealed class Sha1EcdsaSignatureGenerator : X509SignatureGenerator
{
    private const String EcdsaWithSha1Oid = "1.2.840.10045.4.1";
    private readonly ECDsa _key;

    public Sha1EcdsaSignatureGenerator(ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key);
        _key = key;
    }

    public override Byte[] GetSignatureAlgorithmIdentifier(HashAlgorithmName hashAlgorithm)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier(EcdsaWithSha1Oid);
        }

        return writer.Encode();
    }

    public override Byte[] SignData(Byte[] data, HashAlgorithmName hashAlgorithm)
        => _key.SignData(data, HashAlgorithmName.SHA1, DSASignatureFormat.Rfc3279DerSequence);

    protected override PublicKey BuildPublicKey() => CreateForECDsa(_key).PublicKey;
}
