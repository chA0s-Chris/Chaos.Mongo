// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// The result of verifying one stored timestamp token of an anchor.
/// </summary>
public sealed record TimestampVerificationResult
{
    /// <summary>
    /// Gets the name of the authority that issued the token.
    /// </summary>
    public required String Authority { get; init; }

    /// <summary>
    /// Gets why the token is not valid, or <c>null</c> when it is.
    /// </summary>
    public String? Failure { get; init; }

    /// <summary>
    /// Gets whether the token is valid evidence for the anchor digest.
    /// </summary>
    public required Boolean IsValid { get; init; }

    /// <summary>
    /// Gets the same recomputed revocation status as <see cref="RevocationStatus"/>, exposed separately from it so a
    /// caller can read "what today's rule computes from the stored evidence" under its own name, apart from
    /// <see cref="StoredRevocationStatus"/>'s historical value. The two values can diverge: a chain whose non-root
    /// certificates genuinely name no CRL distribution point is stored as <see cref="TimestampRevocationStatus.Confirmed"/>
    /// under the rule in effect before this status existed, stays cryptographically valid, and is not retroactively
    /// invalidated, but this field reports <see cref="TimestampRevocationStatus.NoRevocationSource"/> for it, so a
    /// caller can tell "there was nothing to check" apart from "it was genuinely confirmed," without the library
    /// claiming a stronger historical guarantee than was actually recorded.
    /// </summary>
    public required TimestampRevocationStatus RecomputedRevocationStatus { get; init; }

    /// <summary>
    /// Gets the revocation status of the token's chain at the token's time, recomputed from the stored confirming
    /// CRLs, used for the revoked/valid decision: only <see cref="TimestampRevocationStatus.Revoked"/> invalidates the
    /// token. Equal to <see cref="RecomputedRevocationStatus"/>.
    /// </summary>
    public required TimestampRevocationStatus RevocationStatus { get; init; }

    /// <summary>
    /// Gets the revocation status stored on the anchor, for comparison only; anyone who can write the anchors can
    /// change it.
    /// </summary>
    public required TimestampRevocationStatus StoredRevocationStatus { get; init; }

    /// <summary>
    /// Gets the token's time as stored on the anchor.
    /// </summary>
    public required DateTime TimestampUtc { get; init; }
}
