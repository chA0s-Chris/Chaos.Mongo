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
    /// Gets or sets the source of anchor candidates. Defaults to <see cref="AnchorCandidateSourceKind.None"/>.
    /// </summary>
    public AnchorCandidateSourceKind CandidateSource { get; set; }

    /// <summary>
    /// Gets or sets the name of the collection that holds the change-stream position.
    /// Defaults to <c>"IntegrityChangeStreamState"</c>.
    /// </summary>
    public String ChangeStreamStateCollectionName { get; set; } = "IntegrityChangeStreamState";

    /// <summary>
    /// Gets or sets the name of the pending-anchor collection. Defaults to <c>"IntegrityPendingAnchors"</c>.
    /// </summary>
    public String PendingAnchorsCollectionName { get; set; } = "IntegrityPendingAnchors";

    /// <summary>
    /// Gets a value indicating whether appends and retroactive sealing write pending-anchor entries.
    /// </summary>
    public Boolean WritesPendingAnchors => CandidateSource == AnchorCandidateSourceKind.PendingCollection;
}
