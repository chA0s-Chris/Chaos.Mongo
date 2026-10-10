// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// A sealed stream head that an anchor has to cover.
/// </summary>
/// <param name="AggregateType">The aggregate type name, as used for the genesis value of the chain.</param>
/// <param name="AggregateId">The aggregate identifier.</param>
/// <param name="Version">The sealed version.</param>
/// <param name="Hash">The hash of <paramref name="Version"/>.</param>
internal sealed record AnchorCandidate(String AggregateType, Guid AggregateId, Int64 Version, Byte[] Hash);
