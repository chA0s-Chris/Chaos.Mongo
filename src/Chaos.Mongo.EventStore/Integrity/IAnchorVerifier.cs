// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// Verifies the chain of anchors, which covers every protected aggregate type. Registered by the anchoring and by the
/// verification configuration.
/// </summary>
public interface IAnchorVerifier
{
    /// <summary>
    /// Verifies that committed anchor sequence numbers are contiguous from 1, that each anchor's previous digest equals
    /// its predecessor's digest, and that each digest recomputes from its stored fields.
    /// </summary>
    /// <param name="trustedHead">
    /// An anchor kept outside the database that the chain must contain unchanged, or <c>null</c> to check consistency
    /// only.
    /// </param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The result, with the first broken sequence number if any.</returns>
    /// <exception cref="ArgumentException">Thrown when the trusted head has no positive sequence number or no SHA-256 digest.</exception>
    Task<AnchorChainVerificationResult> VerifyAnchorChainAsync(TrustedAnchorHead? trustedHead = null, CancellationToken cancellationToken = default);
}
