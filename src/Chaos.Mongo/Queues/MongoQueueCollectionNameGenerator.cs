// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Queues;

using Chaos.Mongo.Reflection;
using System.IO.Hashing;
using System.Text;

/// <summary>
/// Provides a generator implementation that creates unique collection names for queues based on payload type hashing.
/// </summary>
/// <remarks>
/// Generated names follow the format: "_Queue.{Hash}.{TypeName}" where the hash ensures uniqueness across namespaces.
/// </remarks>
public sealed class MongoQueueCollectionNameGenerator : IMongoQueueCollectionNameGenerator
{
    private const String NamePrefix = "_Queue.";

    /// <inheritdoc/>
    public String GenerateQueueCollectionName(Type payloadType)
    {
        ArgumentNullException.ThrowIfNull(payloadType);

        var builder = new StringBuilder(NamePrefix);

        var nameToHash = payloadType.IsGenericType && !payloadType.IsGenericTypeDefinition
            ? GenericAwareTypeNameFormatter.FormatGenericAwareName(payloadType, t => t.FullName ?? t.Name)
            : payloadType.FullName;

        if (!String.IsNullOrEmpty(nameToHash))
        {
            var hash = XxHash3.Hash(Encoding.UTF8.GetBytes(nameToHash));
            var hexString = Convert.ToHexString(hash);

            builder.Append(hexString);
            builder.Append('.');
        }

        builder.Append(payloadType.Name);

        return builder.ToString();
    }
}
