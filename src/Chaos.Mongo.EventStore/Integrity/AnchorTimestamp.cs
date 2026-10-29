// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// A timestamp token of an anchor with the evidence captured when it was obtained and, once confirmed, the CRLs that
/// confirm its chain's revocation status, resolved from the evidence store so a handed-out anchor is self-contained.
/// </summary>
/// <remarks>
/// Records compare byte arrays and lists by reference, so compare single byte arrays with <c>SequenceEqual</c> and lists
/// of them, such as the certificate chain, item by item, since <c>SequenceEqual</c> on a list of byte arrays compares
/// references too.
/// </remarks>
public sealed record AnchorTimestamp
{
    /// <summary>
    /// Gets the name of the authority that issued the token.
    /// </summary>
    public required String Authority { get; init; }

    /// <summary>
    /// Gets the DER-encoded certificates from the signing certificate to the root.
    /// </summary>
    public required IReadOnlyList<Byte[]> CertificateChain { get; init; }

    /// <summary>
    /// Gets the CRLs captured for the chain.
    /// </summary>
    public required IReadOnlyList<Byte[]> CertificateRevocationLists { get; init; }

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
    /// Gets the time the token was obtained.
    /// </summary>
    public required DateTime ObtainedUtc { get; init; }

    /// <summary>
    /// Gets the revocation status of the token's chain at the token's time.
    /// </summary>
    public TimestampRevocationStatus RevocationStatus { get; init; } = TimestampRevocationStatus.Unconfirmed;

    /// <summary>
    /// Gets the time the revocation status was recorded, or <c>null</c> while it is unconfirmed.
    /// </summary>
    public DateTime? RevocationStatusUtc { get; init; }

    /// <summary>
    /// Gets the time the authority asserts in the token.
    /// </summary>
    public required DateTime TimestampUtc { get; init; }

    /// <summary>
    /// Gets the raw RFC 3161 token.
    /// </summary>
    public required Byte[] Token { get; init; }
}
