// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace BuildPipeline;

using JetBrains.Annotations;
using Nuke.Common;
using Nuke.Common.IO;
using Serilog;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

internal partial class BuildPipeline
{
    /// <summary>
    /// The public key token of every released assembly. Hard-coded so that a swapped committed key cannot pass verification.
    /// </summary>
    private const String ReleasedPublicKeyToken = "258e5594a5dc8a36";

    /// <summary>
    /// The public key file used for public signing by ordinary builds. It holds no private key.
    /// </summary>
    private static AbsolutePath PublicKeyFile => RootDirectory / "Chaos.Mongo.Public.snk";

    /// <summary>
    /// The private key file that the release workflow decodes from the <c>CHAOSMONGO_SNK</c> secret. Ignored by Git.
    /// </summary>
    private static AbsolutePath ReleaseKeyFile => RootDirectory / "Chaos.Mongo.snk";

    /// <summary>
    /// Verifies the packages already present in the publish directory without packing.
    /// </summary>
    [UsedImplicitly]
    private Target VerifyPackages => target =>
        target.After(Pack)
              .Executes(VerifyPackageSignatures);

    private static void AssertReleaseKeyFileExists()
    {
        if (!ReleaseKeyFile.FileExists() || (new FileInfo(ReleaseKeyFile).Length == 0))
        {
            Assert.Fail($"The release signing key '{ReleaseKeyFile}' is missing or empty. " +
                        "Pack signs fully with the private key and never falls back to public signing.");
        }
    }

    private static Byte[] ComputeStrongNameHash(Byte[] image, PEHeaders headers, PEMagic magic, Int32 signatureOffset, Int32 signatureSize)
    {
        // The hash covers the PE headers up to the end of the section table, with the checksum and the
        // certificate table entry zeroed, followed by the raw data of every section except the signature blob.
        var optionalHeaderOffset = headers.PEHeaderStartOffset;
        var headerLength = optionalHeaderOffset + headers.CoffHeader.SizeOfOptionalHeader + (40 * headers.SectionHeaders.Length);
        var header = image.AsSpan(0, headerLength).ToArray();

        header.AsSpan(optionalHeaderOffset + 64, 4).Clear();
        var dataDirectoriesOffset = optionalHeaderOffset + (magic == PEMagic.PE32 ? 96 : 112);
        header.AsSpan(dataDirectoriesOffset + (4 * 8), 8).Clear();

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(header);

        foreach (var section in headers.SectionHeaders)
        {
            var start = section.PointerToRawData;
            var end = start + section.SizeOfRawData;

            if ((signatureOffset >= start) && (signatureOffset < end))
            {
                hash.AppendData(image.AsSpan(start, signatureOffset - start));
                hash.AppendData(image.AsSpan(signatureOffset + signatureSize, end - signatureOffset - signatureSize));
            }
            else
            {
                hash.AppendData(image.AsSpan(start, section.SizeOfRawData));
            }
        }

        return hash.GetHashAndReset();
    }

    /// <summary>
    /// Returns why an assembly is not fully signed with the released key, or <c>null</c> if it is.
    /// </summary>
    /// <remarks>
    /// The <c>StrongNameSigned</c> CLI flag is set for public-signed assemblies too, so only a cryptographic check of
    /// the signature proves a full signature.
    /// </remarks>
    private static String? GetStrongNameFailure(Byte[] image, Byte[] expectedPublicKey)
    {
        using var peReader = new PEReader(new MemoryStream(image));

        if (!peReader.HasMetadata || peReader.PEHeaders is not { PEHeader: { } peHeader, CorHeader: { } corHeader })
            return "it is not a .NET assembly";

        var metadata = peReader.GetMetadataReader();
        var publicKey = metadata.GetBlobBytes(metadata.GetAssemblyDefinition().PublicKey);
        var token = PublicKeyTokenCheck.ComputeToken(publicKey);

        if (token != ReleasedPublicKeyToken)
            return $"its public key token is '{token}' instead of '{ReleasedPublicKeyToken}'";

        if (!publicKey.AsSpan().SequenceEqual(expectedPublicKey))
            return $"its public key differs from '{PublicKeyFile.Name}'";

        var signatureDirectory = corHeader.StrongNameSignatureDirectory;

        if ((signatureDirectory.Size == 0) ||
            !peReader.PEHeaders.TryGetDirectoryOffset(signatureDirectory, out var signatureOffset))
            return "it has no strong-name signature";

        var signature = image.AsSpan(signatureOffset, signatureDirectory.Size).ToArray();

        if (signature.All(b => b == 0))
            return "it is public-signed: the strong-name signature is empty";

        // The stored signature is little-endian; RSA verification expects big-endian.
        Array.Reverse(signature);
        var hash = ComputeStrongNameHash(image, peReader.PEHeaders, peHeader.Magic, signatureOffset, signatureDirectory.Size);

        using var rsa = new RSACryptoServiceProvider();
        // The strong-name public key blob starts with a 12-byte header that precedes the CSP public key blob.
        rsa.ImportCspBlob(expectedPublicKey[12..]);

        if (signature.Length != (rsa.KeySize / 8))
            return $"its strong-name signature has {signature.Length} bytes instead of {rsa.KeySize / 8}";

        return rsa.VerifyHash(hash, signature, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1)
            ? null
            : "its strong-name signature is not valid for the released public key";
    }

