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
    /// Gets or sets the name of the collection that holds the chunks of evidence items too large to store inline.
    /// Defaults to <c>"IntegrityAnchorEvidenceChunks"</c>.
    /// </summary>
    public String AnchorEvidenceChunksCollectionName { get; set; } = "IntegrityAnchorEvidenceChunks";

    /// <summary>
    /// Gets or sets the name of the collection that holds the tokens, certificates and CRLs of anchor timestamps,
    /// addressed by their SHA-256. Defaults to <c>"IntegrityAnchorEvidence"</c>.
    /// </summary>
    public String AnchorEvidenceCollectionName { get; set; } = "IntegrityAnchorEvidence";

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
    /// Gets or sets the age of the oldest unanchored candidate above which a run reports the anchor delay through a
    /// log entry and a metric. Defaults to 1 hour.
    /// </summary>
    public TimeSpan DelayThreshold { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Gets or sets the interval between anchoring runs. Defaults to 5 minutes.
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets the number of leaves after which a run stops reading candidates; the remaining candidates go
    /// into the next run's anchor. A run also stops after reading as many batches as this maximum divided by
    /// <see cref="CandidateReadBatchSize"/>, so continuous changes to few streams, or changes that yield no candidate,
    /// cannot keep it reading. The run checks both between batches, so an anchor may exceed the maximum by less than
    /// one batch. Defaults to 100,000.
    /// </summary>
    public Int32 MaxLeavesPerAnchor { get; set; } = 100_000;

    /// <summary>
    /// Gets or sets the number of valid timestamp tokens an anchor needs to complete. Defaults to 1.
    /// </summary>
    public Int32 MinimumTimestampTokens { get; set; } = 1;
}
