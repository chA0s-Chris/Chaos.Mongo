// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// Identifies an anchor.
/// </summary>
/// <param name="AnchorId">The anchor identifier.</param>
/// <param name="Sequence">The anchor's position in the chain of anchors.</param>
public sealed record AnchorReference(Guid AnchorId, Int64 Sequence);
