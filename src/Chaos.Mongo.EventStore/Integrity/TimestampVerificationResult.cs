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
    /// Gets the revocation status of the token's chain at the token's time, recomputed from the stored confirming
    /// CRLs.
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
