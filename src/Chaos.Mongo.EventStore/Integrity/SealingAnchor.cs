// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// The anchor that seals an event, with the leaf of the event's stream in it.
/// </summary>
/// <param name="Anchor">The first completed anchor whose leaf for the stream has a version at least the event's.</param>
/// <param name="Leaf">The leaf of the stream in that anchor, with its inclusion proof.</param>
internal sealed record SealingAnchor(AnchorRecord Anchor, AnchorLeaf Leaf);
