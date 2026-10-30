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
    /// Gets the name of the collection that holds the chunks of evidence items too large to store inline.
    /// Derived from <see cref="CollectionPrefix"/>.
    /// </summary>
    public String AnchorEvidenceChunksCollectionName => $"{CollectionPrefix}AnchorEvidenceChunks";

    /// <summary>
    /// Gets the name of the collection that holds the tokens, certificates and CRLs of anchor timestamps, addressed
    /// by their SHA-256. Derived from <see cref="CollectionPrefix"/>.
    /// </summary>
    public String AnchorEvidenceCollectionName => $"{CollectionPrefix}AnchorEvidence";

    /// <summary>
    /// Gets the name of the collection that holds the leaves and inclusion proofs of anchors. Derived from
    /// <see cref="CollectionPrefix"/>.
    /// </summary>
    public String AnchorLeavesCollectionName => $"{CollectionPrefix}AnchorLeaves";

    /// <summary>
    /// Gets the name of the collection that holds the anchors. Derived from <see cref="CollectionPrefix"/>.
    /// </summary>
    public String AnchorsCollectionName => $"{CollectionPrefix}Anchors";

    /// <summary>
    /// Gets or sets the number of candidates an anchoring run reads per batch. Defaults to 1,000.
    /// </summary>
    public Int32 CandidateReadBatchSize { get; set; } = 1_000;

    /// <summary>
    /// Gets the name of the collection that holds the change-stream position. Derived from
    /// <see cref="CollectionPrefix"/>.
    /// </summary>
    public String ChangeStreamStateCollectionName => $"{CollectionPrefix}ChangeStreamState";

    /// <summary>
    /// Gets or sets the prefix of the five module-level anchoring collection names. Defaults to <c>"Integrity"</c>.
    /// </summary>
    public String CollectionPrefix { get; set; } = "Integrity";

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

    /// <summary>
    /// Gets or sets the age of the oldest timestamp token with an unconfirmed revocation status above which a run
    /// reports the confirmation delay through a log entry and a metric. It must exceed
    /// <see cref="RevocationGracePeriod"/>. Defaults to 72 hours, which covers the default grace period plus a daily
    /// CRL interval with a margin.
    /// </summary>
    public TimeSpan RevocationConfirmationDelayThreshold { get; set; } = TimeSpan.FromHours(72);

    /// <summary>
    /// Gets or sets the time after a token's time that the CRLs confirming its revocation status must be issued at or
    /// after, so revocations requested shortly before the token's time have reached them; confirmation starts once
    /// it has passed. Defaults to 24 hours.
    /// </summary>
    public TimeSpan RevocationGracePeriod { get; set; } = TimeSpan.FromHours(24);
}
