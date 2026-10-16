// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// The result of verifying one stored timestamp token of an anchor.
/// </summary>
/// <param name="Authority">The name of the authority that issued the token.</param>
/// <param name="TimestampUtc">The token's time as stored on the anchor.</param>
/// <param name="IsValid">Whether the token is valid evidence for the anchor digest.</param>
/// <param name="Failure">Why the token is not valid, or <c>null</c> when it is.</param>
/// <param name="RevocationStatus">
/// The revocation status of the token's chain at the token's time, recomputed from the stored confirming CRLs.
/// </param>
/// <param name="StoredRevocationStatus">
/// The revocation status stored on the anchor, for comparison only; anyone who can write the anchors can change it.
/// </param>
public sealed record TimestampVerificationResult(
    String Authority,
    DateTime TimestampUtc,
    Boolean IsValid,
    String? Failure,
    TimestampRevocationStatus RevocationStatus,
    TimestampRevocationStatus StoredRevocationStatus);
