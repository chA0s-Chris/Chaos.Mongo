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
    /// Gets the roots the authority's certificate chains must lead to. When empty, the system trust store is used, as
    /// when tokens are received.
    /// </summary>
    /// <remarks>
    /// Configure the authority's roots explicitly. Verification compares the chain it builds with the chain stored when
    /// the token was received, and the system trust store changes over the years: a chain built later may take another
    /// path, for example through a cross-signed root, so a genuine token would no longer verify.
    /// </remarks>
    public X509Certificate2Collection TrustedRoots { get; } = [];
}
