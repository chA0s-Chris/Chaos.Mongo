// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// A timestamp token of an anchor with the evidence captured when it was obtained and, once confirmed, the CRLs that
/// confirm its chain's revocation status, resolved from the evidence store so a handed-out anchor is self-contained.
/// </summary>
/// <param name="Authority">The name of the authority that issued the token.</param>
/// <param name="Token">The raw RFC 3161 token.</param>
/// <param name="CertificateChain">The DER-encoded certificates from the signing certificate to the root.</param>
/// <param name="CertificateRevocationLists">The CRLs captured for the chain.</param>
/// <param name="TimestampUtc">The time the authority asserts in the token.</param>
/// <param name="ObtainedUtc">The time the token was obtained.</param>
public sealed record AnchorTimestamp(
    String Authority,
    Byte[] Token,
    IReadOnlyList<Byte[]> CertificateChain,
    IReadOnlyList<Byte[]> CertificateRevocationLists,
    DateTime TimestampUtc,
    DateTime ObtainedUtc)
{
    /// <summary>
    /// Gets the failed attempts to confirm the revocation status, or <c>null</c> when none failed.
    /// </summary>
    public TimestampConfirmationFailure? ConfirmationFailure { get; init; }

    /// <summary>
    /// Gets the time before which confirmation is not attempted again, because the CRLs available at the last attempt
    /// were issued too early or the attempt failed; <c>null</c> when no attempt was deferred.
    /// </summary>
    public DateTime? ConfirmationNotBeforeUtc { get; init; }

    /// <summary>
    /// Gets the CRLs, issued at least a grace period after the token's time, that confirm the revocation status of
    /// the chain; empty while the status is unconfirmed or when the chain names no distribution points.
    /// </summary>
    public IReadOnlyList<Byte[]> ConfirmingRevocationLists { get; init; } = [];

    /// <summary>
    /// Gets the revocation status of the token's chain at the token's time.
    /// </summary>
    public TimestampRevocationStatus RevocationStatus { get; init; } = TimestampRevocationStatus.Unconfirmed;

    /// <summary>
    /// Gets the time the revocation status was recorded, or <c>null</c> while it is unconfirmed.
    /// </summary>
    public DateTime? RevocationStatusUtc { get; init; }
}
