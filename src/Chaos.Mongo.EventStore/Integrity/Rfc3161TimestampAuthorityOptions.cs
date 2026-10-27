// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using System.Security.Cryptography.X509Certificates;

/// <summary>
/// Configures an RFC 3161 timestamp authority reached over HTTP. Its name and URI are the arguments of
/// <see cref="IntegrityAnchoringBuilder.WithRfc3161TimestampAuthority"/>.
/// </summary>
public sealed class Rfc3161TimestampAuthorityOptions
{
    private static readonly HttpClient SharedHttpClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    internal Rfc3161TimestampAuthorityOptions(String name, Uri uri)
    {
        Name = name;
        Uri = uri;
    }

    /// <summary>
    /// Gets or sets the longest time one timestamp attempt may spend on its HTTP work, the timestamp request and the
    /// CRL downloads together. Each request is also bounded by the HTTP client, but a chain naming several dead
    /// locations would otherwise stall an anchoring run for their sum. Defaults to 2 minutes.
    /// </summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Gets or sets the factory for the HTTP client used for timestamp requests and CRL downloads. Defaults to a
    /// client shared by all authorities without a factory, which times out after 30 seconds when a host does not
    /// answer, so a hung authority or CRL host does not stall an anchoring run for long. Response bodies are bounded
    /// by <see cref="MaximumResponseSize"/> and the attempt by <see cref="AttemptTimeout"/>, whatever client is used.
    /// </summary>
    public Func<IServiceProvider, HttpClient> HttpClientFactory { get; set; } = _ => SharedHttpClient;

    /// <summary>
    /// Gets or sets the largest difference between the time a token asserts and the local time. A token outside it is
    /// rejected, because its chain is validated at the asserted time. Defaults to 5 minutes.
    /// </summary>
    public TimeSpan MaximumClockSkew { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets the largest timestamp response or CRL, in bytes, that an attempt reads. The authority enforces it
    /// itself, because a client from <see cref="HttpClientFactory"/> may buffer up to 2 GB per response. A CRL above it
    /// counts as unavailable at that location. Defaults to 16 MB; raise it for a CA that publishes larger CRLs.
    /// </summary>
    public Int32 MaximumResponseSize { get; set; } = 16 * 1024 * 1024;

    /// <summary>
    /// Gets the name that identifies the authority in stored anchors.
    /// </summary>
    public String Name { get; }

    /// <summary>
    /// Gets or sets the object identifier of the TSA policy that tokens must be issued under, such as a qualified
    /// timestamping policy. It is requested with every timestamp, and a token under another policy is rejected
    /// (RFC 3161, section 2.2). When <c>null</c>, the authority's default policy is accepted.
    /// </summary>
    public String? PolicyId { get; set; }

    /// <summary>
    /// Gets or sets the longest time the chain validation may spend on one online revocation check or CRL download.
    /// The chain engine performs these outside the HTTP client, so a hung revocation host would otherwise block the
    /// timestamp attempt without limit. Defaults to 30 seconds.
    /// </summary>
    public TimeSpan RevocationCheckTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets how certificate revocation is checked when the token's chain is validated. Defaults to
    /// <see cref="X509RevocationMode.Online"/>.
    /// </summary>
    public X509RevocationMode RevocationMode { get; set; } = X509RevocationMode.Online;

    /// <summary>
    /// Gets the roots the token's chain must end in. When empty, the operating system's trust store is used;
    /// qualified authorities often chain to roots outside it. Explicit roots also keep stored tokens verifiable, because
    /// a chain built later from a changed trust store may take another path than the stored one.
    /// </summary>
    public X509Certificate2Collection TrustedRoots { get; } = [];

    /// <summary>
    /// Gets the absolute HTTP or HTTPS URI that accepts <c>application/timestamp-query</c> requests.
    /// </summary>
    public Uri Uri { get; }
}
