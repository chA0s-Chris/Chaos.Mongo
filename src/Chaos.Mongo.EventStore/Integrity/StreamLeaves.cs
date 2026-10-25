// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// The leaves of a stream in completed anchors, and the lowest version of a stream leaf that cannot be read or whose
/// committed anchor cannot be read.
/// </summary>
/// <param name="Completed">The leaves in completed anchors, ordered by anchor sequence.</param>
/// <param name="FirstUnreadableVersion">
/// The lowest version of an unreadable leaf, 1 when its version cannot be read either, or <c>null</c> when every leaf
/// can be read.
/// </param>
internal sealed record StreamLeaves(IReadOnlyList<CompletedStreamLeaf> Completed, Int64? FirstUnreadableVersion);
