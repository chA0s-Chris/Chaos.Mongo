// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// A finding of the completeness check.
/// </summary>
/// <remarks>
/// Which properties are populated depends on <see cref="Kind"/>; a property not listed for a kind stays at its default
/// (<c>null</c>):
/// <list type="bullet">
///     <item>
///         <description>
///         <see cref="CompletenessFindingKind.AnchorChainBroken"/> populates only <see cref="AnchorChain"/>.
///         </description>
///     </item>
///     <item>
///         <description>
///         <see cref="CompletenessFindingKind.Unanchored"/> populates <see cref="AggregateId"/>,
///         <see cref="FirstVersion"/>, <see cref="LastVersion"/>, <see cref="HeadVersion"/> and <see cref="Age"/>.
///         </description>
///     </item>
///     <item>
///         <description>
///         <see cref="CompletenessFindingKind.StreamDeleted"/> and <see cref="CompletenessFindingKind.StreamTruncated"/>
///         populate <see cref="AggregateId"/>, <see cref="FirstVersion"/>, <see cref="LastVersion"/>,
///         <see cref="HeadVersion"/> and <see cref="Anchor"/>.
///         </description>
///     </item>
///     <item>
///         <description>
///         <see cref="CompletenessFindingKind.RetroactivelyProtected"/> populates <see cref="AggregateId"/>,
///         <see cref="FirstVersion"/>, <see cref="LastVersion"/>, <see cref="HeadVersion"/> and <see cref="Anchor"/>.
///         </description>
///     </item>
///     <item>
///         <description>
///         <see cref="CompletenessFindingKind.Unsealed"/> populates <see cref="AggregateId"/>,
///         <see cref="FirstVersion"/>, <see cref="LastVersion"/> and <see cref="HeadVersion"/>.
///         </description>
///     </item>
///     <item>
///         <description>
///         <see cref="CompletenessFindingKind.MalformedEvent"/> populates whichever of <see cref="AggregateId"/>,
///         <see cref="FirstVersion"/> and <see cref="LastVersion"/> could be read from the malformed event;
///         <see cref="HeadVersion"/> is always unset.
///         </description>
///     </item>
/// </list>
/// </remarks>
public sealed record CompletenessFinding
{
    /// <summary>
    /// Gets, for unanchored events, the age of the oldest of them by <c>CreatedUtc</c>, which the caller supplied, or
    /// <c>null</c> when none of them records one; <c>null</c> for other findings.
    /// </summary>
    public TimeSpan? Age { get; init; }

    /// <summary>
    /// Gets the stream, or <c>null</c> for a broken chain of anchors.
    /// </summary>
    public Guid? AggregateId { get; init; }

    /// <summary>
    /// Gets, for retroactively sealed events, the anchor they are protected since; for a deleted or truncated stream,
    /// the anchor whose leaf shows its highest anchored version.
    /// </summary>
    public AnchorReference? Anchor { get; init; }

    /// <summary>
    /// Gets, for a broken chain of anchors, the result of its verification.
    /// </summary>
    public AnchorChainVerificationResult? AnchorChain { get; init; }

    /// <summary>
    /// Gets the first version the finding concerns, for a deleted or truncated stream the lowest missing one, or
    /// <c>null</c> when it concerns no version.
    /// </summary>
    public Int64? FirstVersion { get; init; }

    /// <summary>
    /// Gets the stream's current head version, or <c>0</c> when no event is left; <c>null</c> for a broken chain of
    /// anchors and a malformed event.
    /// </summary>
    public Int64? HeadVersion { get; init; }

    /// <summary>
    /// Gets the kind of finding.
    /// </summary>
    public required CompletenessFindingKind Kind { get; init; }

    /// <summary>
    /// Gets the last version the finding concerns; for a deleted or truncated stream, the highest anchored version.
    /// </summary>
    public Int64? LastVersion { get; init; }
}
