// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using MongoDB.Bson;

/// <summary>
/// Reads stored aggregate identifiers without throwing on tampered values.
/// </summary>
internal static class StoredAggregateId
{
    /// <summary>
    /// Reads a stored aggregate identifier. Only standard-representation UUID binary data is an aggregate
    /// identifier; every other BSON type is not.
    /// </summary>
    /// <param name="value">The stored value.</param>
    /// <param name="aggregateId">The aggregate identifier when the value is one.</param>
    /// <returns><c>true</c> when the value is an aggregate identifier; otherwise <c>false</c>.</returns>
    public static Boolean TryRead(BsonValue value, out Guid aggregateId)
    {
        if (value is BsonBinaryData { SubType: BsonBinarySubType.UuidStandard } binary)
        {
            aggregateId = binary.ToGuid(GuidRepresentation.Standard);
            return true;
        }

        aggregateId = Guid.Empty;
        return false;
    }
}
