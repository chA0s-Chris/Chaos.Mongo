// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using System.Security.Cryptography.X509Certificates;

/// <summary>
/// The trust settings a token's certificate chain is built with.
/// </summary>
/// <param name="TrustedRoots">The trusted roots; when empty, the system trust store is used.</param>
/// <param name="RevocationMode">How the chain's revocation status is checked.</param>
/// <param name="RevocationCheckTimeout">The time an online revocation check may take.</param>
/// <param name="ExtraCertificates">Further certificates the chain may be built from, besides the token's.</param>
internal sealed record ChainSettings(
    X509Certificate2Collection TrustedRoots,
    X509RevocationMode RevocationMode,
    TimeSpan RevocationCheckTimeout,
    X509Certificate2Collection ExtraCertificates);
