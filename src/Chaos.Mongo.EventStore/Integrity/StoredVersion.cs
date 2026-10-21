// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using MongoDB.Bson;

/// <summary>
/// Reads stored version numbers without throwing on tampered values.
/// </summary>
internal static class StoredVersion
{
    /// <summary>
    /// Reads a stored version. Only 32-bit and 64-bit integers and whole doubles within the <see cref="Int64"/> range
    /// are versions; every other BSON type, including Decimal128, is not.
    /// </summary>
    /// <param name="value">The stored value.</param>
    /// <param name="version">The version when the value is one.</param>
    /// <returns><c>true</c> when the value is a version; otherwise <c>false</c>.</returns>
    public static Boolean TryRead(BsonValue value, out Int64 version)
    {
        // 2^63, the first double above Int64.MaxValue.
        const Double int64Limit = 9223372036854775808d;

        switch (value)
        {
            case BsonInt32 int32:
                version = int32.Value;
                return true;
            case BsonInt64 int64:
                version = int64.Value;
                return true;
            case BsonDouble { Value: var number } when Double.IsInteger(number) && number >= -int64Limit && number < int64Limit:
                version = (Int64)number;
                return true;
            default:
                version = 0;
                return false;
        }
    }
}
