// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// The result of verifying one anchor: its digest and its stored timestamp tokens.
/// </summary>
/// <param name="DigestIsValid">Whether the digest recomputed from the anchor's stored fields matches its stored digest.</param>
/// <param name="Timestamps">The result for each stored timestamp token, in the anchor's order.</param>
internal sealed record AnchorVerificationResult(Boolean DigestIsValid, IReadOnlyList<TimestampVerificationResult> Timestamps)
{
    /// <summary>
    /// Gets a value indicating whether the anchor is verified: its digest is valid and at least one token is valid.
    /// </summary>
    public Boolean IsVerified => DigestIsValid && Timestamps.Any(timestamp => timestamp.IsValid);
}
