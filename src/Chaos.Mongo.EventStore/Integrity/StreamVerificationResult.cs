// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// The result of verifying the hash chain of an event stream. Equality compares the verdict: the first broken version,
/// its failure and the unrecorded erasures, not the per-version details.
/// </summary>
/// <remarks>
/// Records of the integrity API that hold byte arrays or collections, such as <see cref="VerifiedEvent"/>,
/// <see cref="DerivedDataVerificationResult"/>, <see cref="EventAnchorVerificationResult"/> and
/// <see cref="CompletedAnchor"/>, compare them by reference, as records do, so two separately obtained results are not
/// equal; compare hashes and digests with <c>SequenceEqual</c>, and lists of byte arrays or records item by item,
/// field by field, since <c>SequenceEqual</c> on them compares references too.
/// </remarks>
/// <param name="FirstBrokenVersion">The first version whose verification failed, or <c>null</c> when the stream is intact.</param>
/// <param name="Failure">Why the first broken version failed, or <c>null</c> when the stream is intact.</param>
/// <param name="UnrecordedErasures">
/// The erased values that no later erasure event records. They do not break the chain and are only collected for
/// an intact stream.
/// </param>
public sealed record StreamVerificationResult(
    Int64? FirstBrokenVersion,
    StreamVerificationFailure? Failure,
    IReadOnlyList<UnrecordedErasure> UnrecordedErasures)
{
    /// <summary>
    /// Gets a value indicating whether the stream was compared with its leaves in completed anchors and the sealing
    /// anchors were looked up; <c>false</c> when anchors are not configured.
    /// </summary>
    public Boolean AnchorCoverageChecked { get; init; }

    /// <summary>
    /// Gets the integrity data of every version verified before the chain ended or broke, in version order.
    /// </summary>
    public IReadOnlyList<VerifiedEvent> Events { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether the stream's hash chain is intact.
    /// </summary>
    public Boolean IsIntact => FirstBrokenVersion is null;

    /// <summary>
    /// Gets the versions at which the seal mode changes from <see cref="IntegritySealMode.Append"/> to
    /// <see cref="IntegritySealMode.Retroactive"/>. They are informational: a legitimate gap in protection and a suffix
    /// re-sealed after tampering look the same until anchors tell them apart. They do not affect <see cref="IsIntact"/>.
    /// </summary>
    public IReadOnlyList<Int64> SealModeTransitions { get; init; } = [];

    /// <summary>
    /// Gets a result describing an intact stream without unrecorded erasures.
    /// </summary>
    internal static StreamVerificationResult Intact { get; } = new(null, null, []);

    /// <summary>
    /// Gets a hash code of the verdict, consistent with <see cref="Equals(StreamVerificationResult)"/>.
    /// </summary>
    /// <returns>The hash code.</returns>
    public override Int32 GetHashCode()
        => HashCode.Combine(FirstBrokenVersion, Failure, UnrecordedErasures.Count);

    /// <summary>
    /// Creates a result describing a broken stream.
    /// </summary>
    /// <param name="version">The first version whose verification failed.</param>
    /// <param name="failure">Why the version failed.</param>
    /// <returns>The result.</returns>
    internal static StreamVerificationResult Broken(Int64 version, StreamVerificationFailure failure)
        => new(version, failure, []);

    /// <summary>
    /// Creates a result describing an intact stream with the given unrecorded erasures.
    /// </summary>
    /// <param name="unrecordedErasures">The erased values that no later erasure event records.</param>
    /// <returns>The result.</returns>
    internal static StreamVerificationResult IntactWith(IReadOnlyList<UnrecordedErasure> unrecordedErasures)
    {
        ArgumentNullException.ThrowIfNull(unrecordedErasures);
        return unrecordedErasures.Count == 0 ? Intact : new StreamVerificationResult(null, null, unrecordedErasures);
    }

    /// <summary>
    /// Compares the verdict: the first broken version, its failure and the unrecorded erasures, not the per-version
    /// details.
    /// </summary>
    /// <param name="other">The result to compare with.</param>
    /// <returns><c>true</c> when both results reach the same verdict.</returns>
    public Boolean Equals(StreamVerificationResult? other)
        => other is not null &&
           FirstBrokenVersion == other.FirstBrokenVersion &&
           Failure == other.Failure &&
           UnrecordedErasures.SequenceEqual(other.UnrecordedErasures);
}
