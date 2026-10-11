// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// The candidates returned by one read of an <see cref="IAnchorCandidateSource"/>, together with the
/// source-specific position that acknowledging the batch persists.
/// </summary>
internal sealed class AnchorCandidateBatch
{
    public AnchorCandidateBatch(IReadOnlyList<AnchorCandidate> candidates, Object position, Boolean hasMore)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(position);
        Candidates = candidates;
        Position = position;
        HasMore = hasMore;
    }

    /// <summary>
    /// Gets the candidates of the batch, possibly none.
    /// </summary>
    public IReadOnlyList<AnchorCandidate> Candidates { get; }

    /// <summary>
    /// Gets a value indicating whether more candidates are available right away, for example while a recovery
    /// scan is running. A batch can be empty and still have more to come.
    /// </summary>
    public Boolean HasMore { get; }

    /// <summary>
    /// Gets the opaque position of the source after this batch; only the source that created the batch reads it.
    /// </summary>
    public Object Position { get; }
}