    private static Byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Reads every assembly under <c>lib/</c> of every <c>*.nupkg</c> in <paramref name="directory"/>, appending a
    /// failure to <paramref name="failures"/> for a directory with no packages or a package with no assemblies.
    /// </summary>
    private static List<(AbsolutePath Package, String AssemblyName, Byte[] Image)> ReadPackageAssemblies(AbsolutePath directory, List<String> failures)
    {
        var result = new List<(AbsolutePath Package, String AssemblyName, Byte[] Image)>();
        var packages = directory.GlobFiles("*.nupkg");

        if (packages.Count == 0)
            failures.Add($"No packages found in '{directory}'.");

        foreach (var package in packages)
        {
            using var archive = ZipFile.OpenRead(package);
            var assemblies = archive.Entries
                                    .Where(e => e.FullName.StartsWith("lib/", StringComparison.Ordinal) &&
                                                e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                                    .ToList();

            if (assemblies.Count == 0)
                failures.Add($"{package}: contains no assemblies under lib/.");

            foreach (var assembly in assemblies)
                result.Add((package, assembly.FullName, ReadEntry(assembly)));
        }

        return result;
    }

    /// <summary>
    /// Fails unless every assembly in every package in <paramref name="directory"/> carries the released public key
    /// token. Unlike <see cref="VerifyPackageSignatures"/>, this does not need the private key: a public-signed
    /// assembly has no usable strong-name signature, so only the token is checked.
    /// </summary>
    private static void VerifyPublicKeyTokens(AbsolutePath directory)
    {
        var failures = new List<String>();

        foreach (var (package, assemblyName, image) in ReadPackageAssemblies(directory, failures))
        {
            var failure = PublicKeyTokenCheck.GetMismatch(image, ReleasedPublicKeyToken);

            if (failure is null)
                Log.Information("Verified public key token of {Package}: {Assembly}", package.Name, assemblyName);
            else
                failures.Add($"{package}: {assemblyName}: {failure}.");
        }

        if (failures.Count > 0)
        {
            Assert.Fail($"Public key token verification failed for '{directory}'." +
                        Environment.NewLine + String.Join(Environment.NewLine, failures));
        }
    }

    /// <summary>
    /// Deletes the configuration-specific build outputs of the source projects, which forces Pack to recompile them.
    /// </summary>
    /// <remarks>
    /// MSBuild does not recompile when only signing properties change, so a Pack after an ordinary Build or Test
    /// would otherwise package the public-signed assemblies from <c>obj/</c>. The restore assets in <c>obj/</c>
    /// itself stay in place.
    /// </remarks>
    private void ClearSourceBuildOutputs()
    {
        foreach (var project in SourceDirectory.GlobFiles("**/*.csproj"))
        {
            (project.Parent / "obj" / TargetBuildConfiguration).DeleteDirectory();
            (project.Parent / "bin" / TargetBuildConfiguration).DeleteDirectory();
        }
    }

    /// <summary>
    /// Fails unless every assembly in every package in the publish directory is fully signed with the released key.
    /// </summary>
    /// <remarks>
    /// Covers every package that Publish would push, including packages that an earlier local Pack left behind.
    /// </remarks>
    private void VerifyPackageSignatures()
    {
        var expectedPublicKey = PublicKeyFile.ReadAllBytes();
        var failures = new List<String>();

        foreach (var (package, assemblyName, image) in ReadPackageAssemblies(PublishDirectory, failures))
        {
            var failure = GetStrongNameFailure(image, expectedPublicKey);

            if (failure is null)
                Log.Information("Verified strong-name signature of {Package}: {Assembly}", package.Name, assemblyName);
            else
                failures.Add($"{package}: {assemblyName}: {failure}.");
        }

        if (failures.Count > 0)
        {
            Assert.Fail($"Package signature verification failed for '{PublishDirectory}'. It also checks packages " +
                        "that an earlier local Pack left there; run Clean if only those fail." +
                        Environment.NewLine + String.Join(Environment.NewLine, failures));
        }
    }
}
