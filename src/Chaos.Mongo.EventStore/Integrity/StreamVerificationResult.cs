// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// The result of verifying the hash chain of an event stream.
/// </summary>
/// <param name="FirstBrokenVersion">The first version whose verification failed, or <c>null</c> when the stream is intact.</param>
/// <param name="Failure">Why the first broken version failed, or <c>null</c> when the stream is intact.</param>
/// <param name="UnrecordedErasures">
/// The erased values that no later erasure event records. They do not break the chain and are only collected for
/// an intact stream.
/// </param>
internal sealed record StreamVerificationResult(
    Int64? FirstBrokenVersion,
    StreamVerificationFailure? Failure,
    IReadOnlyList<UnrecordedErasure> UnrecordedErasures)
{
    /// <summary>
    /// Gets a result describing an intact stream without unrecorded erasures.
    /// </summary>
    public static StreamVerificationResult Intact { get; } = new(null, null, []);

    /// <summary>
    /// Gets a value indicating whether the stream's hash chain is intact.
    /// </summary>
    public Boolean IsIntact => FirstBrokenVersion is null;

    /// <summary>
    /// Creates a result describing a broken stream.
    /// </summary>
    /// <param name="version">The first version whose verification failed.</param>
    /// <param name="failure">Why the version failed.</param>
    /// <returns>The result.</returns>
    public static StreamVerificationResult Broken(Int64 version, StreamVerificationFailure failure)
        => new(version, failure, []);

    /// <summary>
    /// Creates a result describing an intact stream with the given unrecorded erasures.
    /// </summary>
    /// <param name="unrecordedErasures">The erased values that no later erasure event records.</param>
    /// <returns>The result.</returns>
    public static StreamVerificationResult IntactWith(IReadOnlyList<UnrecordedErasure> unrecordedErasures)
    {
        ArgumentNullException.ThrowIfNull(unrecordedErasures);
        return unrecordedErasures.Count == 0 ? Intact : new StreamVerificationResult(null, null, unrecordedErasures);
    }

    public override Int32 GetHashCode()
        => HashCode.Combine(FirstBrokenVersion, Failure, UnrecordedErasures.Count);

    public Boolean Equals(StreamVerificationResult? other)
        => other is not null &&
           FirstBrokenVersion == other.FirstBrokenVersion &&
           Failure == other.Failure &&
           UnrecordedErasures.SequenceEqual(other.UnrecordedErasures);
}
