// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using MongoDB.Bson;

/// <summary>
/// Creates and reads the documents of the pending-anchor collection. They are written as raw documents with
/// fixed element names, so neither class maps nor convention packs change the format.
/// </summary>
internal static class PendingAnchorEntry
{
    private const String AggregateIdElement = "AggregateId";
    private const String AggregateTypeElement = "AggregateType";
    private const String HashElement = "Hash";
    private const String VersionElement = "Version";

    /// <summary>
    /// Creates a pending-anchor entry for a sealed stream head.
    /// </summary>
    /// <param name="aggregateType">The aggregate type name.</param>
    /// <param name="aggregateId">The aggregate identifier.</param>
    /// <param name="version">The sealed version.</param>
    /// <param name="hash">The hash of <paramref name="version"/>.</param>
    /// <returns>The entry with a new identifier.</returns>
    public static BsonDocument Create(String aggregateType, Guid aggregateId, Int64 version, Byte[] hash)
        => new()
        {
            { "_id", ObjectId.GenerateNewId() },
            { AggregateTypeElement, aggregateType },
            { AggregateIdElement, new BsonBinaryData(aggregateId, GuidRepresentation.Standard) },
            { VersionElement, version },
            { HashElement, new BsonBinaryData(hash) }
        };

    /// <summary>
    /// Reads the candidate an entry stands for.
    /// </summary>
    /// <param name="entry">The pending-anchor entry.</param>
    /// <returns>The anchor candidate.</returns>
    public static AnchorCandidate ToCandidate(BsonDocument entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new AnchorCandidate(entry[AggregateTypeElement].AsString,
                                   entry[AggregateIdElement].AsGuid,
                                   entry[VersionElement].ToInt64(),
                                   entry[HashElement].AsByteArray);
    }
}
