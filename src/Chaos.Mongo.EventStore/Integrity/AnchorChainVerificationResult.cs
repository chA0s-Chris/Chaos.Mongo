// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// The result of verifying the chain of anchors.
/// </summary>
/// <param name="FirstBrokenSequence">
/// The first sequence number whose verification failed, or <c>null</c> when the chain is
/// intact.
/// </param>
/// <param name="Failure">Why the first broken sequence number failed, or <c>null</c> when the chain is intact.</param>
/// <param name="AnchorCount">The number of anchors verified before the chain ended or broke.</param>
internal sealed record AnchorChainVerificationResult(Int64? FirstBrokenSequence, AnchorChainFailure? Failure, Int64 AnchorCount)
{
    /// <summary>
    /// Gets a value indicating whether the chain of anchors is intact.
    /// </summary>
    public Boolean IsIntact => FirstBrokenSequence is null;
}
