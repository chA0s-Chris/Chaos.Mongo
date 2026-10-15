// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// A timestamp entry as stored on an anchor: the token, the evidence captured when it was obtained and the CRLs that
/// later confirm its chain's revocation status are referenced by their SHA-256 in the <see cref="AnchorEvidenceStore"/>,
/// so the entry's size does not depend on theirs. RFC 4998 renewal can later append re-timestamping entries to an
/// anchor's list.
/// </summary>
/// <param name="Authority">The name of the authority that issued the token.</param>
/// <param name="TokenHash">The SHA-256 of the raw RFC 3161 token.</param>
/// <param name="CertificateChainHashes">The SHA-256 of each certificate, from the signing certificate to the root.</param>
/// <param name="CertificateRevocationListHashes">The SHA-256 of each CRL captured for the chain, in capture order.</param>
/// <param name="TimestampUtc">The time the authority asserts in the token.</param>
/// <param name="ObtainedUtc">The time the token was obtained.</param>
internal sealed record AnchorTimestampReference(
    String Authority,
    Byte[] TokenHash,
    IReadOnlyList<Byte[]> CertificateChainHashes,
    IReadOnlyList<Byte[]> CertificateRevocationListHashes,
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
    /// Gets the SHA-256 of each CRL, issued at least a grace period after the token's time, that confirms the revocation
    /// status of the chain; empty while the status is unconfirmed or when the chain names no distribution points.
    /// </summary>
    public IReadOnlyList<Byte[]> ConfirmingRevocationListHashes { get; init; } = [];

    /// <summary>
    /// Gets the revocation status of the token's chain at the token's time.
    /// </summary>
    public TimestampRevocationStatus RevocationStatus { get; init; } = TimestampRevocationStatus.Unconfirmed;

    /// <summary>
    /// Gets the time the revocation status was recorded, or <c>null</c> while it is unconfirmed.
    /// </summary>
    public DateTime? RevocationStatusUtc { get; init; }
}
