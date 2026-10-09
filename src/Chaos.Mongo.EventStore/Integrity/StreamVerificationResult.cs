// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// The result of verifying the hash chain of an event stream.
/// </summary>
/// <param name="FirstBrokenVersion">The first version whose verification failed, or <c>null</c> when the stream is intact.</param>
/// <param name="Failure">Why the first broken version failed, or <c>null</c> when the stream is intact.</param>
internal sealed record StreamVerificationResult(Int64? FirstBrokenVersion, StreamVerificationFailure? Failure)
{
    /// <summary>
    /// Gets a result describing an intact stream.
    /// </summary>
    public static StreamVerificationResult Intact { get; } = new(null, null);

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
        => new(version, failure);
}
