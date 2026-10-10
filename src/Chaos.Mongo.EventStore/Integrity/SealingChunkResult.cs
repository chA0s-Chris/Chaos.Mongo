// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// The outcome of sealing one chunk of a stream.
/// </summary>
/// <param name="SealedCount">The number of events sealed by the chunk.</param>
/// <param name="LastSealedVersion">The last sealed version of the stream after the chunk.</param>
/// <param name="LastSealedHash">
/// The hash of <paramref name="LastSealedVersion"/>, or the genesis value for an empty
/// stream.
/// </param>
/// <param name="HasMore">Whether unsealed events remain after the chunk.</param>
internal sealed record SealingChunkResult(Int32 SealedCount, Int64 LastSealedVersion, Byte[] LastSealedHash, Boolean HasMore);
