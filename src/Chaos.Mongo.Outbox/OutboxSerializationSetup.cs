// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox;

using MongoDB.Bson.Serialization;

/// <summary>
/// Handles BsonClassMap registration for outbox message and payload types.
/// </summary>
public static class OutboxSerializationSetup
{
    /// <summary>
    /// Registers BsonClassMaps for <see cref="OutboxMessage"/> and all configured payload types.
    /// </summary>
    /// <param name="messageTypes">The registered message types mapped to their discriminator names.</param>
    public static void RegisterClassMaps(IDictionary<Type, String> messageTypes)
    {
        ArgumentNullException.ThrowIfNull(messageTypes);

        // Register the OutboxMessage class map
        if (!BsonClassMap.IsClassMapRegistered(typeof(OutboxMessage)))
        {
            BsonClassMap.RegisterClassMap<OutboxMessage>(cm =>
            {
                cm.AutoMap();
                cm.SetIgnoreExtraElements(true);
                cm.MapIdMember(m => m.Id);
                cm.GetMemberMap(m => m.CorrelationId).SetIgnoreIfNull(true);
                cm.GetMemberMap(m => m.Error).SetIgnoreIfNull(true);
                cm.GetMemberMap(m => m.FailedUtc).SetIgnoreIfNull(true);
                cm.GetMemberMap(m => m.LockedUtc).SetIgnoreIfNull(true);
                cm.GetMemberMap(m => m.LockId).SetIgnoreIfNull(true);
                cm.GetMemberMap(m => m.NextAttemptUtc).SetIgnoreIfNull(true);
                cm.GetMemberMap(m => m.ProcessedUtc).SetIgnoreIfNull(true);
                cm.GetMemberMap(m => m.RetryCount).SetIgnoreIfDefault(true);
            });
        }

        // Register each payload type so the driver can serialize them to BsonDocument
        foreach (var (payloadType, _) in messageTypes)
        {
            if (BsonClassMap.IsClassMapRegistered(payloadType))
                continue;

            var classMap = new BsonClassMap(payloadType);
            classMap.AutoMap();
            classMap.SetIgnoreExtraElements(true);
            BsonClassMap.RegisterClassMap(classMap);
        }
    }
}
