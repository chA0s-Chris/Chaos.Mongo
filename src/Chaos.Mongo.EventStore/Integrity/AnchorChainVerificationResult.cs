// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// The result of verifying the chain of anchors.
/// </summary>
public sealed record AnchorChainVerificationResult
{
    /// <summary>
    /// Gets the number of anchors verified before the chain ended or broke.
    /// </summary>
    public required Int64 AnchorCount { get; init; }

    /// <summary>
    /// Gets why the first broken sequence number failed, or <c>null</c> when the chain is intact.
    /// </summary>
    public AnchorChainFailure? Failure { get; init; }

    /// <summary>
    /// Gets the first sequence number whose verification failed, or <c>null</c> when the chain is intact.
    /// </summary>
    public Int64? FirstBrokenSequence { get; init; }

    /// <summary>
    /// Gets a value indicating whether the chain of anchors is intact.
    /// </summary>
    public Boolean IsIntact => FirstBrokenSequence is null;
}
