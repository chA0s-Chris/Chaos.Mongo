// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// An anchor known from outside the database, for example a copy the completion hook stored in WORM storage, that the
/// stored chain of anchors must contain unchanged.
/// </summary>
/// <remarks>
/// Records compare byte arrays by reference, so compare digests with <c>SequenceEqual</c>.
/// </remarks>
public sealed record TrustedAnchorHead
{
    /// <summary>
    /// Gets the anchor's digest.
    /// </summary>
    public required Byte[] AnchorDigest { get; init; }

    /// <summary>
    /// Gets the anchor's sequence number.
    /// </summary>
    public required Int64 Sequence { get; init; }
}
