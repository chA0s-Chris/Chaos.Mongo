// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using MongoDB.Bson;

/// <summary>
/// A single-document query of the sealing sweep.
/// </summary>
/// <param name="Filter">The query filter.</param>
/// <param name="Sort">The sort order, which matches the unique <c>(AggregateId, Version)</c> index.</param>
/// <param name="Projection">The projection.</param>
internal sealed record SweepQuery(BsonDocument Filter, BsonDocument Sort, BsonDocument Projection);
