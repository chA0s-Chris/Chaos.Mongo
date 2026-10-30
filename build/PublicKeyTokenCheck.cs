// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace BuildPipeline;

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

/// <summary>
/// Checks an assembly's public-key token without needing the private signing key, so a secret-less CI
/// run can still detect a package built with the wrong key. Has no dependency on Nuke, so it can be
/// linked into a test project and exercised without running a pack.
/// </summary>
internal static class PublicKeyTokenCheck
{
    public static String ComputeToken(Byte[] publicKey)
        => Convert.ToHexStringLower(SHA1.HashData(publicKey)[^8..].Reverse().ToArray());

    /// <summary>
    /// Returns why an assembly's public key token does not equal <paramref name="expectedToken"/>, or
    /// <c>null</c> if it does.
    /// </summary>
    public static String? GetMismatch(Byte[] assemblyImage, String expectedToken)
    {
        using var peReader = new PEReader(new MemoryStream(assemblyImage));

        try
        {
            if (!peReader.HasMetadata)
                return "it is not a .NET assembly";
        }
        catch (BadImageFormatException)
        {
            return "it is not a .NET assembly";
        }

        var metadata = peReader.GetMetadataReader();
        var publicKey = metadata.GetBlobBytes(metadata.GetAssemblyDefinition().PublicKey);

        if (publicKey.Length == 0)
            return "it is not strong-named: it has no public key";

        var token = ComputeToken(publicKey);

        return token == expectedToken
            ? null
            : $"its public key token is '{token}' instead of '{expectedToken}'";
    }
}
