// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore;

/// <summary>
/// Composite identifier for checkpoint documents, combining aggregate ID and version.
/// Used as the MongoDB <c>_id</c> field for checkpoint storage.
/// </summary>
public readonly record struct CheckpointId(Guid AggregateId, Int64 Version);
