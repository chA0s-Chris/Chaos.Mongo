// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// Describes the state of a personal data value recorded in an event's integrity data.
/// </summary>
internal enum PersonalDataStatus
{
    /// <summary>
    /// The value and its salt are present.
    /// </summary>
    Present,

    /// <summary>
    /// The value is erased, and a later erasure event of the stream records the erasure.
    /// </summary>
    ErasedWithRecord,

    /// <summary>
    /// The value is erased, but no later erasure event of the stream records the erasure.
    /// </summary>
    ErasedWithoutRecord
}
