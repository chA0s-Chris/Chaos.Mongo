// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// Module-level anchoring settings shared by all protected aggregate types, because one global anchor
/// covers all of them.
/// </summary>
internal sealed class IntegrityAnchoringOptions
{
    /// <summary>
    /// Gets or sets the name of the collection that holds the leaves and inclusion proofs of anchors.
    /// Defaults to <c>"IntegrityAnchorLeaves"</c>.
    /// </summary>
    public String AnchorLeavesCollectionName { get; set; } = "IntegrityAnchorLeaves";

    /// <summary>
    /// Gets or sets the name of the collection that holds the anchors.
    /// Defaults to <c>"IntegrityAnchors"</c>.
    /// </summary>
    public String AnchorsCollectionName { get; set; } = "IntegrityAnchors";

    /// <summary>
    /// Gets or sets the number of candidates an anchoring run reads per batch. Defaults to 1,000.
    /// </summary>
    public Int32 CandidateReadBatchSize { get; set; } = 1_000;

    /// <summary>
    /// Gets or sets the name of the collection that holds the change-stream position.
    /// Defaults to <c>"IntegrityChangeStreamState"</c>.
    /// </summary>
    public String ChangeStreamStateCollectionName { get; set; } = "IntegrityChangeStreamState";

    /// <summary>
    /// Gets or sets the number of leaves after which a run stops reading candidates; the remaining candidates go
    /// into the next run's anchor. A run also stops after reading that many candidates, so continuous changes to few
    /// streams cannot keep it reading. The run checks both between batches, so an anchor may exceed the maximum by
    /// less than one batch. Defaults to 100,000.
    /// </summary>
    public Int32 MaxLeavesPerAnchor { get; set; } = 100_000;

    /// <summary>
    /// Gets or sets the number of valid timestamp tokens an anchor needs to complete. Defaults to 1.
    /// </summary>
    public Int32 MinimumTimestampTokens { get; set; } = 1;
}
