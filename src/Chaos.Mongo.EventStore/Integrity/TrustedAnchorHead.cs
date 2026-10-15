// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// An anchor known from outside the database, for example a copy the completion hook stored in WORM storage, that the
/// stored chain of anchors must contain unchanged.
/// </summary>
/// <param name="Sequence">The anchor's sequence number.</param>
/// <param name="AnchorDigest">The anchor's digest.</param>
internal sealed record TrustedAnchorHead(Int64 Sequence, Byte[] AnchorDigest);
