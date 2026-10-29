// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// Describes how an event was sealed into its stream's hash chain.
/// </summary>
/// <remarks>
/// <see cref="MongoEventStoreSerializationSetup"/> registers an
/// <c>EnumSerializer&lt;IntegritySealMode&gt;(BsonType.String)</c>
/// for <see cref="EventIntegrity.SealMode"/>, so this enum is persisted as a string: its member names (
/// <see cref="Append"/>,
/// <see cref="Retroactive"/>) are the storage contract and must not be renamed. This is independent of
/// <see cref="EventIntegrityChain.EncodeSealMode"/>'s separate, fixed <c>0x01</c>/<c>0x02</c> byte encoding used inside
/// the
/// hash chain itself, which does not change with this enum's member names.
/// </remarks>
public enum IntegritySealMode
{
    /// <summary>
    /// The event was sealed while it was appended.
    /// </summary>
    Append,

    /// <summary>
    /// The event was sealed after it had been stored without integrity data.
    /// </summary>
    Retroactive
}
