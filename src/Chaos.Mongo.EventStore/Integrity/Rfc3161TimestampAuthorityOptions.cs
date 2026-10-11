// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using System.Security.Cryptography.X509Certificates;

/// <summary>
/// Configures one <see cref="Rfc3161TimestampAuthority"/>.
/// </summary>
internal sealed class Rfc3161TimestampAuthorityOptions
{
    private static readonly HttpClient SharedHttpClient = new();

    /// <summary>
    /// Gets or sets the factory for the HTTP client used for timestamp requests and CRL downloads. Defaults to a
    /// client shared by all authorities without a factory.
    /// </summary>
    public Func<IServiceProvider, HttpClient> HttpClientFactory { get; set; } = _ => SharedHttpClient;

    /// <summary>
    /// Gets or sets the name that identifies the authority in stored anchors.
    /// </summary>
    public required String Name { get; set; }

    /// <summary>
    /// Gets or sets how certificate revocation is checked when the token's chain is validated. Defaults to
    /// <see cref="X509RevocationMode.Online"/>.
    /// </summary>
    public X509RevocationMode RevocationMode { get; set; } = X509RevocationMode.Online;

    /// <summary>
    /// Gets the roots the token's chain must end in. When empty, the operating system's trust store is used;
    /// qualified authorities often chain to roots outside it.
    /// </summary>
    public X509Certificate2Collection TrustedRoots { get; } = [];

    /// <summary>
    /// Gets or sets the URI that accepts <c>application/timestamp-query</c> requests.
    /// </summary>
    public required Uri Uri { get; set; }
}
