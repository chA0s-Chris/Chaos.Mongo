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
    /// <param name="options">The outbox options containing message type registrations.</param>
    public static void RegisterClassMaps(OutboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Register the OutboxMessage class map
        if (!BsonClassMap.IsClassMapRegistered(typeof(OutboxMessage)))
        {
            BsonClassMap.RegisterClassMap<OutboxMessage>(cm =>
            {
                cm.AutoMap();
                cm.MapIdMember(m => m.Id);
            });
        }

        // Register each payload type so the driver can serialize them to BsonDocument
        foreach (var (payloadType, _) in options.MessageTypes)
        {
            if (BsonClassMap.IsClassMapRegistered(payloadType))
                continue;

            var classMap = new BsonClassMap(payloadType);
            classMap.AutoMap();
            BsonClassMap.RegisterClassMap(classMap);
        }
    }
}
