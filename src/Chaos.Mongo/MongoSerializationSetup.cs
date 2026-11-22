// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo;

using Chaos.Mongo.Migrations;
using Chaos.Mongo.Queues;
using MongoDB.Bson.Serialization;

/// <summary>
/// Handles BsonClassMap registration for the library's own persisted types that are not covered by a more
/// specific setup, pinning every member to its member name so a hosting application's global convention pack
/// cannot silently rename a stored field.
/// </summary>
internal static class MongoSerializationSetup
{
    /// <summary>
    /// Serializes <see cref="RegisterClassMaps"/>. Class maps are process-global and checked before they are
    /// registered, so concurrent calls, for example of hosts built in parallel, would otherwise both register the
    /// same class map.
    /// </summary>
    private static readonly Object RegistrationLock = new();

    /// <summary>
    /// Registers BsonClassMaps for <see cref="MongoLockDocument"/>, <see cref="MongoMigrationHistoryItem"/>, and
    /// the non-generic <see cref="MongoQueueItem"/> base class. Each registration is independently guarded, so
    /// calling this method repeatedly, including once per <see cref="MongoBuilder.WithQueue{TPayload}"/> call and
    /// from several threads at once, is safe.
    /// </summary>
    public static void RegisterClassMaps()
    {
        lock (RegistrationLock)
        {
            RegisterMongoLockDocumentClassMap();
            RegisterMongoMigrationHistoryItemClassMap();
            RegisterMongoQueueItemClassMap();
        }
    }

    /// <summary>
    /// Registers the BsonClassMap for the closed generic <see cref="MongoQueueItem{TPayload}"/>, pinning its own
    /// members (<see cref="MongoQueueItem{TPayload}.Payload"/>, <see cref="MongoQueueItem{TPayload}.PayloadType"/>)
    /// to their member names. One class map is registered per closed generic payload type; calling this method
    /// repeatedly and from several threads at once is safe.
    /// </summary>
    /// <typeparam name="TPayload">The payload type of the queue.</typeparam>
    public static void RegisterQueueItemClassMap<TPayload>()
        where TPayload : class
    {
        lock (RegistrationLock)
        {
            if (BsonClassMap.IsClassMapRegistered(typeof(MongoQueueItem<TPayload>)))
            {
                return;
            }

            BsonClassMap.RegisterClassMap<MongoQueueItem<TPayload>>(cm =>
            {
                cm.AutoMap();
                cm.GetMemberMap(i => i.Payload).SetElementName(nameof(MongoQueueItem<TPayload>.Payload));
                cm.GetMemberMap(i => i.PayloadType).SetElementName(nameof(MongoQueueItem<TPayload>.PayloadType));
            });
        }
    }

    private static void RegisterMongoLockDocumentClassMap()
    {
        if (BsonClassMap.IsClassMapRegistered(typeof(MongoLockDocument)))
        {
            return;
        }

        BsonClassMap.RegisterClassMap<MongoLockDocument>(cm =>
        {
            cm.AutoMap();
            cm.GetMemberMap(d => d.AcquisitionToken).SetElementName(nameof(MongoLockDocument.AcquisitionToken));
            cm.GetMemberMap(d => d.FenceCounter).SetElementName(nameof(MongoLockDocument.FenceCounter));
            cm.GetMemberMap(d => d.Holder).SetElementName(nameof(MongoLockDocument.Holder));
            cm.GetMemberMap(d => d.LeaseUntilUtc).SetElementName(nameof(MongoLockDocument.LeaseUntilUtc));
        });
    }

    private static void RegisterMongoMigrationHistoryItemClassMap()
    {
        if (BsonClassMap.IsClassMapRegistered(typeof(MongoMigrationHistoryItem)))
        {
            return;
        }

        BsonClassMap.RegisterClassMap<MongoMigrationHistoryItem>(cm =>
        {
            cm.AutoMap();
            cm.GetMemberMap(i => i.AppliedUtc).SetElementName(nameof(MongoMigrationHistoryItem.AppliedUtc));
            cm.GetMemberMap(i => i.Description).SetElementName(nameof(MongoMigrationHistoryItem.Description));
            cm.GetMemberMap(i => i.DurationMs).SetElementName(nameof(MongoMigrationHistoryItem.DurationMs));
        });
    }

    private static void RegisterMongoQueueItemClassMap()
    {
        if (BsonClassMap.IsClassMapRegistered(typeof(MongoQueueItem)))
        {
            return;
        }

        // MongoQueueItem.Id carries no [BsonId] attribute; AutoMap() still recognizes a member literally named
        // "Id" as the id member, independent of any element-naming convention, so it needs no explicit mapping.
        BsonClassMap.RegisterClassMap<MongoQueueItem>(cm =>
        {
            cm.AutoMap();
            cm.GetMemberMap(i => i.AttemptCount).SetElementName(nameof(MongoQueueItem.AttemptCount));
            cm.GetMemberMap(i => i.ClosedUtc).SetElementName(nameof(MongoQueueItem.ClosedUtc));
            cm.GetMemberMap(i => i.CreatedUtc).SetElementName(nameof(MongoQueueItem.CreatedUtc));
            cm.GetMemberMap(i => i.DeferredUtc).SetElementName(nameof(MongoQueueItem.DeferredUtc));
            cm.GetMemberMap(i => i.IsClosed).SetElementName(nameof(MongoQueueItem.IsClosed));
            cm.GetMemberMap(i => i.IsLocked).SetElementName(nameof(MongoQueueItem.IsLocked));
            cm.GetMemberMap(i => i.IsTerminal).SetElementName(nameof(MongoQueueItem.IsTerminal));
            cm.GetMemberMap(i => i.LockedUtc).SetElementName(nameof(MongoQueueItem.LockedUtc));
        });
    }
}
