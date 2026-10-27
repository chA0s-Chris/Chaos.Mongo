// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// A leaf of a stream in a completed anchor.
/// </summary>
/// <param name="Leaf">The leaf with its inclusion proof.</param>
/// <param name="Anchor">The completed anchor that holds the leaf.</param>
internal sealed record CompletedStreamLeaf(AnchorLeaf Leaf, AnchorRecord Anchor);
