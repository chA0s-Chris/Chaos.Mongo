// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity;

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

/// <summary>
/// A throwaway self-signed certificate for registration tests that only need a non-empty
/// <c>TrustedRoots</c> collection, not a validated chain.
/// </summary>
internal static class TestRootCertificate
{
    /// <summary>
    /// Creates a new self-signed certificate suitable for <c>TrustedRoots</c>.
    /// </summary>
    public static X509Certificate2 Create()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Chaos.Mongo Test Root", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}
