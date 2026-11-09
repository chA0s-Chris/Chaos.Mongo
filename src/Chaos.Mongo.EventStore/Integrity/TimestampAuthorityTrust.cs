// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using System.Security.Cryptography.X509Certificates;

/// <summary>
/// The trust settings stored timestamp tokens of one authority are verified with.
/// </summary>
public sealed class TimestampAuthorityTrust
{
    /// <summary>
    /// Creates empty trust settings; consumers receive them from
    /// <see cref="IntegrityVerificationBuilder.WithTimestampAuthorityTrust"/>.
    /// </summary>
    internal TimestampAuthorityTrust() { }

    /// <summary>
    /// Gets or sets the object identifier of the policy the authority's tokens must be issued under, or <c>null</c>
    /// to accept any policy.
    /// </summary>
    public String? PolicyId { get; set; }

    /// <summary>
    /// Gets the roots the authority's certificate chains must lead to.
    /// <see cref="IntegrityVerificationBuilder.WithTimestampAuthorityTrust"/> requires at least one; leaving this
    /// empty throws, so a deployment that forgot to configure roots can never silently trust the system trust store.
    /// </summary>
    /// <remarks>
    /// Configure the authority's roots explicitly. Verification no longer requires the chain it builds to equal the
    /// chain stored when the token was received element for element: the built chain may continue past the signer and
    /// every stored intermediate to a different, still-trusted root, for example through a cross-signed bridging
    /// certificate, while the stored chain's own recorded root is independently validated to truly terminate it. See
    /// <c>TimestampTokenValidation.BuildIsolatedChain</c> for the two checks this enables.
    /// </remarks>
    public X509Certificate2Collection TrustedRoots { get; } = [];
}
